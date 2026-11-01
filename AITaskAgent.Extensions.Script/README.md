# AITaskAgent.Extensions.Script

`ScriptStep`: JavaScript declarable in YAML (`type: ScriptStep`), without an LLM. **One worker process per pipeline run**:
when the run starts, the scripts of all its ScriptSteps are compiled once (BRMS style) and each step is a call of its own
function, in a fresh V8 context, with time and memory limits per call. Grammar and example in the
[YAML guide](../docs/guides/yaml-pipelines.md#scriptstep-javascript-declarable); architecture in
[ARCHITECTURE-v2](../docs/ARCHITECTURE-v2.md#scriptstep-one-process-per-run).

```csharp
services.AddAITaskAgentCore().AddScriptExtension();
```

```yaml
- id: totals
  type: ScriptStep
  timeoutMs: 2000      # 5000 by default
  maxHeapMb: 32        # 64 by default, from 8 to 128
  maxStackKb: 256      # 512 by default, from 64 to 2048
  script: |
    return { total: input.lines.reduce((s, l) => s + l.qty * l.unitPrice, 0) };
```

The text of `script` is the body of `function (input, context)`; the value of the `return` is the result. `context.get/set`
read and write the pipeline variables (JSON only), and `log`/`console.*` write to the step's `ILogger`.

## Extending a script

Everything is closed by default; the step declares what it uses and the host decides what it grants.

| Property | Effect |
|---|---|
| `uses: [name]` | `require('name')` opens that library of the host's `IScriptCatalog` (`services.AddScriptLibrary(new ScriptLibrary(name, source))`); any other name throws. |
| `allow: [ns.fn]` | `host.ns.fn(args)` exists, **only if the host also grants it** (`services.ConfigureScriptHost(new ScriptHostOptions { GrantedFunctions = [...] })`; empty by default). Built-ins: `config.get`, `file.readText`, `file.exists` (the file ones under `FileRoots`). Your own: `services.AddScriptHostFunction(new DelegateHostFunction("ns.fn", handler))`. |
| `maxCalls`, `maxCallKb` | Calls and kilobytes exchanged with host functions per run (100 / 1024; ceilings 10 000 / 8192). |

Without a registration the script also has a small native library (`hash`, `base64`, `uuid`, `date`, `path`) and `nextStep({ type, id, ... })`, which
queues steps the host builds with the step registry and returns as `NextSteps`. `ScriptChecks.Check` (names against the catalog and the grant, hints for
`host.x.y(` and `require('x')` not declared) and `ScriptSyntax.CheckAsync` (compiles scripts and libraries in one worker, runs nothing, joined in the same
bundle a run compiles, so validation and run agree) are the static checks. The worker and the host check the protocol version (`ready.proto`, `load.proto`,
`run.proto`, version 5): a mismatch fails with a message. Contract: `docs/designer/architecture.md` §18 (§18.6 for the run bundle, §18.7 for the
JavaScript validators and the script objects).

## JavaScript validators

Any step that derives from `StepBase` can name a function a JavaScript library exports as a validator or a result validator:

```yaml
- id: order
  type: LlmStep                       # any step; a run without ScriptSteps starts the worker for its validators all the same
  validators: [js:orders.checkTotal]  # js:<library>.<function>; the library is what comes before the last dot
  resultValidators: [js:orders.wellFormed]
```

```javascript
// library "orders" (services.AddScriptLibrary, a package's scripts/orders.js or a plugin's js/orders.js)
exports.checkTotal = function (args) {
  var r = args.result;                                   // read-only facade over the result's JSON value
  var sum = r.lines.reduce(function (s, l) { return s + l.qty * l.price; }, 0);
  args.vars.lastTotal = sum;                             // read/write: comes back as a patch applied to PipelineContext.Metadata
  return sum === r.total ? true : 'total ' + r.total + ' does not match ' + sum;
};
```

The function runs in the run's worker (same bundle, same per-call limits, fresh V8 context per call) and receives `args`: `args.result` (read-only),
`args.vars` (the JSON-like pipeline variables, read/write) and, for a step validator, `args.step` (`{ name }`, read-only). Facades are JavaScript `Proxy`
objects over plain JSON: reading is plain JavaScript; writing to a read-only one throws `TypeError: args.result.x is read-only.`; writes to `args.vars` are
returned as `[ { op: "set" | "delete", path, value? } ]` and applied in place after the call. It returns `true`, `false`, an error text (empty = valid) or
`{ isValid, error }`; an exception, a broken limit or another return ends the step without retry (`StepHookException`, as a C# validator's exception does).
`console.*` goes to the step's logger. Limits: `services.ConfigureJsValidators(new JsValidatorOptions { TimeoutMs = 2000, MaxHeapMb = 64, ... })`.
A malformed name or an unknown library is a build error; a function the library does not export fails the run before any step runs.

## JavaScript hooks

A slot of `hooks:` can name a JavaScript function the same way (`js:<library>.<function>`, protocol 5; architecture §18.8):

```yaml
- id: order
  type: LlmStep
  hooks:
    postProcess: js:orders.stamp      # after each successful attempt, before validation
    onToolLimit: js:orders.finalAnswer
```

```javascript
exports.stamp = function (args) {          // returns nothing
  args.result.total = args.result.lines.reduce(function (s, l) { return s + l.amount; }, 0);
};
exports.finalAnswer = function (args) {
  if (args.reason === 'IterationLimit') args.finalAnswerPrompt = 'Answer with what you have.';
};
```

`args` is the **data view** of the slot's arguments (`HookData` in AITaskAgent.Core: `step.name`, `input`, `result`, `attempt`, `message`, `response`…)
plus `args.vars`. The writable members (`args.result`, `args.message`, `args.error`, `args.finalAnswerPrompt`) can be changed inside or reassigned; the
changes come back as a patch the host applies to the real arguments (`args.result` keeps its result object, type and metrics); a read-only member throws a
`TypeError` naming it. The function must not return anything (a returned value, a `Promise` included, is an error). It runs where a C# hook of the slot runs
(at the end of the step's method, with the step's own value already in `args`), in the run's worker with the validators' limits; a run whose only JavaScript
is a hook starts the worker. Since 2026-10-24 every slot takes JavaScript except `onConversationCreated` (its argument is the `ConversationContext`
itself, an object with methods; `JsHookSlots.Unsupported(slot)` says so); `selectConversation` is the JavaScript way to decide the conversation.

**Synchronous slots** (`enrichActivityBefore/After`, `enrichStarted/Completed`, `configureRequest`, 2026-10-24). Their delegates are synchronous
(`Action<TArgs>`, `Func<TArgs, T>`), so the host waits for the worker with a bounded blocking wait: the call runs on the thread pool (`Task.Run`, no
synchronization context captured) and the caller thread blocks at most `TimeoutMs` + 2 s kill grace + 10 s (queueing behind another call of the run and a
respawn); when that expires the call is cancelled (the worker is killed and the next call starts another) and the hook fails. The arguments are read and the
patch is applied on the caller thread, so a late answer is never applied. What they see: `enrich*` the activity (`activity { traceId, spanId,
parentSpanId, operationName }` read-only, `displayName` and `tags` writable and applied with `Activity.DisplayName`/`SetTag`) or the event (`event` read-only,
`additionalData` and `suppressFromUser` writable; the event is rebuilt with `with`, and a completed event that is not the framework's `StepCompletedEvent`
refuses the write), plus `input`/`result`; `configureRequest` the request (`messages` and `tools` read-only; `options`, `model`, `systemPrompt`,
`temperature`, `topP`, `maxTokens` writable, applied in that order to a copy of the request). A failure follows the slot's rule: logged and ignored for
`enrich*`, fatal for `configureRequest`.

## Script objects

An extension offers a .NET object to scripts with `builder.AddScriptObject<Inventory>("inventory")` (or `services.AddScriptObject<T>(name)`): each public
instance method becomes `host.inventory.<method>(args)` in camelCase (one JSON argument at most, plus an optional `CancellationToken`/`ScriptCallContext`;
no properties, no delegates, no overloads; `Task` results are awaited). Registering the object is its grant: a ScriptStep lists the methods it uses in
`allow`, a JavaScript validator may call them all. A plugin's JavaScript libraries are the `js/*.js` files of its folder (`ScriptLibraryFolder.Load`; a
`.nupkg` ships them as `plugin/js/`).

## How it is assembled

| Project | What it is |
|---|---|
| `AITaskAgent.Extensions.Script` (this one) | `ScriptStep`, its run hook, the extension and the worker client. It does not call ClearScript. |
| `AITaskAgent.Extensions.Script.Worker` | Executable that carries ClearScript V8: it compiles a run's scripts once and runs the calls it is sent. |

**One compile per run.** `ScriptStep` names a run hook (`IPipelineRunHookProvider`, framework contract added 2026-10-23): when
`IPipeline.ExecuteAsync` starts a run, `ScriptRunHook` joins the scripts of every ScriptStep whose place is known before the run
(top level and the children of Group, Parallel, Loop and declarative Switch) and the libraries they `use` into one bundle
(`Protocol/ScriptBundle.cs`, compiled into both projects: each script becomes the function `__bundle.steps["<step path>"]`,
each library `__bundle.libs["<name>"]`), starts the run's worker and sends `load`; the worker compiles the bundle once
(`V8Runtime.Compile`) and checks that every script stayed inside its function (a script that closes its function early and
plants code of its own is refused). A compile error fails the run before any step runs, naming each step and the line in its
own script. Each execution of a step is then `run` (function name, input, limits): a fresh V8 context of the same runtime runs
the compiled bundle (no parsing), builds the globals and calls the step's function. The bundle is built again on every run and
never cached between runs; the worker is killed when the run ends (success, error, cancellation or suspension: a resumed run
compiles again). A step the run could not know in advance (a `nextStep` child, a step executed directly) gets a worker of its
own for that call, built the same way with a bundle of one.

The host reads messages until it receives `result`; the protocol is JSON lines over stdin/stdout (`Protocol/ScriptProtocol.cs`).
`context.get` and `host.*` calls are the only times the worker waits for a reply from the host. Calls of one run are serialized
(one V8 isolate): a ParallelStep whose branches run scripts runs them one after another.

## Limits

Per call. A call that breaks one (time, heap, a killed or crashed worker) kills the run's worker; the next call starts a new
one from the same scripts, so nothing of the broken runtime is reused. A script that hangs holds the other script calls of the
run at most its own `timeoutMs` plus the 2 s grace.

- **Time:** the worker interrupts V8 at `timeoutMs`; the host kills the process 2 s later if V8 did not allow itself to be
  interrupted (a single long native call) and as soon as the token is cancelled.
- **Heap, `ArrayBuffer` and stack:** `maxHeapMb` and `maxStackKb`, with hard ceilings of 128 MB and 2048 KB. A value out of range
  is rejected when the step is built; it is not clamped. The heap cap of a call is measured from the runtime's baseline after a
  full GC (what the compiled bundle and earlier calls committed is not charged to it). The `ArrayBuffer` cap is the runtime's: the
  largest `maxHeapMb` of the run.
- **Whole process:** `maxHeapMb × 4 + maxHeapMb + 192 MB`, with the largest `maxHeapMb` of the run. Windows: *job object* (memory, a
  single process, the worker dies if the host dies). Other platforms: the host checks the private memory every 25 ms and kills the process.
- A worker that dies or exceeds its memory returns an error result with the step id; the host does not learn anything more.

## Cost and deployment

One process per run: .NET and V8 start up once, when the run starts. Measured on the development machine (2026-10-23, trivial
script, Release): a run of 20 ScriptSteps went from 6.8 s (≈ 335 ms per step, one process each) to 0.40 s, a run of 100 from
33.3 s to 0.51 s; a loop of 100 iterations from 33.5 s to 0.51 s. The fixed cost of a run with scripts is ≈ 0.4 s (start-up +
compile); each further call costs ≈ 1-1.5 ms (fresh context, GC, the call). A step executed on its own still pays the whole
start-up (≈ 350 ms). The application that uses the step must carry `AITaskAgent.Extensions.Script.Worker.dll`
with its `.runtimeconfig.json` and dependencies next to its binaries: referencing this project already does that. To change where
it is looked up, the environment variable `AITASKAGENT_SCRIPT_WORKER` points to the `.dll`; if it is missing, the error says so.

## Known pending item

The Linux path (memory watchdog instead of a *job object*) is written but has not been run. There is no per-process CPU limit
beyond `timeoutMs`. Script calls of one run are serialized, so parallel branches do not run JavaScript in parallel. One hook slot stays C#-only,
`onConversationCreated` (see «JavaScript hooks»); the synchronous slots block a thread-pool thread while they wait for the worker (bounded).
