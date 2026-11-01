using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.Script.Hosting;
using AITaskAgent.Script.Isolation;
using AITaskAgent.Script.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.ComponentModel;

namespace AITaskAgent.Script.Steps;

/// <summary>
/// Runs JavaScript declared in the pipeline (YAML <c>type: ScriptStep</c>) on a V8 engine and returns
/// its result as JSON. No LLM involved; the JavaScript counterpart of a delegated step.
/// </summary>
/// <remarks>
/// <para><b>Contract of the script.</b> The text of <see cref="Script"/> is the <i>body of a function</i>
/// <c>function (input, context) { ... }</c>: the value it <c>return</c>s is the step's result (a bare
/// last expression is NOT returned; use <c>return</c>). <c>undefined</c> (no return) becomes JSON <c>null</c>.
/// Only JSON-representable values survive (objects, arrays, strings, numbers, booleans, null).</para>
/// <list type="bullet">
/// <item><c>input</c>: the previous step's value as a plain JS object (round-tripped through JSON; a
/// string stays a string).</item>
/// <item><c>context.get(key)</c> / <c>context.set(key, value)</c>: read and write the pipeline's variables
/// (<see cref="PipelineContext.Metadata"/>). Values are exchanged as JSON: <c>set</c> stores a JSON value;
/// <c>get</c> returns <c>undefined</c> for a missing key and for values that are not JSON-like (JSON values,
/// strings, booleans, numbers, dates, guids); arbitrary .NET objects are never exposed.</item>
/// <item><c>log(...)</c> and <c>console.log/info/warn/error/debug(...)</c>: written to the step's
/// <see cref="ILogger"/>; arguments that are not strings are logged as JSON.</item>
/// </list>
/// <list type="bullet">
/// <item><c>require(name)</c>: closed. Only the libraries listed in <see cref="Uses"/> (CommonJS, <c>module.exports</c>).</item>
/// <item>Native library, no I/O: <c>hash.sha256/sha1/md5</c>, <c>base64.encode/decode</c>, <c>uuid.v4</c>,
/// <c>date.format/parse</c>, <c>path.join/basename/dirname/extname/normalize</c>.</item>
/// <item><c>host.&lt;ns&gt;.&lt;fn&gt;(args)</c>: the host functions listed in <see cref="Allow"/> that the host grants
/// (<see cref="ScriptHostOptions.GrantedFunctions"/>, default: none). A call is a JSON round trip to the host.</item>
/// <item><c>nextStep({ type, id, ... })</c>: queues a step definition (the shape of YAML v2); when the script succeeds the host builds
/// each one with the step registry and returns them as <see cref="IStepResult.NextSteps"/>.</item>
/// </list>
/// <para><b>One compile per run (BRMS style).</b> When a pipeline run starts, the scripts of all its ScriptSteps whose
/// place is known in advance and the libraries they use are joined in one bundle, each script as the function named
/// after the step's path, and compiled once in one worker process for the run; a compile error fails the run before any
/// step runs, naming the step and the line of its own script. Each execution of the step is then a call of its
/// function. Never cached between runs; the worker is killed when the run ends (also on error, cancellation or
/// suspension: a resumed run compiles again). A step the run could not know in advance (a <c>NextSteps</c> child, a
/// step executed directly) gets a worker of its own for that call, as before.</para>
/// <para><b>Isolation.</b> Each call runs in a fresh V8 context of the run's worker
/// (<c>AITaskAgent.Extensions.Script.Worker</c>): no global survives from one step, loop iteration or branch to the
/// next, and the input is a new object every time. Calls of one run are serialized (one V8 isolate). The host
/// process never loads V8 and never holds the script's memory. Inside the worker the engine is bare: no .NET type
/// or object is exposed to the script (<c>AddHostType</c>/<c>AddHostObject</c> are not used; three bridge delegates
/// are captured by a bootstrap closure and removed from the global scope) and a bare V8 has no file, network or
/// process APIs. <c>context.get/set</c> and <c>log</c> cross to the host as JSON over the worker's stdin/stdout.</para>
/// <para><b>Limits.</b> Per call. A call that breaks one kills the run's worker; the next call starts another from
/// the same scripts. Wall-clock <see cref="TimeoutMs"/> (the worker interrupts the engine and names the line;
/// if V8 cannot be interrupted, for example in one long native call, the host kills the worker two seconds later);
/// a V8 heap cap (<see cref="MaxHeapMb"/>, checked while the script runs and again when it ends); an ArrayBuffer
/// cap of the same size; a stack cap (<see cref="MaxStackKb"/>). <see cref="MaxHeapMb"/> and
/// <see cref="MaxStackKb"/> have hard ceilings (<see cref="MaxHeapMbCeiling"/>, <see cref="MaxStackKbCeiling"/>)
/// that no pipeline can raise: they are rejected, not clamped. Behind those soft limits sits a memory limit on the
/// whole worker process, derived from the largest heap cap of the run (also the ArrayBuffer cap): on Windows the operating system enforces it (job object, which
/// also kills the worker if the host dies); on other platforms the host polls the worker's private memory every
/// 25 ms and kills it, so a single native allocation can overshoot by what it grabs between two polls. A killed or
/// crashed worker is an error result naming the step; it cannot take the host down.</para>
/// <para><b>Cost.</b> One process per run: the start-up of the .NET runtime and the V8 engine (a few hundred
/// milliseconds) is paid once, when the run starts; each call then costs a fresh context and the call itself.</para>
/// <para><b>Deployment.</b> The application that runs this step must carry the worker next to its own binaries
/// (referencing this project does it); <c>AITASKAGENT_SCRIPT_WORKER</c> overrides where it is looked up.</para>
/// </remarks>
[StepTag("ScriptStep")]
[Description("Runs JavaScript in an isolated, resource-limited process; the script is the body of function (input, context).")]
public sealed class ScriptStep : StepBase, IPipelineRunHookProvider
{
    /// <summary>Default wall-clock limit, in milliseconds.</summary>
    public const int DefaultTimeoutMs = 5000;

    /// <summary>Default heap cap, in MB.</summary>
    public const int DefaultMaxHeapMb = ScriptLimits.DefaultHeapMb;

    /// <summary>Smallest accepted heap cap, in MB.</summary>
    public const int MinHeapMb = ScriptLimits.MinHeapMb;

    /// <summary>Hard ceiling of the heap cap, in MB; a larger value is rejected.</summary>
    public const int MaxHeapMbCeiling = ScriptLimits.MaxHeapMbCeiling;

    /// <summary>Default stack cap, in KB.</summary>
    public const int DefaultMaxStackKb = ScriptLimits.DefaultStackKb;

    /// <summary>Smallest accepted stack cap, in KB.</summary>
    public const int MinStackKb = ScriptLimits.MinStackKb;

    /// <summary>Hard ceiling of the stack cap, in KB; a larger value is rejected.</summary>
    public const int MaxStackKbCeiling = ScriptLimits.MaxStackKbCeiling;

    private const string CancelledMessage = "the execution was cancelled.";

    private int _maxHeapMb = DefaultMaxHeapMb;
    private int _maxStackKb = DefaultMaxStackKb;

    /// <summary>Creates the step; the registry sets <see cref="StepBase.Name"/> from the YAML <c>id</c>.</summary>
    public ScriptStep() : this("ScriptStep")
    {
    }

    /// <summary>Creates the step with an explicit name (programmatic use).</summary>
    public ScriptStep(string name) : base(name, typeof(IStepResult), typeof(JsonStepResult))
    {
        // A failing script is deterministic: retrying it only repeats the failure.
        MaxRetries = 1;
    }

    /// <summary>Function body to run (multiline <c>|</c> block in YAML). See the type remarks.</summary>
    [Description("Function body; receives (input, context) and must return the result.")]
    public string Script { get; set; } = "";

    /// <summary>Wall-clock limit in milliseconds (default <see cref="DefaultTimeoutMs"/>). Must be positive.</summary>
    [Description("Wall-clock limit in milliseconds (default 5000).")]
    public int TimeoutMs { get; set; } = DefaultTimeoutMs;

    /// <summary>
    /// V8 heap cap in MB, between <see cref="MinHeapMb"/> and <see cref="MaxHeapMbCeiling"/> (default
    /// <see cref="DefaultMaxHeapMb"/>). A value outside that range is rejected when it is set.
    /// </summary>
    [Description("V8 heap cap in MB: 8 to 128 (default 64). Above 128 is rejected.")]
    public int MaxHeapMb
    {
        get => _maxHeapMb;
        set => _maxHeapMb = value is >= MinHeapMb and <= MaxHeapMbCeiling
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(MaxHeapMb), value, $"'maxHeapMb' must be between {MinHeapMb} and {MaxHeapMbCeiling} MB.");
    }

    /// <summary>
    /// V8 stack cap in KB, between <see cref="MinStackKb"/> and <see cref="MaxStackKbCeiling"/> (default
    /// <see cref="DefaultMaxStackKb"/>). A value outside that range is rejected when it is set.
    /// </summary>
    [Description("V8 stack cap in KB: 64 to 2048 (default 512). Above 2048 is rejected.")]
    public int MaxStackKb
    {
        get => _maxStackKb;
        set => _maxStackKb = value is >= MinStackKb and <= MaxStackKbCeiling
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(MaxStackKb), value, $"'maxStackKb' must be between {MinStackKb} and {MaxStackKbCeiling} KB.");
    }

    /// <summary>Names of the libraries (of the host's <see cref="IScriptCatalog"/>) the script may <c>require</c>.</summary>
    [Description("Names of the JavaScript libraries the script may require(); anything else is refused.")]
    public List<string> Uses { get; set; } = [];

    /// <summary>
    /// Host functions the script may call as <c>host.ns.fn(args)</c>. Default deny: a function also has to be granted by
    /// the host (<see cref="ScriptHostOptions"/>); this list can narrow what the host grants, never widen it.
    /// </summary>
    [Description("Host functions the script may call (host.ns.fn(args)); the host must also grant each one.")]
    public List<string> Allow { get; set; } = [];

    /// <summary>Most host function calls in one run (default 100, at most 10 000).</summary>
    [Description("Most host function calls in one run (default 100, at most 10000).")]
    public int MaxCalls { get; set; } = ScriptLimits.DefaultMaxCalls;

    /// <summary>Budget of kilobytes exchanged with host functions in one run (default 1024, at most 8192).</summary>
    [Description("Kilobytes exchanged with host functions in one run (default 1024, at most 8192).")]
    public int MaxCallKb { get; set; } = ScriptLimits.DefaultMaxCallBytes / 1024;

    /// <inheritdoc/>
    IPipelineRunHook IPipelineRunHookProvider.RunHook => ScriptRunHook.Instance;

    /// <inheritdoc/>
    protected override async Task<IStepResult> ExecuteAsync(
        IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Script))
        {
            return Fail("the 'script' property is empty.");
        }

        if (TimeoutMs <= 0)
        {
            return Fail($"'timeoutMs' must be positive (was {TimeoutMs}).");
        }

        // Same outcome as a cancellation that arrives mid-run; an already-cancelled token never starts a worker.
        if (cancellationToken.IsCancellationRequested)
        {
            return Fail(CancelledMessage);
        }

        string inputJson;
        try
        {
            inputJson = ToJsonToken(input.Value).ToString(Formatting.None);
        }
        catch (Exception ex)
        {
            return Fail($"the input could not be converted to JSON: {ex.Message}", ex);
        }

        if (MaxCalls is < 0 or > ScriptLimits.MaxCallsCeiling)
        {
            return Fail($"'maxCalls' must be between 0 and {ScriptLimits.MaxCallsCeiling} (was {MaxCalls}).");
        }

        if (MaxCallKb is < 0 or > ScriptLimits.MaxCallBytesCeiling / 1024)
        {
            return Fail($"'maxCallKb' must be between 0 and {ScriptLimits.MaxCallBytesCeiling / 1024} (was {MaxCallKb}).");
        }

        IServiceProvider services = context.ServiceProvider;
        IReadOnlyList<IScriptLibrary> libraries;
        IReadOnlyList<string> allow;
        try
        {
            (libraries, allow) = Prepare(services);
        }
        catch (InvalidOperationException ex)
        {
            return Fail(ex.Message);
        }

        WorkerCall call = new(
            Name, inputJson, TimeoutMs, MaxHeapMb, MaxStackKb,
            [.. libraries.Select(l => l.Name)], allow, MaxCalls, MaxCallKb * 1024);
        ContextBridge bridge = new(this, context, services);

        // Bundled by the run (the usual case: the run compiled every script once when it started), or on its own when the
        // run could not know this step in advance (a NextSteps child, a step executed directly).
        WorkerResult result = ScriptRunSessions.Lookup(context, this) is { } bundled
            ? await bundled.Session.CallAsync(bundled.Function, call, bridge, cancellationToken)
            : await RunAloneAsync(libraries, call, bridge, cancellationToken);

        if (result.Error != null)
        {
            return Fail(result.Error, result.Exception);
        }

        JsonStepResult stepResult = new(this, result.Value ?? JValue.CreateNull());
        if (result.NextStepsJson != null)
        {
            try
            {
                stepResult.NextSteps.AddRange(BuildNextSteps(result.NextStepsJson, services));
            }
            catch (InvalidOperationException ex)
            {
                return Fail(ex.Message, ex);
            }
        }

        return stepResult;
    }

    /// <summary>
    /// Runs this step's script in a worker of its own, started and compiled for this call and killed after it (the bundle
    /// holds only this script and its libraries).
    /// </summary>
    private async Task<WorkerResult> RunAloneAsync(
        IReadOnlyList<IScriptLibrary> libraries, WorkerCall call, IScriptHostBridge bridge, CancellationToken cancellationToken)
    {
        List<BundlePiece> pieces =
        [
            new BundlePiece(Name, false, Name, Script),
            .. libraries.Select(l => new BundlePiece("lib:" + l.Name, true, l.Name, l.Source)),
        ];
        (ScriptRunSession? session, IReadOnlyList<(string Id, string Error)> errors, string? failure, Exception? exception) =
            await ScriptRunSession.OpenAsync(Name, pieces, MaxHeapMb, cancellationToken);
        if (session == null)
        {
            string message = failure ?? (errors.Count == 1 && errors[0].Id == Name
                ? $"script error: {errors[0].Error}"
                : string.Join(Environment.NewLine, errors.Select(e => ScriptRunHook.Describe(e.Id, e.Error))));
            return new WorkerResult(null, message, exception);
        }

        await using (session)
        {
            return await session.CallAsync(Name, call, bridge, cancellationToken);
        }
    }

    /// <summary>
    /// Resolves what this run may use: the declared libraries and the host functions the script can call (declared,
    /// known to the catalog and granted by the host). Anything declared that is not available is an error that names it,
    /// so a script never meets a half-defined <c>host</c>.
    /// </summary>
    private (IReadOnlyList<IScriptLibrary> Libraries, IReadOnlyList<string> Allow) Prepare(IServiceProvider services)
    {
        IScriptCatalog? catalog = services.GetService<IScriptCatalog>();
        List<IScriptLibrary> modules = [];
        int bytes = 0;
        foreach (string name in Uses.Distinct(StringComparer.Ordinal))
        {
            IScriptLibrary library = catalog?.FindLibrary(name)
                ?? throw new InvalidOperationException($"the library '{name}' listed in 'uses' is not known to this host.");
            bytes += System.Text.Encoding.UTF8.GetByteCount(library.Source);
            if (bytes > ScriptLimits.MaxModulesBytes)
            {
                throw new InvalidOperationException($"the libraries in 'uses' are larger than {ScriptLimits.MaxModulesBytes / 1024} KB.");
            }

            modules.Add(library);
        }

        ScriptHostOptions? options = services.GetService<ScriptHostOptions>();
        List<string> allow = [];
        foreach (string function in Allow.Distinct(StringComparer.Ordinal))
        {
            if (catalog?.FindHostFunction(function) == null)
            {
                throw new InvalidOperationException($"the host function '{function}' listed in 'allow' does not exist.");
            }

            // A script object's method is granted by its registration (ScriptObjectFunction); the other host functions by ScriptHostOptions.
            if (catalog?.FindHostFunction(function) is not ScriptObjectFunction && options?.GrantedFunctions.Contains(function) != true)
            {
                throw new InvalidOperationException(
                    $"the host function '{function}' listed in 'allow' is not granted by the host (default deny).");
            }

            allow.Add(function);
        }

        return (modules, allow);
    }

    /// <summary>Builds the step definitions a script queued with <c>nextStep</c> through the step registry of the run.</summary>
    private static List<IStep> BuildNextSteps(string json, IServiceProvider services)
    {
        IStepRegistry registry = services.GetService<IStepRegistry>()
            ?? throw new InvalidOperationException("'nextStep' needs the step registry of the run, which is not available.");

        List<IStep> steps = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        JArray definitions = JArray.Parse(json);
        for (int i = 0; i < definitions.Count; i++)
        {
            if (definitions[i] is not JObject definition)
            {
                throw new InvalidOperationException($"next step #{i + 1} is not an object.");
            }

            string? type = definition.Value<string>("type");
            string? id = definition.Value<string>("id");
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException($"next step #{i + 1} needs a text 'type' and a text 'id'.");
            }

            if (!ids.Add(id))
            {
                throw new InvalidOperationException($"next step #{i + 1}: the id '{id}' is used twice.");
            }

            JObject config = (JObject)definition.DeepClone();
            config.Remove("type");
            config.Remove("id");
            IStep step;
            try
            {
                step = registry.Create(type, config, services);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"next step #{i + 1} ('{id}', type '{type}') could not be created: {ex.Message}", ex);
            }

            if (step is StepBase)
            {
                // The setter is protected internal (the YAML factory names steps this way): reflection is how code outside the framework does it.
                typeof(StepBase).GetProperty(nameof(StepBase.Name))!.GetSetMethod(nonPublic: true)!.Invoke(step, [id]);
            }

            steps.Add(step);
        }

        return steps;
    }

    private IStepResult Fail(string message, Exception? exception = null)
    {
        string full = $"ScriptStep '{Name}' failed: {message}";
        Logger.LogError(exception, "{Message}", full);
        return CreateErrorResult(full, exception);
    }

    private static JToken ToJsonToken(object? value) => value switch
    {
        null => JValue.CreateNull(),
        JToken token => token,
        _ => JToken.FromObject(value),
    };

    /// <summary>What the worker's script reaches in this step: the pipeline variables and the step's logger.</summary>
    private sealed class ContextBridge(ScriptStep step, PipelineContext context, IServiceProvider services) : IScriptHostBridge
    {
        /// <summary>Runs a host function. The client already checked the allow list; the catalog and the grant are checked again here.</summary>
        public async Task<HostCallReply> CallAsync(string function, string argsJson, CancellationToken cancellationToken)
        {
            IScriptHostFunction? host = services.GetService<IScriptCatalog>()?.FindHostFunction(function);
            if (host == null || (host is not ScriptObjectFunction && services.GetService<ScriptHostOptions>()?.GrantedFunctions.Contains(function) != true))
            {
                return new HostCallReply(false, null, $"host function '{function}' is not available.");
            }

            try
            {
                JToken args = JToken.Parse(argsJson);
                JToken? value = await host.InvokeAsync(args, new ScriptCallContext(step.Name, context, services), cancellationToken);
                return new HostCallReply(true, value?.ToString(Formatting.None), null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                step.Logger.LogWarning(ex, "[ScriptStep {Step}] host function {Function} failed.", step.Name, function);
                return new HostCallReply(false, null, $"{function}: {ex.Message}");
            }
        }

        /// <summary>JSON text of a context variable, or null when absent or not JSON-like (nothing else is exposed).</summary>
        public string? Get(string key)
        {
            if (!context.Metadata.TryGetValue(key, out object? value) || value == null)
            {
                return null;
            }

            return value switch
            {
                JToken token => token.ToString(Formatting.None),
                string or bool or byte or sbyte or short or ushort or int or uint or long or ulong
                    or float or double or decimal or DateTime or DateTimeOffset or Guid
                    => JToken.FromObject(value).ToString(Formatting.None),
                _ => null,
            };
        }

        public void Set(string key, string json) => context.Metadata[key] = JToken.Parse(json);

        public void Log(string level, string text)
        {
            LogLevel logLevel = level switch
            {
                "warn" => LogLevel.Warning,
                "error" => LogLevel.Error,
                "debug" => LogLevel.Debug,
                _ => LogLevel.Information,
            };
            step.Logger.Log(logLevel, "[ScriptStep {Step}] {Message}", step.Name, text);
        }
    }
}
