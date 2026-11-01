using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.Script.Hosting;
using AITaskAgent.Script.Isolation;
using AITaskAgent.Script.Protocol;
using AITaskAgent.Script.Validators;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace AITaskAgent.Script.Steps;

/// <summary>
/// The once-per-run preparation of the <see cref="ScriptStep"/>s and the JavaScript validators and hooks (BRMS style): when a run starts, the
/// scripts of all its known ScriptSteps, the libraries they use and the libraries of its <c>js:</c> validators and hooks (with what they
/// <c>require</c>) are joined in one bundle, each script as the function named after the step's path, and compiled once in the run's worker.
/// A compile error fails the run before any step runs, naming each step and the line in its own script; so does a <c>js:</c> validator or
/// hook whose library does not export its function. The bundle is built again on every run (never cached between runs: the workflow may change), and the worker is killed when
/// the run ends.
/// </summary>
internal sealed class ScriptRunHook : IPipelineRunHook
{
    public static readonly ScriptRunHook Instance = new();

    private ScriptRunHook()
    {
    }

    /// <inheritdoc/>
    public async Task<IAsyncDisposable?> RunStartingAsync(
        IReadOnlyList<KeyValuePair<string, IStep>> steps, PipelineContext context, CancellationToken cancellationToken)
    {
        List<BundlePiece> pieces = [];
        Dictionary<object, string> functions = new(ReferenceEqualityComparer.Instance);
        HashSet<string> keys = new(StringComparer.Ordinal);
        SortedSet<string> uses = new(StringComparer.Ordinal);
        List<(string Path, IJsFunction Function)> validators = [];
        int heapMb = ScriptLimits.MinHeapMb;
        IScriptCatalog? catalog = context.ServiceProvider.GetService<IScriptCatalog>();

        foreach ((string path, IStep step) in steps)
        {
            // The step's validators, result validators and hooks written in JavaScript: their libraries join the bundle.
            if (step is StepBase stepBase)
            {
                IEnumerable<IJsFunction> scripted = stepBase.Validators.OfType<IJsFunction>()
                    .Concat(stepBase.ResultValidators.OfType<IJsFunction>())
                    .Concat(Core.Hooks.HookSlots.Bound(stepBase.Hooks).Select(h => h.Value.Target).OfType<IJsFunction>());
                foreach (IJsFunction validator in scripted)
                {
                    if (!functions.ContainsKey(validator))
                    {
                        functions[validator] = validator.Id;
                        validators.Add((path, validator));
                    }
                }
            }

            // An empty script is reported by the step itself when it runs, as before.
            if (step is not ScriptStep script || string.IsNullOrWhiteSpace(script.Script) || functions.ContainsKey(script))
            {
                continue;
            }

            // The function's name is the step's path in the tree: deterministic, and unique in a valid pipeline.
            string key = path;
            for (int n = 2; !keys.Add(key); n++)
            {
                key = $"{path}#{n}";
            }

            functions[script] = key;
            pieces.Add(new BundlePiece(script.Name, false, key, script.Script));
            uses.UnionWith(script.Uses);
            heapMb = Math.Max(heapMb, script.MaxHeapMb);
        }

        if (validators.Count > 0)
        {
            JsValidatorOptions options = context.ServiceProvider.GetService<JsValidatorOptions>() ?? new JsValidatorOptions();
            heapMb = Math.Max(heapMb, Math.Clamp(options.MaxHeapMb, ScriptLimits.MinHeapMb, ScriptLimits.MaxHeapMbCeiling));
            uses.UnionWith(ScriptLibraryClosure.Of(catalog, validators.Select(v => v.Function.Library)).Select(l => l.Name));
        }

        if (pieces.Count == 0 && validators.Count == 0)
        {
            return null;
        }

        // Each library once, whoever uses it. A name the host does not know is left out: the step that declares it
        // reports it when it runs, with the message it always had.
        foreach (string name in uses)
        {
            if (catalog?.FindLibrary(name) is { } library)
            {
                pieces.Add(new BundlePiece("lib:" + name, true, name, library.Source));
            }
        }

        string[] exports = [.. validators.Select(v => v.Function.Library).Distinct(StringComparer.Ordinal)];
        (ScriptRunSession? session, IReadOnlyList<(string Id, string Error)> errors, _, _, JObject? listed) =
            await ScriptRunSession.OpenAsync(context.CorrelationId, pieces, heapMb, exports, cancellationToken);

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "The JavaScript of this run does not compile, so no step was run:" + Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(e => "  " + Describe(e.Id, e.Error))));
        }

        // No worker (it could not be located, started or capped): the steps and validators run on their own, one worker each, and
        // report that failure themselves when they run, as they always did.
        if (session == null)
        {
            return null;
        }

        List<string> missing = [];
        foreach ((string path, IJsFunction validator) in validators)
        {
            if (Missing(validator, listed) is { } problem)
            {
                string label = validator is JsValidator { IsResultValidator: true } ? "result validator" : validator.Label;
                missing.Add($"  step '{path}', {label} '{validator.Id}': {problem}");
            }
        }

        if (missing.Count > 0)
        {
            await session.DisposeAsync();
            throw new InvalidOperationException(
                "A JavaScript validator or hook of this run names a function its library does not export, so no step was run:" + Environment.NewLine +
                string.Join(Environment.NewLine, missing.Distinct(StringComparer.Ordinal)));
        }

        return ScriptRunSessions.Attach(context, session, functions);
    }

    /// <summary>Why the library of <paramref name="validator"/> does not give its function, from the load's listing; null when it does.</summary>
    internal static string? Missing(IJsFunction validator, JObject? listed)
        => ScriptLibraryExports.From(listed).TryGetValue(validator.Library, out ScriptLibraryExports? exports)
            ? exports.Problem(validator.Function)
            : $"the library '{validator.Library}' is not known to this host.";

    /// <summary>One compile error, under the step or library it belongs to.</summary>
    internal static string Describe(string id, string error) => id switch
    {
        "" => error,
        _ when id.StartsWith("lib:", StringComparison.Ordinal) => $"library '{id[4..]}': script error: {error}",
        _ => $"ScriptStep '{id}': script error: {error}",
    };
}
