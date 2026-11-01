using AITaskAgent.Core.Abstractions;
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
using System.Text.RegularExpressions;

namespace AITaskAgent.Script.Validators;

/// <summary>
/// Limits of every JavaScript validator call of a host (architecture §18.7): a validator is a name in the YAML, it has no properties of its own.
/// Registered with <c>ConfigureJsValidators</c>; the defaults apply without it. Same ranges as a <c>ScriptStep</c>'s.
/// </summary>
public sealed class JsValidatorOptions
{
    /// <summary>Default wall-clock limit of one validation, in milliseconds.</summary>
    public const int DefaultTimeoutMs = 2000;

    /// <summary>Wall-clock limit of one validation, in milliseconds (positive).</summary>
    public int TimeoutMs { get; set; } = DefaultTimeoutMs;

    /// <summary>V8 heap cap of one validation, in MB (8 to 128).</summary>
    public int MaxHeapMb { get; set; } = ScriptLimits.DefaultHeapMb;

    /// <summary>V8 stack cap of one validation, in KB (64 to 2048).</summary>
    public int MaxStackKb { get; set; } = ScriptLimits.DefaultStackKb;

    /// <summary>Most host function calls (script object methods) in one validation (0 to 10 000).</summary>
    public int MaxCalls { get; set; } = ScriptLimits.DefaultMaxCalls;

    /// <summary>Kilobytes exchanged with host functions in one validation (0 to 8192).</summary>
    public int MaxCallKb { get; set; } = ScriptLimits.DefaultMaxCallBytes / 1024;

    /// <summary>Why these options cannot be used, or null.</summary>
    public string? Problem()
        => TimeoutMs <= 0 ? $"'TimeoutMs' must be positive (was {TimeoutMs})."
            : MaxHeapMb is < ScriptLimits.MinHeapMb or > ScriptLimits.MaxHeapMbCeiling ? $"'MaxHeapMb' must be between {ScriptLimits.MinHeapMb} and {ScriptLimits.MaxHeapMbCeiling} (was {MaxHeapMb})."
            : MaxStackKb is < ScriptLimits.MinStackKb or > ScriptLimits.MaxStackKbCeiling ? $"'MaxStackKb' must be between {ScriptLimits.MinStackKb} and {ScriptLimits.MaxStackKbCeiling} (was {MaxStackKb})."
            : MaxCalls is < 0 or > ScriptLimits.MaxCallsCeiling ? $"'MaxCalls' must be between 0 and {ScriptLimits.MaxCallsCeiling} (was {MaxCalls})."
            : MaxCallKb is < 0 or > ScriptLimits.MaxCallBytesCeiling / 1024 ? $"'MaxCallKb' must be between 0 and {ScriptLimits.MaxCallBytesCeiling / 1024} (was {MaxCallKb})."
            : null;
}

/// <summary>The name of a JavaScript validator in the YAML: <c>js:&lt;library&gt;.&lt;function&gt;</c> (architecture §18.7).</summary>
public static partial class JsValidatorName
{
    /// <summary>The prefix (<see cref="IValidatorFactory.Prefix"/> of the JavaScript validators).</summary>
    public const string Prefix = "js:";

    /// <summary>
    /// Reads <c>js:&lt;library&gt;.&lt;function&gt;</c>: the library is what comes before the last dot (a library name may contain dots), the
    /// function a JavaScript identifier. Null and the reason when it is not one. <paramref name="kind"/> names what it is in the reason
    /// (<c>validator</c>, <c>hook</c>).
    /// </summary>
    public static (string Library, string Function)? Parse(string name, out string? problem, string kind = "validator")
    {
        problem = null;
        if (name is null || !name.StartsWith(Prefix, StringComparison.Ordinal))
        {
            problem = $"a JavaScript {kind} is named '{Prefix}<library>.<function>'.";
            return null;
        }

        string rest = name[Prefix.Length..].Trim();
        int dot = rest.LastIndexOf('.');
        string library = dot > 0 ? rest[..dot] : "";
        string function = dot > 0 ? rest[(dot + 1)..] : "";
        if (!ScriptLibraryFolder.IsValidName(library) || !Identifier().IsMatch(function))
        {
            problem = $"'{name}' is not '{Prefix}<library>.<function>' (a library name: letters, digits, '.', '_', '-'; a function: a JavaScript identifier).";
            return null;
        }

        return (library, function);
    }

    /// <summary>The name for <paramref name="library"/> and <paramref name="function"/>.</summary>
    public static string Of(string library, string function) => Prefix + library + "." + function;

    [GeneratedRegex(@"^[A-Za-z_$][A-Za-z0-9_$]*$")]
    private static partial Regex Identifier();
}

/// <summary>
/// Builds the <c>js:</c> validators of the YAML (<see cref="IValidatorFactory"/>): the library must be known to the host's
/// <see cref="IScriptCatalog"/> when the pipeline is built; whether it exports the function is checked when the run starts (the run's worker
/// lists it) and by the designer's validation.
/// </summary>
internal sealed class JsValidatorFactory(IServiceProvider services) : IValidatorFactory
{
    public string Prefix => JsValidatorName.Prefix;

    public IStepValidator CreateValidator(string name, StepBase step) => Create(name, step, result: false);

    public IResultValidator CreateResultValidator(string name, StepBase step) => Create(name, step, result: true);

    private JsValidator Create(string name, StepBase step, bool result)
    {
        (string library, string function) = JsValidatorName.Parse(name, out string? problem)
            ?? throw new InvalidOperationException(problem);
        IScriptCatalog? catalog = services.GetService<IScriptCatalog>();
        if (catalog?.FindLibrary(library) == null)
        {
            string known = string.Join(", ", catalog?.Libraries.Select(l => l.Name) ?? []);
            throw new InvalidOperationException(
                $"the JavaScript library '{library}' is not known to this host. Libraries: {(known.Length == 0 ? "(none)" : known)}.");
        }

        return new JsValidator(name.Trim(), library, function, step, result);
    }
}

/// <summary>
/// A JavaScript validator (architecture §18.7): the function <see cref="Function"/> exported by the library <see cref="Library"/>, run in the
/// run's script worker (the same process, bundle and limits per call as the ScriptSteps; a run without ScriptSteps starts one for its
/// validators). It receives one argument, <c>args</c>, made of facades over JSON: <c>args.result</c> (the result's value, read-only),
/// <c>args.vars</c> (the pipeline variables, read and write: the writes come back as a patch applied to <c>PipelineContext.Metadata</c> after
/// the call) and, for a step validator, <c>args.step</c> (<c>{ name }</c>, read-only). It returns what a C# validator returns:
/// <c>true</c>; <c>false</c>; an error text (empty = valid); or <c>{ isValid, error }</c>.
/// </summary>
/// <remarks>
/// A JavaScript error, a broken limit (time, heap; the worker is killed and the next call starts another) or a return of another shape ends
/// the step as a fatal error through <see cref="StepHookException"/>, as an exception of a C# validator does (no retry: the script would fail
/// the same way). The patch is applied only when the call returned a valid shape.
/// </remarks>
internal sealed class JsValidator(string name, string library, string function, StepBase step, bool result)
    : IStepValidator, IResultValidator, IPipelineRunHookProvider, IJsFunction
{
    /// <inheritdoc cref="IStepValidator.Id"/>
    public string Id { get; } = name;

    /// <summary>The library that exports the function.</summary>
    public string Library { get; } = library;

    /// <summary>The function's name.</summary>
    public string Function { get; } = function;

    /// <summary>The step it validates.</summary>
    public StepBase Step { get; } = step;

    /// <summary>True for a result validator (<c>resultValidators</c>), false for a step validator.</summary>
    public bool IsResultValidator { get; } = result;

    /// <inheritdoc/>
    public string Label => "validator";

    /// <inheritdoc/>
    IPipelineRunHook IPipelineRunHookProvider.RunHook => ScriptRunHook.Instance;

    /// <inheritdoc cref="IStepValidator.ValidateAsync"/>
    public async Task<(bool IsValid, string? Error)> ValidateAsync(IStepResult value, PipelineContext context, CancellationToken cancellationToken)
    {
        JObject payload;
        try
        {
            payload = new JObject
            {
                ["result"] = JsFunctionCall.ToJson(value.Value),
                ["step"] = IsResultValidator ? null : new JObject { ["name"] = Step.Name },
            };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw JsFunctionCall.Fail(this, $"the result could not be converted to JSON: {ex.Message}", ex);
        }

        JObject envelope = await JsFunctionCall.InvokeAsync(this, ScriptCallKinds.Validate, payload, context, cancellationToken);
        (bool isValid, string? error) = Map(envelope);
        JsFunctionCall.ApplyVariables(this, context, envelope);
        return (isValid, error);
    }

    /// <summary>The return of the function as <c>(IsValid, Error)</c> (see the type summary); another shape is a <see cref="StepHookException"/>.</summary>
    private (bool IsValid, string? Error) Map(JObject envelope)
    {
        string type = envelope.Value<string>("type") ?? "undefined";
        JToken? value = envelope["value"];
        switch (type)
        {
            case "boolean":
                return value!.Value<bool>() ? (true, null) : (false, $"JavaScript validator '{Id}' rejected the result.");
            case "string":
                string text = value!.Value<string>() ?? "";
                return string.IsNullOrWhiteSpace(text) ? (true, null) : (false, text);
            case "object" when value is JObject shape && shape["isValid"]?.Type == JTokenType.Boolean:
                bool valid = shape.Value<bool>("isValid");
                string? error = shape["error"] is { Type: not (JTokenType.Null or JTokenType.Undefined) } e
                    ? (e.Type == JTokenType.String ? e.Value<string>() : e.ToString(Formatting.None))
                    : null;
                return valid ? (true, null) : (false, string.IsNullOrWhiteSpace(error) ? $"JavaScript validator '{Id}' rejected the result." : error);
            default:
                throw JsFunctionCall.Fail(this,
                    $"it returned {(type == "object" ? "an object without a boolean 'isValid'" : type)}; a JavaScript validator returns true, false, " +
                    "an error message (empty = valid) or { isValid, error }.");
        }
    }
}

/// <summary>The kinds of call of a library function in the worker.</summary>
internal static class ScriptCallKinds
{
    /// <summary>A JavaScript validator.</summary>
    public const string Validate = "validate";

    /// <summary>A JavaScript hook.</summary>
    public const string Hook = "hook";
}

/// <summary>
/// Applies the patch of a validator's writes to <c>args.vars</c> (architecture §18.7): <c>[ { "op": "set", "path": [..], "value": .. } |
/// { "op": "delete", "path": [..] } ]</c>, in order. <c>path[0]</c> is the variable (a key of <see cref="PipelineContext.Metadata"/>); a longer
/// path walks into its JSON value (object properties by name, array items by index) and changes it in place.
/// </summary>
internal static class VariablePatch
{
    public static void Apply(PipelineContext context, JArray patch) => Apply(context.Metadata, patch);

    /// <summary>The same patch over any map of names to values (the writable members of a hook, for instance).</summary>
    public static void Apply(IDictionary<string, object?> target, JArray patch)
    {
        foreach (JToken entry in patch)
        {
            string op = entry.Value<string>("op") ?? "";
            if (entry["path"] is not JArray { Count: > 0 } path || path[0].Type != JTokenType.String)
            {
                throw new InvalidOperationException("a patch entry without a path.");
            }

            string key = path[0].Value<string>()!;
            JToken? value = entry["value"];
            if (path.Count == 1)
            {
                if (op == "set")
                {
                    target[key] = value?.DeepClone() ?? JValue.CreateNull();
                }
                else
                {
                    target.Remove(key);
                }

                continue;
            }

            if (!target.TryGetValue(key, out object? stored) || stored is not JContainer root)
            {
                throw new InvalidOperationException($"the variable '{key}' is not a JSON object or array any more.");
            }

            JToken parent = root;
            for (int i = 1; i < path.Count - 1; i++)
            {
                parent = Step(parent, path[i], key)
                    ?? throw new InvalidOperationException($"'{key}' has no {path[i]} any more.");
            }

            JToken last = path[^1]!;
            switch (parent)
            {
                case JObject obj when last.Type == JTokenType.String:
                    if (op == "set")
                    {
                        obj[last.Value<string>()!] = value?.DeepClone() ?? JValue.CreateNull();
                    }
                    else
                    {
                        obj.Remove(last.Value<string>()!);
                    }

                    break;
                case JArray array when last.Type == JTokenType.Integer && op == "set":
                    int index = last.Value<int>();
                    while (array.Count <= index)
                    {
                        array.Add(JValue.CreateNull());
                    }

                    array[index] = value?.DeepClone() ?? JValue.CreateNull();
                    break;
                default:
                    throw new InvalidOperationException($"cannot {op} {last} inside '{key}'.");
            }
        }
    }

    private static JToken? Step(JToken parent, JToken segment, string key) => parent switch
    {
        JObject obj when segment.Type == JTokenType.String => obj[segment.Value<string>()!],
        JArray array when segment.Type == JTokenType.Integer && segment.Value<int>() is var i && i >= 0 && i < array.Count => array[i],
        _ => throw new InvalidOperationException($"cannot walk into {segment} inside '{key}'."),
    };
}
