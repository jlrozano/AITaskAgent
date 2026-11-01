using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.Script.Hosting;
using AITaskAgent.Script.Isolation;
using AITaskAgent.Script.Protocol;
using AITaskAgent.Script.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AITaskAgent.Script.Validators;

/// <summary>A function a JavaScript library exports, used as a validator or a hook of a step (<c>js:&lt;library&gt;.&lt;function&gt;</c>).</summary>
internal interface IJsFunction
{
    /// <summary>The YAML name (<c>js:orders.check</c>).</summary>
    string Id { get; }

    /// <summary>The library that exports the function.</summary>
    string Library { get; }

    /// <summary>The function's name.</summary>
    string Function { get; }

    /// <summary>The step it belongs to.</summary>
    StepBase Step { get; }

    /// <summary>What it is, for messages: <c>validator</c>, <c>result validator</c>, <c>hook 'postProcess'</c>.</summary>
    string Label { get; }
}

/// <summary>
/// One call of a <see cref="IJsFunction"/> in the run's script worker (architecture §18.7, §18.8): the same process, bundle and limits per call
/// for validators and hooks (<see cref="JsValidatorOptions"/>); a call whose run did not bundle it (a <c>NextSteps</c> child, a step executed on
/// its own) gets a worker of its own. A JavaScript error or a broken limit (the worker is killed and the next call starts another) is a
/// <see cref="StepHookException"/> naming the function.
/// </summary>
internal static class JsFunctionCall
{
    /// <summary>Runs <paramref name="function"/> with <paramref name="payload"/> as a <paramref name="kind"/> call and returns its envelope.</summary>
    public static async Task<JObject> InvokeAsync(IJsFunction function, string kind, JObject payload, PipelineContext context, CancellationToken cancellationToken)
    {
        IServiceProvider services = context.ServiceProvider;
        ILogger logger = services.GetService<ILoggerFactory>()?.CreateLogger(function.Step.GetType()) ?? NullLogger.Instance;
        JsValidatorOptions options = services.GetService<JsValidatorOptions>() ?? new JsValidatorOptions();
        if (options.Problem() is { } problem)
        {
            throw Fail(function, problem);
        }

        IScriptCatalog? catalog = services.GetService<IScriptCatalog>();
        IReadOnlyList<IScriptLibrary> libraries = ScriptLibraryClosure.Of(catalog, [function.Library]);
        if (libraries.Count == 0 || libraries[0].Name != function.Library)
        {
            throw Fail(function, $"the JavaScript library '{function.Library}' is not known to this host.");
        }

        // Only the JSON-like variables exist for the script; each one is read from the host on first use.
        payload["keys"] = new JArray(context.Metadata.Where(e => Bridge.ToJson(e.Value) != null).Select(e => e.Key).Order(StringComparer.Ordinal));

        // A validator or a hook may call every method of the script objects the host registered (registering one is its grant), nothing else.
        string[] allow = [.. (catalog?.HostFunctions ?? []).OfType<ScriptObjectFunction>().Select(f => f.Name).Order(StringComparer.Ordinal)];
        WorkerCall call = new(
            function.Id, payload.ToString(Formatting.None), options.TimeoutMs, options.MaxHeapMb, options.MaxStackKb,
            [.. libraries.Select(l => l.Name)], allow, options.MaxCalls, options.MaxCallKb * 1024, Export: function.Function, Kind: kind);
        Bridge bridge = new(function, context, services, logger);

        WorkerResult outcome = ScriptRunSessions.Lookup(context, function) is { } bundled
            ? await bundled.Session.CallAsync(function.Library, call, bridge, cancellationToken)
            : await RunAloneAsync(function, libraries, call, bridge, options, cancellationToken);

        if (outcome.Error != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogError(outcome.Exception, "[{Function}] ({Label}) of step {Step} failed: {Message}", function.Id, function.Label, function.Step.Name, outcome.Error);
            throw Fail(function, outcome.Error, outcome.Exception);
        }

        return outcome.Value as JObject ?? throw Fail(function, "the worker did not return the function's envelope.");
    }

    /// <summary>The value as JSON for the payload (null as a JSON null).</summary>
    public static JToken ToJson(object? value) => value switch
    {
        null => JValue.CreateNull(),
        JToken token => token,
        _ => JToken.FromObject(value),
    };

    /// <summary>Applies the <c>args.vars</c> patch of an envelope to the pipeline variables.</summary>
    public static void ApplyVariables(IJsFunction function, PipelineContext context, JObject envelope)
    {
        if (envelope["patch"] is not JArray { Count: > 0 } patch)
        {
            return;
        }

        try
        {
            VariablePatch.Apply(context, patch);
        }
        catch (InvalidOperationException ex)
        {
            throw Fail(function, $"its writes to args.vars could not be applied: {ex.Message}", ex);
        }
    }

    /// <summary>The error of <paramref name="function"/>: a <see cref="StepHookException"/> that names it and its step.</summary>
    public static StepHookException Fail(IJsFunction function, string message, Exception? inner = null)
        => new(function.Id, function.Step.Name, new InvalidOperationException(message, inner));

    private static async Task<WorkerResult> RunAloneAsync(
        IJsFunction function, IReadOnlyList<IScriptLibrary> libraries, WorkerCall call, IScriptHostBridge bridge, JsValidatorOptions options, CancellationToken cancellationToken)
    {
        List<BundlePiece> pieces = [.. libraries.Select(l => new BundlePiece("lib:" + l.Name, true, l.Name, l.Source))];
        (ScriptRunSession? session, IReadOnlyList<(string Id, string Error)> errors, string? failure, Exception? exception) =
            await ScriptRunSession.OpenAsync(function.Step.Name, pieces, options.MaxHeapMb, cancellationToken);
        if (session == null)
        {
            return new WorkerResult(null, failure ?? string.Join(Environment.NewLine, errors.Select(e => ScriptRunHook.Describe(e.Id, e.Error))), exception);
        }

        await using (session)
        {
            return await session.CallAsync(function.Library, call, bridge, cancellationToken);
        }
    }

    /// <summary>What a validator's or hook's script reaches in the host: the pipeline variables (read), the step's logger and the script objects.</summary>
    internal sealed class Bridge(IJsFunction function, PipelineContext context, IServiceProvider services, ILogger logger) : IScriptHostBridge
    {
        /// <summary>A variable as JSON when it is JSON-like (JSON values, strings, booleans, numbers, dates, guids), else null: nothing else is exposed.</summary>
        public static JToken? ToJson(object? value) => value switch
        {
            null => null,
            JToken token => token,
            string or bool or byte or sbyte or short or ushort or int or uint or long or ulong
                or float or double or decimal or DateTime or DateTimeOffset or Guid => JToken.FromObject(value),
            _ => null,
        };

        public string? Get(string key)
            => context.Metadata.TryGetValue(key, out object? value) && ToJson(value) is { } json ? json.ToString(Formatting.None) : null;

        // No context.set: the writes go through args.vars and the patch.
        public void Set(string key, string json)
        {
        }

        public void Log(string level, string text)
        {
            LogLevel logLevel = level switch
            {
                "warn" => LogLevel.Warning,
                "error" => LogLevel.Error,
                "debug" => LogLevel.Debug,
                _ => LogLevel.Information,
            };
            logger.Log(logLevel, "[{Function} of {Step}] {Message}", function.Id, function.Step.Name, text);
        }

        public async Task<HostCallReply> CallAsync(string name, string argsJson, CancellationToken cancellationToken)
        {
            if (services.GetService<IScriptCatalog>()?.FindHostFunction(name) is not ScriptObjectFunction host)
            {
                return new HostCallReply(false, null, $"host function '{name}' is not available to a {function.Label}.");
            }

            try
            {
                JToken? value = await host.InvokeAsync(JToken.Parse(argsJson), new ScriptCallContext(function.Step.Name, context, services), cancellationToken);
                return new HostCallReply(true, value?.ToString(Formatting.None), null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[{Function} of {Step}] host function {Name} failed.", function.Id, function.Step.Name, name);
                return new HostCallReply(false, null, $"{name}: {ex.Message}");
            }
        }
    }
}
