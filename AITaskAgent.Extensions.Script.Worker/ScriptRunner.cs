using AITaskAgent.Script.Protocol;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;
using Timer = System.Threading.Timer;

namespace AITaskAgent.Script.Worker;

/// <summary>What the host asks for: one call of a function of the loaded bundle (the <c>run</c> message).</summary>
/// <remarks>
/// <paramref name="Call"/> is <c>step</c> (a ScriptStep: <paramref name="Function"/> is the step's key), <c>validate</c> (a JavaScript
/// validator: <paramref name="Function"/> is the library and <paramref name="Export"/> the function it exports; <paramref name="InputJson"/> is
/// the payload <c>{ result, keys, step? }</c>) or <c>hook</c> (a JavaScript hook: same, with the payload <c>{ fields, writable, keys }</c>).
/// </remarks>
internal sealed record RunRequest(
    string Name,
    string Function,
    string InputJson,
    int TimeoutMs,
    int HeapMb,
    int StackKb,
    string UsesJson,
    string AllowJson,
    int MaxCalls,
    int MaxCallBytes,
    string Call = "step",
    string? Export = null);

/// <summary>
/// Result of a call: the script's JSON text (and the step definitions it queued), or an error message the host reports
/// as is. <paramref name="Recycle"/>: the call broke a limit and this worker must not serve another one.
/// </summary>
internal readonly record struct RunOutcome(string? Json, string? Error, string? NextStepsJson = null, bool Recycle = false);

/// <summary>
/// Compiles the JavaScript of a run once (<see cref="Load"/>) and runs each call in a fresh V8 context of the same
/// runtime (<see cref="LoadedBundle.Run"/>). Same contract and error texts as the one-process-per-execution worker had;
/// what changed is that the compiled code is shared by the calls of the run and the globals are not.
/// </summary>
internal static class ScriptRunner
{
    private const double HeapExpansionMultiplier = 2;
    private const int StackHeadroomKb = 1024;
    private const int MaxLogLength = 8192;

    // Runs once per call, in the call's fresh context. Captures the host delegates in a closure, builds the only globals the script gets
    // besides ECMAScript itself (log, console, require, host and the native library; context and nextStep for a step) and leaves
    // no reference to the host. The bundle compiled at load time already ran in this context and left __bundle. `kind` is the call:
    // 'step' (a ScriptStep's function), 'validate' (a JavaScript validator: a function exported by a library, called with the facades
    // built below), 'hook' (a JavaScript hook: the same, with the facades of the hook's arguments) or 'list' (the load-time listing of the
    // functions a library exports).
    private const string Bootstrap = """
        (function (hostLog, hostGet, hostSet, hostCall, nHash, nB64e, nB64d, nUuid, nDateFmt, nDateParse,
                   allowJson, maxCalls, maxCallBytes, bundle, stepKey, usesJson, kind) {
          var freeze = Object.freeze;
          var has = Object.prototype.hasOwnProperty;
          // This call's function and only the libraries it declared (in 'uses' for a step; its library and what that requires for a validator).
          var stepFn = kind === 'step' ? bundle.steps[stepKey] : undefined;
          var libs = {};
          JSON.parse(usesJson).forEach(function (n) { if (has.call(bundle.libs, n)) libs[n] = bundle.libs[n]; });
          var fmt = function (a) {
            if (typeof a === 'string') return a;
            try { var s = JSON.stringify(a); return s === undefined ? String(a) : s; } catch (e) { return String(a); }
          };
          var writer = function (level) {
            return function () {
              var parts = [];
              for (var i = 0; i < arguments.length; i++) parts.push(fmt(arguments[i]));
              hostLog(level, parts.join(' '));
            };
          };
          globalThis.log = writer('info');
          globalThis.console = freeze({
            log: writer('info'), info: writer('info'), warn: writer('warn'), error: writer('error'), debug: writer('debug')
          });
          if (kind === 'step') {
            globalThis.context = freeze({
              get: function (key) { var s = hostGet(String(key)); return s == null ? undefined : JSON.parse(s); },
              set: function (key, value) { hostSet(String(key), value === undefined ? 'null' : JSON.stringify(value)); }
            });
          }

          // Native library: pure functions implemented in the worker.
          globalThis.hash = freeze({
            sha256: function (t) { return nHash('sha256', String(t)); },
            sha1: function (t) { return nHash('sha1', String(t)); },
            md5: function (t) { return nHash('md5', String(t)); }
          });
          globalThis.base64 = freeze({
            encode: function (t) { return nB64e(String(t)); },
            decode: function (t) { return nB64d(String(t)); }
          });
          globalThis.uuid = freeze({ v4: function () { return nUuid(); } });
          globalThis.date = freeze({
            format: function (v, f, tz) { return nDateFmt(String(v), f == null ? '' : String(f), tz == null ? '' : String(tz)); },
            parse: function (t, f, tz) { return nDateParse(String(t), f == null ? '' : String(f), tz == null ? '' : String(tz)); }
          });
          var slashes = function (p) { return String(p).replace(/\\/g, '/'); };
          var trimEnd = function (p) { return slashes(p).replace(/\/+$/, ''); };
          var normalize = function (p) {
            var s0 = slashes(p), abs = s0.charAt(0) === '/', out = [], parts = s0.split('/');
            for (var i = 0; i < parts.length; i++) {
              var s = parts[i];
              if (s === '' || s === '.') continue;
              if (s === '..') { if (out.length && out[out.length - 1] !== '..') out.pop(); else if (!abs) out.push('..'); }
              else out.push(s);
            }
            var r = (abs ? '/' : '') + out.join('/');
            return r === '' ? (abs ? '/' : '.') : r;
          };
          globalThis.path = freeze({
            normalize: function (p) { return normalize(p); },
            join: function () {
              var a = [];
              for (var i = 0; i < arguments.length; i++) if (arguments[i] !== '') a.push(String(arguments[i]));
              return a.length ? normalize(a.join('/')) : '.';
            },
            basename: function (p) { var s = trimEnd(p); return s.substring(s.lastIndexOf('/') + 1); },
            dirname: function (p) { var s = trimEnd(p), i = s.lastIndexOf('/'); return i < 0 ? '.' : (i === 0 ? '/' : s.substring(0, i)); },
            extname: function (p) {
              var b = trimEnd(p); b = b.substring(b.lastIndexOf('/') + 1);
              var i = b.lastIndexOf('.'); return i <= 0 ? '' : b.substring(i);
            }
          });

          // require: closed over the libraries the call declared.
          var cache = {};
          var req = function require(name) {
            name = String(name);
            if (has.call(cache, name)) return cache[name].exports;
            if (!has.call(libs, name)) throw new Error("module '" + name + "' is not declared in 'uses'.");
            var module = { exports: {} };
            cache[name] = module;
            libs[name].call(module.exports, module, module.exports, req);
            return module.exports;
          };
          globalThis.require = freeze(req);

          // host.<ns>.<fn>: only what the call may use; each call is one round trip, counted on both sides.
          var allow = JSON.parse(allowJson), calls = 0, bytes = 0, host = {};
          var call = function (fn, args) {
            if (++calls > maxCalls) throw new Error('the script exceeded its budget of ' + maxCalls + ' host calls.');
            var a = args === undefined ? 'null' : JSON.stringify(args);
            bytes += a.length;
            if (bytes > maxCallBytes) throw new Error('the script exceeded its budget of ' + maxCallBytes + ' bytes exchanged with host functions.');
            var reply = JSON.parse(hostCall(fn, a));
            if (!reply.ok) throw new Error(reply.error || ('host function ' + fn + ' failed.'));
            if (reply.json != null) bytes += reply.json.length;
            return reply.json == null ? undefined : JSON.parse(reply.json);
          };
          allow.forEach(function (fn) {
            var dot = fn.indexOf('.'), ns = fn.substring(0, dot), name = fn.substring(dot + 1);
            if (!has.call(host, ns)) host[ns] = {};
            host[ns][name] = function (args) { return call(fn, args); };
          });
          Object.keys(host).forEach(function (k) { freeze(host[k]); });
          globalThis.host = freeze(host);

          if (kind === 'step') {
            // nextStep: step definitions the host builds after the script succeeds.
            var queue = [];
            globalThis.nextStep = freeze(function nextStep(definition) {
              if (definition === null || typeof definition !== 'object' || Array.isArray(definition))
                throw new Error('nextStep takes a step definition object: { type, id, ... }.');
              if (queue.length >= MAX_NEXT_STEPS) throw new Error('a script may queue at most MAX_NEXT_STEPS next steps.');
              queue.push(JSON.parse(JSON.stringify(definition)));
            });
            Object.defineProperty(globalThis, '__readNext', {
              value: function () { return queue.length ? JSON.stringify(queue) : null; }
            });
            Object.defineProperty(globalThis, '__entry', {
              value: function (inputJson) { return stepFn(JSON.parse(inputJson), globalThis.context); }
            });
          }

          // The facades of a validator (architecture §18.7) and of a hook (§18.8): Proxy objects over plain JSON values of this context. Reading
          // is plain property access; a read-only facade throws on any write, naming the property; a writable one (args.vars, and a hook's
          // writable members such as args.result or args.message) applies the write to its own copy and records it as a patch the host applies
          // after the call (to the pipeline variables; to the hook's real arguments). Values written are JSON-cloned, so the tree never shares
          // objects; a write under an array records the outermost array whole (indices move); a write into an object that was replaced or
          // deleted is not recorded (it is no longer reachable).
          var makeArgs = function (payload) {
            var sinks = { vars: { ops: [], dirty: {} }, fields: { ops: [], dirty: {} } };
            var proxies = new WeakMap(), detached = new WeakSet();
            var isObj = function (v) { return v !== null && typeof v === 'object'; };
            var label = function (root, path) {
              var s = root;
              for (var i = 0; i < path.length; i++) s += typeof path[i] === 'number' ? '[' + path[i] + ']' : '.' + String(path[i]);
              return s;
            };
            var plain = function (v, where) {
              var s = JSON.stringify(v);
              if (s === undefined) throw new TypeError(where + ' only takes JSON values (objects, arrays, strings, numbers, booleans, null).');
              return JSON.parse(s);
            };
            var keyOf = function (t, k) { return Array.isArray(t) && /^(0|[1-9][0-9]*)$/.test(k) ? Number(k) : k; };
            var reachable = function (chain) {
              for (var i = 0; i < chain.length; i++) if (detached.has(chain[i])) return false;
              return true;
            };
            var detach = function (old) { if (isObj(old)) detached.add(old); };
            var touchArray = function (sink, node) {
              var key = JSON.stringify(node.path);
              if (has.call(sink.dirty, key)) sink.ops[sink.dirty[key]] = null;
              sink.dirty[key] = sink.ops.length;
              sink.ops.push({ op: 'set', path: node.path, ref: node.target });
            };
            var record = function (ctx, op, k, value) {
              if (!reachable(ctx.chain)) return;
              if (ctx.array) { touchArray(ctx.sink, ctx.array); return; }
              var path = ctx.path.concat([k]);
              ctx.sink.ops.push(op === 'set' ? { op: 'set', path: path, ref: value } : { op: 'delete', path: path });
            };
            var child = function (ctx, t, k, v) {
              var path = ctx.path.concat([keyOf(t, k)]);
              return {
                root: ctx.root, path: path, chain: ctx.chain.concat([v]), writable: ctx.writable, sink: ctx.sink,
                array: ctx.array || (Array.isArray(v) ? { path: path, target: v } : null)
              };
            };
            var handler;
            var wrap = function (v, ctx) {
              if (!isObj(v)) return v;
              var p = proxies.get(v);
              if (p) return p;
              p = new Proxy(v, handler(ctx));
              proxies.set(v, p);
              return p;
            };
            var refuse = function (ctx, k) {
              return new TypeError(label(ctx.root, k === undefined ? ctx.path : ctx.path.concat([k])) + ' is read-only.');
            };
            handler = function (ctx) {
              var traps = {
                get: function (t, k, r) {
                  if (typeof k === 'symbol' || !has.call(t, k)) return Reflect.get(t, k, r);
                  var v = t[k];
                  return isObj(v) ? wrap(v, child(ctx, t, k, v)) : v;
                },
                set: function (t, k, v) {
                  if (!ctx.writable) throw refuse(ctx, keyOf(t, k));
                  if (typeof k === 'symbol') throw new TypeError(label(ctx.root, ctx.path) + ' only takes text keys.');
                  var value = plain(v, label(ctx.root, ctx.path.concat([keyOf(t, k)])));
                  var old = t[k];
                  t[k] = value;
                  if (old !== value) detach(old);
                  record(ctx, 'set', keyOf(t, k), value);
                  return true;
                },
                deleteProperty: function (t, k) {
                  if (!ctx.writable) throw refuse(ctx, keyOf(t, k));
                  if (!has.call(t, k)) return true;
                  var old = t[k];
                  delete t[k];
                  detach(old);
                  record(ctx, 'delete', keyOf(t, k));
                  return true;
                },
                defineProperty: function (t, k, d) {
                  if (!ctx.writable) throw refuse(ctx, keyOf(t, k));
                  if ('get' in d || 'set' in d) throw new TypeError(label(ctx.root, ctx.path) + ' only takes data properties.');
                  return traps.set(t, k, d.value);
                },
                getOwnPropertyDescriptor: function (t, k) {
                  var d = Reflect.getOwnPropertyDescriptor(t, k);
                  if (d && d.configurable && isObj(d.value)) d.value = wrap(d.value, child(ctx, t, k, d.value));
                  return d;
                },
                setPrototypeOf: function () { throw new TypeError(label(ctx.root, ctx.path) + ' cannot change its prototype.'); },
                preventExtensions: function () { throw new TypeError(label(ctx.root, ctx.path) + ' cannot be frozen, sealed or made non-extensible.'); }
              };
              return traps;
            };

            // args.vars: the pipeline variables, read from the host on first use (only JSON-like ones exist), writable.
            var holder = {}, known = {}, loaded = {};
            (payload.keys || []).forEach(function (k) { known[k] = true; });
            var ensure = function (k) {
              if (typeof k !== 'string' || !has.call(known, k) || has.call(loaded, k)) return;
              loaded[k] = true;
              var s = hostGet(k);
              if (s == null) delete known[k]; else holder[k] = JSON.parse(s);
            };
            var inner = handler({ root: 'args.vars', path: [], chain: [], writable: true, array: null, sink: sinks.vars });
            var vars = new Proxy(holder, {
              get: function (t, k, r) { ensure(k); return inner.get(t, k, r); },
              set: function (t, k, v) {
                ensure(k);
                var done = inner.set(t, k, v);
                if (typeof k === 'string') { known[k] = true; loaded[k] = true; }
                return done;
              },
              deleteProperty: function (t, k) {
                ensure(k);
                if (typeof k === 'string') delete known[k];
                return inner.deleteProperty(t, k);
              },
              defineProperty: function (t, k, d) {
                ensure(k);
                var done = inner.defineProperty(t, k, d);
                if (typeof k === 'string') { known[k] = true; loaded[k] = true; }
                return done;
              },
              has: function (t, k) { return (typeof k === 'string' && has.call(known, k)) || Reflect.has(t, k); },
              ownKeys: function (t) {
                var keys = Reflect.ownKeys(t);
                Object.keys(known).forEach(function (k) { if (keys.indexOf(k) < 0) keys.push(k); });
                return keys;
              },
              getOwnPropertyDescriptor: function (t, k) { ensure(k); return inner.getOwnPropertyDescriptor(t, k); },
              setPrototypeOf: inner.setPrototypeOf,
              preventExtensions: inner.preventExtensions
            });

            var drain = function (sink) {
              var out = [];
              sink.ops.forEach(function (o) {
                if (o) out.push(o.op === 'set' ? { op: 'set', path: o.path, value: o.ref } : { op: 'delete', path: o.path });
              });
              return out;
            };
            var patch = function () { return drain(sinks.vars); };
            var fieldPatch = function () { return drain(sinks.fields); };

            if (payload.fields === undefined) {
              // A validator: args.result (read-only), args.step (a step validator's) and args.vars.
              var result = wrap(payload.result, {
                root: 'args.result', path: [], chain: [payload.result], writable: false, sink: sinks.fields,
                array: Array.isArray(payload.result) ? { path: [], target: payload.result } : null
              });
              var shape = { result: result, vars: vars };
              if (payload.step) shape.step = wrap(payload.step, { root: 'args.step', path: [], chain: [payload.step], writable: false, array: null, sink: sinks.fields });
              var args = new Proxy(freeze(shape), {
                set: function (t, k) { throw new TypeError('args.' + String(k) + ' is read-only.'); },
                deleteProperty: function (t, k) { throw new TypeError('args.' + String(k) + ' is read-only.'); },
                defineProperty: function (t, k) { throw new TypeError('args.' + String(k) + ' is read-only.'); },
                setPrototypeOf: function () { throw new TypeError('args cannot change its prototype.'); }
              });
              return { args: args, patch: patch, fieldPatch: fieldPatch };
            }

            // A hook: one member per field of the arguments' data view (the host's IHookData), read-only unless the payload lists it in
            // 'writable'; a writable member can be changed inside (args.result.total = 1) or replaced (args.message = '...'), and each
            // change is recorded with the member's name first in its path. Plus args.vars.
            var fields = payload.fields, writable = {};
            (payload.writable || []).forEach(function (n) { writable[n] = true; });
            var fieldCtx = function (name, v) {
              return {
                root: 'args', path: [name], chain: [v], writable: has.call(writable, name), sink: sinks.fields,
                array: Array.isArray(v) ? { path: [name], target: v } : null
              };
            };
            var member = function (name) {
              var v = fields[name];
              return isObj(v) ? wrap(v, fieldCtx(name, v)) : v;
            };
            var isField = function (k) { return typeof k === 'string' && has.call(fields, k); };
            var hookArgs = new Proxy({}, {
              get: function (t, k, r) {
                if (k === 'vars') return vars;
                if (isField(k)) return member(k);
                return Reflect.get(t, k, r);
              },
              set: function (t, k, v) {
                if (!isField(k) || !has.call(writable, k)) throw new TypeError('args.' + String(k) + ' is read-only.');
                var value = plain(v, 'args.' + k);
                var old = fields[k];
                fields[k] = value;
                if (old !== value) detach(old);
                sinks.fields.ops.push({ op: 'set', path: [k], ref: value });
                return true;
              },
              deleteProperty: function (t, k) { throw new TypeError('args.' + String(k) + (isField(k) && has.call(writable, k) ? ' cannot be deleted (assign null).' : ' is read-only.')); },
              defineProperty: function (t, k) { throw new TypeError('args.' + String(k) + ' is read-only.'); },
              has: function (t, k) { return k === 'vars' || isField(k) || Reflect.has(t, k); },
              ownKeys: function () { return Object.keys(fields).concat(['vars']); },
              getOwnPropertyDescriptor: function (t, k) {
                if (k === 'vars') return { value: vars, writable: false, enumerable: true, configurable: true };
                if (isField(k)) return { value: member(k), writable: has.call(writable, k), enumerable: true, configurable: true };
                return undefined;
              },
              setPrototypeOf: function () { throw new TypeError('args cannot change its prototype.'); },
              preventExtensions: function () { throw new TypeError('args cannot be frozen, sealed or made non-extensible.'); }
            });
            return { args: hookArgs, patch: patch, fieldPatch: fieldPatch };
          };

          if (kind === 'validate') {
            Object.defineProperty(globalThis, '__validateEntry', {
              value: function (payloadJson, libName, exportName) {
                var lib = req(libName);
                var f = lib == null ? undefined : lib[exportName];
                if (typeof f !== 'function') throw new Error("the library '" + libName + "' has no function '" + exportName + "'.");
                var session = makeArgs(JSON.parse(payloadJson));
                var value = f.call(undefined, session.args);
                var type = value === null ? 'null' : Array.isArray(value) ? 'array' : typeof value;
                var out = { type: type, patch: session.patch() };
                if (type === 'boolean' || type === 'string') out.value = value;
                else if (type === 'object') out.value = { isValid: value.isValid, error: value.error };
                return JSON.stringify(out);
              }
            });
          }

          if (kind === 'hook') {
            // A JavaScript hook (§18.8): returns nothing; what it changed in its writable members and in args.vars comes back as patches.
            Object.defineProperty(globalThis, '__hookEntry', {
              value: function (payloadJson, libName, exportName) {
                var lib = req(libName);
                var f = lib == null ? undefined : lib[exportName];
                if (typeof f !== 'function') throw new Error("the library '" + libName + "' has no function '" + exportName + "'.");
                var session = makeArgs(JSON.parse(payloadJson));
                var value = f.call(undefined, session.args);
                var type = value === null ? 'null' : Array.isArray(value) ? 'array' : typeof value;
                if (type === 'object' && typeof value.then === 'function') type = 'promise';
                return JSON.stringify({ type: type, patch: session.patch(), fields: session.fieldPatch() });
              }
            });
          }

          if (kind === 'list') {
            Object.defineProperty(globalThis, '__listExports', {
              value: function (libName) {
                var lib = req(libName);
                var names = [];
                if (lib !== null && (typeof lib === 'object' || typeof lib === 'function'))
                  Object.keys(lib).forEach(function (k) { if (typeof lib[k] === 'function') names.push(k); });
                return JSON.stringify(names.sort());
              }
            });
          }
        })(__hostLog, __hostGet, __hostSet, __hostCall, __nHash, __nB64e, __nB64d, __nUuid, __nDateFmt, __nDateParse,
           __allow, __maxCalls, __maxCallBytes, globalThis.__bundle, __fn, __uses, __kind);
        delete globalThis.__hostLog; delete globalThis.__hostGet; delete globalThis.__hostSet; delete globalThis.__hostCall;
        delete globalThis.__nHash; delete globalThis.__nB64e; delete globalThis.__nB64d; delete globalThis.__nUuid;
        delete globalThis.__nDateFmt; delete globalThis.__nDateParse; delete globalThis.__allow;
        delete globalThis.__maxCalls; delete globalThis.__maxCallBytes; delete globalThis.__bundle;
        delete globalThis.__fn; delete globalThis.__uses; delete globalThis.__kind;
        """;

    // Checks, in a throwaway context, that every piece is exactly the function it was wrapped in. A script that closes
    // its function early and declares code of its own (another step's function, say) changes the source text of its
    // function or the number of functions, and the load is refused.
    private const string ShapeCheck = """
        (function (expected) {
          var b = globalThis.__bundle, bad = [], seen = { steps: 0, libs: 0 };
          expected.forEach(function (e) {
            var f = b && b[e.kind] ? b[e.kind][e.key] : undefined;
            seen[e.kind]++;
            if (typeof f !== 'function' || Function.prototype.toString.call(f) !== e.text) bad.push(e.id);
          });
          if (!b || Object.keys(b.steps).length !== seen.steps || Object.keys(b.libs).length !== seen.libs) bad.push('*');
          return JSON.stringify(bad);
        })(JSON.parse(__expected))
        """;

    private const string CallScript = "globalThis.__scriptResult = __entry(__input); undefined";

    // A validator: the envelope { type, value?, patch } is already JSON text (built inside the call, under its limits).
    private const string ValidateScript = "globalThis.__scriptResult = __validateEntry(__input, __lib, __export); undefined";

    private const string ValidateResultScript = "globalThis.__scriptResult";

    // A hook: the envelope { type, patch, fields } is JSON text too.
    private const string HookScript = "globalThis.__scriptResult = __hookEntry(__input, __lib, __export); undefined";

    /// <summary>Kind of a call: a ScriptStep's function.</summary>
    public const string StepCall = "step";

    /// <summary>Kind of a call: a JavaScript validator (a function a library exports, called with the facades).</summary>
    public const string ValidateCall = "validate";

    /// <summary>Kind of a call: a JavaScript hook (a function a library exports, called with the facades of the hook's arguments).</summary>
    public const string HookCall = "hook";

    private static readonly string BootstrapText = Bootstrap.Replace("MAX_NEXT_STEPS", ScriptLimits.MaxNextSteps.ToString(CultureInfo.InvariantCulture));

    private const string SerializeScript =
        "(function () { var r = globalThis.__scriptResult; return r === undefined ? null : JSON.stringify(r); })()";

    /// <summary>
    /// Compiles the bundle of <paramref name="piecesJson"/> once, in a V8 runtime sized for <paramref name="heapMb"/>
    /// (the largest heap cap of the run's steps). On a syntax error each piece is compiled on its own, so every error is
    /// reported under its own id and line (<c>[ { id, error? } ]</c>, one entry per piece). With
    /// <paramref name="checkOnly"/> nothing is kept: that is the syntax check of the designer. <paramref name="exportsJson"/> (a JSON array
    /// of library names, or null) asks, once the bundle compiled, for the functions each of those libraries exports: each one is run in a
    /// throwaway context of its own (bounded in time and heap, no host function), and the answer is <c>{ "&lt;lib&gt;": { "functions": [..] } |
    /// { "error": ".." } }</c>.
    /// </summary>
    public static (LoadedBundle? Bundle, JArray Report, string? Error, JObject? Exports) Load(string piecesJson, int heapMb, bool checkOnly, string? exportsJson = null)
    {
        if (heapMb is < ScriptLimits.MinHeapMb or > ScriptLimits.MaxHeapMbCeiling)
        {
            return (null, [], $"'maxHeapMb' must be between {ScriptLimits.MinHeapMb} and {ScriptLimits.MaxHeapMbCeiling} (was {heapMb}).", null);
        }

        ScriptBundle bundle;
        List<string> exports;
        try
        {
            bundle = ScriptBundle.Build(ScriptBundle.FromJson(piecesJson));
            exports = exportsJson == null ? [] : [.. JArray.Parse(exportsJson).Values<string>().OfType<string>()];
        }
        catch (Exception ex)
        {
            return (null, [], $"the scripts could not be read: {ex.Message}", null);
        }

        V8Runtime runtime = CreateRuntime(heapMb);
        bool kept = false;
        try
        {
            V8Script compiled;
            try
            {
                compiled = runtime.Compile(new DocumentInfo(ScriptBundle.DocumentName), bundle.Text);
            }
            catch (ScriptEngineException ex)
            {
                return (null, Diagnose(runtime, bundle, Details(ex)), null, null);
            }

            Dictionary<string, string> shape = CheckShape(runtime, compiled, bundle);
            JObject? listed = shape.Count == 0 && exports.Count > 0 ? ListExports(runtime, compiled, bundle, exports, heapMb) : null;
            if (shape.Count > 0 || checkOnly)
            {
                compiled.Dispose();
                return (null, Report(bundle, shape), null, listed);
            }

            kept = true;
            return (new LoadedBundle(runtime, compiled, bundle), Report(bundle, []), null, listed);
        }
        catch (Exception ex)
        {
            return (null, [], $"the scripts could not be compiled: {ex.Message}", null);
        }
        finally
        {
            if (!kept)
            {
                runtime.Dispose();
            }
        }
    }

    /// <summary>The bundle did not compile: each piece is compiled alone so every error is reported under its own id and line.</summary>
    private static JArray Diagnose(V8Runtime runtime, ScriptBundle bundle, string bundleError)
    {
        Dictionary<string, string> errors = new(StringComparer.Ordinal);
        foreach (BundlePiece piece in bundle.Pieces)
        {
            try
            {
                using V8Script alone = runtime.Compile(new DocumentInfo(piece.Id), ScriptBundle.Wrap(piece));
            }
            catch (ScriptEngineException ex)
            {
                errors.TryAdd(piece.Id, Details(ex));
            }
        }

        if (errors.Count == 0)
        {
            // Every piece compiles alone but not joined (a script that closes its function early): the bundle's own
            // error, under the piece it points at.
            BundlePiece? culprit = ScriptBundle.FirstLine(bundleError) is { } line ? bundle.PieceAt(line) : null;
            errors[culprit?.Id ?? ""] = bundle.MapLocations(bundleError);
        }

        return Report(bundle, errors);
    }

    private static JArray Report(ScriptBundle bundle, Dictionary<string, string> errors)
    {
        JArray report = [];
        foreach (BundlePiece piece in bundle.Pieces)
        {
            JObject entry = new() { ["id"] = piece.Id };
            if (errors.Remove(piece.Id, out string? error))
            {
                entry["error"] = error;
            }

            report.Add(entry);
        }

        if (errors.TryGetValue("", out string? general))
        {
            report.Add(new JObject { ["id"] = "", ["error"] = general });
        }

        return report;
    }

    /// <summary>
    /// Runs the compiled bundle once in a throwaway context (bounded in time) and checks its shape: the errors by piece
    /// id (an empty id when no piece can be named), none when every piece is the function it was wrapped in.
    /// </summary>
    private static Dictionary<string, string> CheckShape(V8Runtime runtime, V8Script compiled, ScriptBundle bundle)
    {
        JArray expected = new(bundle.Pieces.Select(p => new JObject
        {
            ["id"] = p.Id,
            ["kind"] = p.IsLibrary ? "libs" : "steps",
            ["key"] = p.Key,
            ["text"] = ScriptBundle.FunctionText(p),
        }));

        using V8ScriptEngine engine = runtime.CreateScriptEngine(V8ScriptEngineFlags.DisableGlobalMembers);
        using Timer timer = new(_ =>
        {
            try
            {
                engine.Interrupt();
            }
            catch (ObjectDisposedException)
            {
                // Already finished.
            }
        }, null, ScriptLimits.LoadTimeoutMs, System.Threading.Timeout.Infinite);

        try
        {
            engine.Execute(compiled);
            engine.Script.__expected = expected.ToString(Formatting.None);
            string bad = engine.Evaluate(new DocumentInfo("bundle (check)"), ShapeCheck) as string ?? "[]";
            Dictionary<string, string> errors = new(StringComparer.Ordinal);
            foreach (string id in JArray.Parse(bad).Values<string>().OfType<string>())
            {
                errors.TryAdd(id == "*" ? "" : id, id == "*"
                    ? "the scripts declare functions of their own outside their bodies; each script must be a function body."
                    : "the script does not stay inside its function: it closes the function early and declares code of its own. " +
                      "A script must be a function body.");
            }

            if (errors.Count > 1)
            {
                errors.Remove("");
            }

            return errors;
        }
        catch (ScriptInterruptedException)
        {
            return new(StringComparer.Ordinal)
            {
                [""] = $"the scripts did not finish loading within {ScriptLimits.LoadTimeoutMs / 1000} s (code outside the step functions?).",
            };
        }
        catch (ScriptEngineException ex)
        {
            string details = Details(ex);
            BundlePiece? culprit = ScriptBundle.FirstLine(details) is { } line ? bundle.PieceAt(line) : null;
            return new(StringComparer.Ordinal) { [culprit?.Id ?? ""] = bundle.MapLocations(details) };
        }
    }

    /// <summary>
    /// The functions each library of <paramref name="libraries"/> exports: the library runs in a throwaway context of its own with the
    /// globals of a call (no host function, no pipeline variable, logs dropped), under the load time limit and a heap cap of
    /// <paramref name="heapMb"/> above the baseline. A library that is not in the bundle is left out (the host reports it).
    /// </summary>
    private static JObject ListExports(V8Runtime runtime, V8Script compiled, ScriptBundle bundle, IReadOnlyList<string> libraries, int heapMb)
    {
        JObject listed = [];
        string[] all = [.. bundle.Pieces.Where(p => p.IsLibrary).Select(p => p.Key)];
        string uses = new JArray(all).ToString(Formatting.None);
        using V8Script bootstrap = runtime.Compile(new DocumentInfo("bootstrap"), BootstrapText);
        foreach (string library in libraries.Distinct(StringComparer.Ordinal).Where(l => all.Contains(l, StringComparer.Ordinal)))
        {
            runtime.CollectGarbage(true);
            V8RuntimeHeapInfo baseline = runtime.GetHeapInfo();
            runtime.MaxHeapSize = (UIntPtr)(baseline.TotalHeapSize + (ulong)heapMb * 1024 * 1024);
            bool timedOut = false;
            try
            {
                using V8ScriptEngine engine = runtime.CreateScriptEngine(V8ScriptEngineFlags.DisableGlobalMembers);
                using Timer timer = new(_ =>
                {
                    timedOut = true;
                    try
                    {
                        engine.Interrupt();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Already finished.
                    }
                }, null, ScriptLimits.LoadTimeoutMs, System.Threading.Timeout.Infinite);

                engine.Execute(compiled);
                SetGlobals(engine, (_, _) => { }, _ => null, (_, _) => { }, (fn, _) => Refused(fn), "[]", 0, 0, "", uses, "list");
                engine.Execute(bootstrap);
                string names = engine.Evaluate(new DocumentInfo("call"), "__listExports(" + JsonConvert.ToString(library) + ")") as string ?? "[]";
                timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                listed[library] = new JObject { ["functions"] = JArray.Parse(names) };
            }
            catch (ScriptInterruptedException)
            {
                listed[library] = new JObject
                {
                    ["error"] = timedOut
                        ? $"the library did not finish loading within {ScriptLimits.LoadTimeoutMs / 1000} s."
                        : $"the library exceeded the heap limit of {heapMb} MB while loading.",
                };
            }
            catch (ScriptEngineException ex)
            {
                listed[library] = new JObject { ["error"] = bundle.MapLocations(Details(ex)) };
            }
            finally
            {
                runtime.MaxHeapSize = UIntPtr.Zero;
            }
        }

        return listed;

        static string Refused(string function)
            => new JObject { ["ok"] = false, ["error"] = $"host function '{function}' is not available while the libraries load." }.ToString(Formatting.None);
    }

    /// <summary>The globals the bootstrap captures (then deletes): the host delegates, the native library and the call's parameters.</summary>
    private static void SetGlobals(
        V8ScriptEngine engine,
        Action<string, string> log,
        Func<string, string?> get,
        Action<string, string> set,
        Func<string, string, string> call,
        string allowJson,
        int maxCalls,
        int maxCallBytes,
        string function,
        string usesJson,
        string kind)
    {
        engine.Script.__hostLog = log;
        engine.Script.__hostGet = get;
        engine.Script.__hostSet = set;
        engine.Script.__hostCall = call;
        engine.Script.__nHash = new Func<string, string, string>(NativeLibrary.Hash);
        engine.Script.__nB64e = new Func<string, string>(NativeLibrary.Base64Encode);
        engine.Script.__nB64d = new Func<string, string>(NativeLibrary.Base64Decode);
        engine.Script.__nUuid = new Func<string>(NativeLibrary.Uuid);
        engine.Script.__nDateFmt = new Func<string, string, string, string>((v, f, z) => NativeLibrary.DateFormat(v, f, z));
        engine.Script.__nDateParse = new Func<string, string, string, string>((t, f, z) => NativeLibrary.DateParse(t, f, z));
        engine.Script.__allow = allowJson;
        engine.Script.__maxCalls = maxCalls;
        engine.Script.__maxCallBytes = maxCallBytes;
        engine.Script.__fn = function;
        engine.Script.__uses = usesJson;
        engine.Script.__kind = kind;
    }

    private static string Details(ScriptEngineException ex)
        => (string.IsNullOrWhiteSpace(ex.ErrorDetails) ? ex.Message : ex.ErrorDetails).Trim();

    private static V8Runtime CreateRuntime(int heapMb)
    {
        long maxHeapBytes = (long)heapMb * 1024 * 1024;
        V8RuntimeConstraints constraints = new()
        {
            // V8 aborts the whole process (fatal OOM, not catchable) when it hits its heap limit. On-demand
            // expansion raises that limit instead whenever the engine is about to hit it, so a single large
            // allocation (e.g. new Array(3e7).fill(1.5)) completes or fails with a RangeError; the soft cap of each
            // call and the post-call check turn it into an error. Whatever slips past still dies at the process limit.
            MaxOldSpaceSize = heapMb * ScriptLimits.V8OldSpaceFactor,
            HeapExpansionMultiplier = HeapExpansionMultiplier,
            // ArrayBuffer memory lives outside the V8 heap and is invisible to the heap caps: bounded here
            // (an over-limit allocation is a RangeError inside the script). Per runtime: the largest cap of the run.
            MaxArrayBufferAllocation = (ulong)maxHeapBytes,
        };

        // Default HeapSizeViolationPolicy is Interrupt; the soft cap itself is set per call. Types are never exposed.
        return new V8Runtime(constraints) { HeapSizeSampleInterval = TimeSpan.FromMilliseconds(10) };
    }

    /// <summary>The compiled JavaScript of a run and the runtime that holds it. Each call gets a V8 context of its own.</summary>
    internal sealed class LoadedBundle(V8Runtime runtime, V8Script compiled, ScriptBundle bundle) : IDisposable
    {
        private readonly V8Script _bootstrap = runtime.Compile(new DocumentInfo("bootstrap"), BootstrapText);

        private readonly V8Script _call = runtime.Compile(new DocumentInfo("call"), CallScript);
        private readonly V8Script _validate = runtime.Compile(new DocumentInfo("call"), ValidateScript);
        private readonly V8Script _hook = runtime.Compile(new DocumentInfo("call"), HookScript);
        private readonly V8Script _validateResult = runtime.Compile(new DocumentInfo("result"), ValidateResultScript);
        private readonly V8Script _serialize = runtime.Compile(new DocumentInfo("result"), SerializeScript);
        private readonly V8Script _next = runtime.Compile(new DocumentInfo("next steps"), "__readNext()");
        private readonly HashSet<string> _functions = [.. bundle.Pieces.Where(p => !p.IsLibrary).Select(p => p.Key)];
        private readonly HashSet<string> _libraries = [.. bundle.Pieces.Where(p => p.IsLibrary).Select(p => p.Key)];

        /// <summary>Runs one call on a thread whose stack is sized for the requested V8 stack cap.</summary>
        public RunOutcome Run(RunRequest request, WorkerChannel channel)
        {
            string? invalid = Validate(request);
            if (invalid != null)
            {
                return new RunOutcome(null, invalid);
            }

            if (request.Call is ValidateCall or HookCall)
            {
                if (!_libraries.Contains(request.Function) || string.IsNullOrEmpty(request.Export))
                {
                    return new RunOutcome(null, $"the library '{request.Function}' is not in the scripts loaded for this run.");
                }
            }
            else if (request.Call != StepCall)
            {
                return new RunOutcome(null, $"unknown kind of call '{request.Call}'.");
            }
            else if (!_functions.Contains(request.Function))
            {
                return new RunOutcome(null, $"the function '{request.Function}' is not in the scripts loaded for this run.");
            }

            RunOutcome outcome = new(null, "the script worker ended without a result.", Recycle: true);
            Thread thread = new(
                () => outcome = RunCore(request, channel),
                maxStackSize: (request.StackKb + StackHeadroomKb) * 1024);
            thread.Start();
            thread.Join();
            return outcome;
        }

        private RunOutcome RunCore(RunRequest request, WorkerChannel channel)
        {
            long maxHeapBytes = (long)request.HeapMb * 1024 * 1024;

            // What earlier calls left is garbage once their contexts are gone: collect it, so this call is measured
            // against the compiled code and nothing else. The caps are relative to that baseline: V8 keeps the memory it
            // committed for earlier calls (it does not shrink at once), and ClearScript's soft cap counts committed
            // memory, so an absolute cap would trip on what earlier calls committed, not on this one.
            runtime.CollectGarbage(true);
            V8RuntimeHeapInfo baseline = runtime.GetHeapInfo();
            runtime.MaxStackUsage = (UIntPtr)(uint)(request.StackKb * 1024);
            try
            {
                runtime.MaxHeapSize = (UIntPtr)(baseline.TotalHeapSize + (ulong)maxHeapBytes);
                return RunInContext(request, channel, maxHeapBytes, baseline);
            }
            finally
            {
                // Monitoring off between calls. Setting a new value is also what lifts the block ClearScript puts on the
                // runtime after a violation, so a violation sampled late cannot leak into the next call.
                runtime.MaxHeapSize = UIntPtr.Zero;
            }
        }

        private RunOutcome RunInContext(RunRequest request, WorkerChannel channel, long maxHeapBytes, V8RuntimeHeapInfo baseline)
        {
            bool timedOut = false;
            V8ScriptEngine engine;
            try
            {
                engine = runtime.CreateScriptEngine(V8ScriptEngineFlags.DisableGlobalMembers);
            }
            catch (Exception ex)
            {
                return new RunOutcome(null, $"the script engine could not be initialized: {ex.Message}", Recycle: true);
            }

            using V8ScriptEngine owned = engine;
            using Timer timer = new(_ =>
            {
                timedOut = true;
                try
                {
                    engine.Interrupt();
                }
                catch (ObjectDisposedException)
                {
                    // The call already finished and the engine is gone: nothing to stop.
                }
            }, null, TimeSpan.FromMilliseconds(request.TimeoutMs), System.Threading.Timeout.InfiniteTimeSpan);

            try
            {
                // The bundle's functions are created in this context from the code compiled at load time (not parsed again).
                engine.Execute(compiled);
                SetGlobals(
                    engine,
                    (level, message) => WriteLog(channel, level, message),
                    key => GetVariable(channel, key),
                    (key, json) => channel.Send(Msg.Set, ("key", key), ("json", json)),
                    (fn, args) => CallHost(channel, fn, args),
                    request.AllowJson, request.MaxCalls, request.MaxCallBytes, request.Function, request.UsesJson, request.Call);
                engine.Execute(_bootstrap);
                engine.Script.__input = request.InputJson;
                if (request.Call is ValidateCall or HookCall)
                {
                    engine.Script.__lib = request.Function;
                    engine.Script.__export = request.Export;
                }
            }
            catch (ScriptInterruptedException)
            {
                return new RunOutcome(null, $"the script exceeded the time limit of {request.TimeoutMs} ms.", Recycle: true);
            }
            catch (ScriptEngineException ex)
            {
                return new RunOutcome(null, $"script error: {bundle.MapLocations(Details(ex))}");
            }
            catch (Exception ex)
            {
                return new RunOutcome(null, $"the script engine could not be initialized: {ex.Message}", Recycle: true);
            }

            // Two evaluations: the step's function first (its result parked in a global), then serialization. The
            // heap is checked in between, while what the script allocated is still measurable (see below).
            try
            {
                bool validate = request.Call is ValidateCall or HookCall;
                engine.Execute(request.Call == HookCall ? _hook : validate ? _validate : _call);

                // The soft cap is enforced through V8 interrupts, which straight-line code made of large builtin
                // calls (new Array(n).fill(...)) never reaches: check the heap before accepting the result.
                // V8 only raises its own limit (on-demand expansion) when the heap got close to it, i.e. several
                // times the cap: that is a violation even if the garbage has been collected since.
                V8RuntimeHeapInfo heap = runtime.GetHeapInfo();
                if (heap.UsedHeapSize > baseline.UsedHeapSize + (ulong)maxHeapBytes || heap.HeapSizeLimit > baseline.HeapSizeLimit)
                {
                    return new RunOutcome(null, $"the script exceeded the heap limit of {request.HeapMb} MB.", Recycle: true);
                }

                object? result = engine.Evaluate(validate ? _validateResult : _serialize);
                object? next = validate ? null : engine.Evaluate(_next);
                timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                return new RunOutcome(result is string json ? json : "null", null, next as string);
            }
            catch (ScriptInterruptedException)
            {
                string reason = timedOut
                    ? $"the script exceeded the time limit of {request.TimeoutMs} ms."
                    : $"the script was interrupted (heap limit of {request.HeapMb} MB exceeded?).";
                return new RunOutcome(null, reason, Recycle: true);
            }
            catch (ScriptEngineException ex)
            {
                return new RunOutcome(null, $"script error: {bundle.MapLocations(Details(ex))}");
            }
            catch (Exception ex)
            {
                return new RunOutcome(null, $"unexpected error running the script: {ex.Message}", Recycle: true);
            }
        }

        public void Dispose()
        {
            _bootstrap.Dispose();
            _call.Dispose();
            _validate.Dispose();
            _hook.Dispose();
            _validateResult.Dispose();
            _serialize.Dispose();
            _next.Dispose();
            compiled.Dispose();
            runtime.Dispose();
        }
    }

    private static string? Validate(RunRequest request)
    {
        if (request.HeapMb is < ScriptLimits.MinHeapMb or > ScriptLimits.MaxHeapMbCeiling)
        {
            return $"'maxHeapMb' must be between {ScriptLimits.MinHeapMb} and {ScriptLimits.MaxHeapMbCeiling} (was {request.HeapMb}).";
        }

        if (request.StackKb is < ScriptLimits.MinStackKb or > ScriptLimits.MaxStackKbCeiling)
        {
            return $"'maxStackKb' must be between {ScriptLimits.MinStackKb} and {ScriptLimits.MaxStackKbCeiling} (was {request.StackKb}).";
        }

        if (request.MaxCalls is < 0 or > ScriptLimits.MaxCallsCeiling)
        {
            return $"'maxCalls' must be between 0 and {ScriptLimits.MaxCallsCeiling} (was {request.MaxCalls}).";
        }

        if (request.MaxCallBytes is < 0 or > ScriptLimits.MaxCallBytesCeiling)
        {
            return $"'maxCallKb' must be at most {ScriptLimits.MaxCallBytesCeiling / 1024} (was {request.MaxCallBytes / 1024}).";
        }

        return request.TimeoutMs <= 0 ? $"'timeoutMs' must be positive (was {request.TimeoutMs})." : null;
    }

    private static void WriteLog(WorkerChannel channel, string level, string message)
    {
        if (message.Length > MaxLogLength)
        {
            message = message[..MaxLogLength] + "...[truncated]";
        }

        channel.Send(Msg.Log, ("level", level), ("text", message));
    }

    /// <summary>Asks the host for a pipeline variable and waits for its answer: JSON text, or null when absent.</summary>
    private static string? GetVariable(WorkerChannel channel, string key)
    {
        channel.Send(Msg.Get, ("key", key));
        JObject reply = channel.Read()
            ?? throw new InvalidOperationException("the host closed the channel while the script was reading a variable.");
        return reply.Value<string>(Msg.Kind) == Msg.Value ? reply.Value<string>("json") : null;
    }

    /// <summary>Asks the host to run a function and waits for its answer, returned to the bootstrap as JSON text <c>{ ok, json, error }</c>.</summary>
    private static string CallHost(WorkerChannel channel, string function, string argsJson)
    {
        channel.Send(Msg.Call, ("fn", function), ("args", argsJson));
        JObject reply = channel.Read()
            ?? throw new InvalidOperationException("the host closed the channel while the script was calling a host function.");
        return new JObject
        {
            ["ok"] = reply.Value<bool>("ok"),
            ["json"] = reply.Value<string>("json"),
            ["error"] = reply.Value<string>("error"),
        }.ToString(Formatting.None);
    }
}
