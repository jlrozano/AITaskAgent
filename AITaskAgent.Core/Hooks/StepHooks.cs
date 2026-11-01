using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.Observability.Events;
using Newtonsoft.Json.Linq;
using System.ComponentModel;
using System.Diagnostics;

namespace AITaskAgent.Core.Hooks;

/// <summary>
/// An "around" hook: receives its arguments and <paramref name="next"/>, the built-in behaviour. Not calling
/// <paramref name="next"/> replaces it; calling it before/after wraps it. Still supported; the designer writes the "after" form
/// (<see cref="HookForms"/>) instead.
/// </summary>
public delegate Task<T> HookAround<in TArgs, T>(TArgs args, Func<Task<T>> next);

/// <summary>Arguments of <see cref="StepHooks.PostProcess"/>: the result of one attempt, before validation.</summary>
/// <remarks>
/// In the "after" form (<c>Func&lt;PostProcessArgs, Task&gt;</c>) the hook changes <see cref="Result"/> (assigns another result, or leaves it
/// and the attempt's result goes on). In the around form it is the attempt's result, as before.
/// </remarks>
public sealed record PostProcessArgs(StepBase Step, IStepResult Input, IStepResult Result, PipelineContext Context, int Attempt, CancellationToken CancellationToken)
    : IHookOutcome<IStepResult>, IHookData<PostProcessArgs>
{
    /// <summary>The attempt's result; writable: the result that goes on to validation (assignable to the step's output type, or the step fails).</summary>
    public IStepResult Result { get; set; } = Result;

    /// <summary>True when the result carries suspension data (a hook usually skips it).</summary>
    public bool IsSuspended => Result.SuspendInfo != null;

    IStepResult IHookOutcome<IStepResult>.Outcome { get => Result; set => Result = value; }

    /// <inheritdoc/>
    public static IReadOnlyList<HookDataMember<PostProcessArgs>> DataMembers { get; } =
    [
        new("step", "object", a => new JObject { ["name"] = a.Step.Name }, properties: HookData.StepProperties),
        new("attempt", "number", a => a.Attempt),
        new("input", "JSON", a => HookData.ToJson(a.Input.Value)),
        new("result", "JSON", a => HookData.ToJson(a.Result.Value), (a, v) => HookData.WithValue(a.Result, v)),
        new("isSuspended", "boolean", a => a.IsSuspended),
    ];
}

/// <summary>Arguments of <see cref="StepHooks.OnFinalize"/>: the final result (success or error), once per invocation.</summary>
public sealed record FinalizeArgs(StepBase Step, IStepResult Result, PipelineContext Context, CancellationToken CancellationToken)
    : IHookData<FinalizeArgs>
{
    /// <inheritdoc/>
    public static IReadOnlyList<HookDataMember<FinalizeArgs>> DataMembers { get; } =
    [
        new("step", "object", a => new JObject { ["name"] = a.Step.Name }, properties: HookData.StepProperties),
        new("result", "JSON", a => a.Result.HasError ? null : HookData.ToJson(a.Result.Value)),
        new("hasError", "boolean", a => a.Result.HasError),
        new("error", "string | null", a => a.Result.Error?.Message),
    ];
}

/// <summary>Arguments of the activity enrichment hooks. <see cref="Input"/> is set before execution, <see cref="Result"/> after.</summary>
/// <remarks>
/// The data view (a JavaScript hook, architecture §18.8; added 2026-10-24) exposes the activity as data: its ids read-only, <c>displayName</c> and
/// <c>tags</c> writable and applied back to the real <see cref="System.Diagnostics.Activity"/> (<c>DisplayName</c>, <c>SetTag</c>). Without an
/// activity (nobody listens) <c>activity</c> is null, <c>tags</c> is empty and writes are ignored, as a C# hook could do nothing either.
/// </remarks>
public sealed record EnrichActivityArgs(StepBase Step, Activity? Activity, IStepResult? Input, IStepResult? Result, PipelineContext Context)
    : IHookData<EnrichActivityArgs>
{
    /// <inheritdoc/>
    public static IReadOnlyList<HookDataMember<EnrichActivityArgs>> DataMembers { get; } =
    [
        new("step", "object", a => new JObject { ["name"] = a.Step.Name }, properties: HookData.StepProperties),
        new("activity", "object | null", a => a.Activity is not { } activity ? null : new JObject
        {
            ["traceId"] = activity.TraceId.ToHexString(),
            ["spanId"] = activity.SpanId.ToHexString(),
            ["parentSpanId"] = activity.ParentSpanId == default ? null : activity.ParentSpanId.ToHexString(),
            ["operationName"] = activity.OperationName,
        }, properties: [new("traceId", "string"), new("spanId", "string"), new("parentSpanId", "string | null"), new("operationName", "string")]),
        new("displayName", "string | null", a => a.Activity?.DisplayName, (a, v) => EnrichData.SetDisplayName(a.Activity, v)),
        new("tags", "JSON", a => EnrichData.Tags(a.Activity), (a, v) => EnrichData.SetTags(a.Activity, v)),
        new("input", "JSON", a => a.Input == null ? null : HookData.ToJson(a.Input.Value)),
        new("result", "JSON", a => a.Result == null || a.Result.HasError ? null : HookData.ToJson(a.Result.Value)),
    ];
}

/// <summary>Arguments of <see cref="StepHooks.EnrichStarted"/>.</summary>
/// <remarks>
/// A C# hook returns the event to publish. A JavaScript hook (data view, 2026-10-24) changes <c>additionalData</c> and <c>suppressFromUser</c>;
/// the event is rebuilt from <see cref="BaseEvent"/> with them (<c>with</c>), through the explicit <see cref="IHookOutcome{T}"/>.
/// </remarks>
public sealed record EnrichStartedArgs(StepBase Step, StepStartedEvent BaseEvent, IStepResult Input, PipelineContext Context)
    : IHookOutcome<StepStartedEvent>, IHookData<EnrichStartedArgs>
{
    private StepStartedEvent _event = BaseEvent;

    StepStartedEvent IHookOutcome<StepStartedEvent>.Outcome { get => _event; set => _event = value ?? BaseEvent; }

    /// <inheritdoc/>
    public static IReadOnlyList<HookDataMember<EnrichStartedArgs>> DataMembers { get; } =
    [
        new("step", "object", a => new JObject { ["name"] = a.Step.Name }, properties: HookData.StepProperties),
        new("event", "object", a => new JObject
        {
            ["eventType"] = a._event.EventType,
            ["stepName"] = a._event.StepName,
            ["stepPath"] = a._event.StepPath,
            ["correlationId"] = a._event.CorrelationId,
            ["timestamp"] = a._event.Timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["attemptNumber"] = a._event.AttemptNumber,
        }, properties: [new("eventType", "string"), new("stepName", "string"), new("stepPath", "string | null"), new("correlationId", "string | null"),
            new("timestamp", "string"), new("attemptNumber", "number")]),
        new("additionalData", "JSON", a => EnrichData.ToJson(a._event.AdditionalData),
            (a, v) => a._event = a._event with { AdditionalData = EnrichData.Merge(a._event.AdditionalData, v, keepNull: false)?.ToDictionary(e => e.Key, e => e.Value!) }),
        new("suppressFromUser", "boolean", a => a._event.SuppressFromUser, (a, v) => a._event = a._event with { SuppressFromUser = EnrichData.Bool(v, "suppressFromUser") }),
        new("input", "JSON", a => HookData.ToJson(a.Input.Value)),
    ];
}

/// <summary>Arguments of <see cref="StepHooks.EnrichCompleted"/>.</summary>
/// <remarks>
/// A C# hook returns the event to publish. A JavaScript hook (data view, 2026-10-24) changes <c>additionalData</c> and <c>suppressFromUser</c>; the
/// event is rebuilt with them when it is the framework's <see cref="StepCompletedEvent"/> (a sealed record, <c>with</c>). Another
/// <see cref="IStepCompletedEvent"/> cannot be rebuilt faithfully: a write is refused (the hook fails, which an enrichment hook logs and ignores).
/// </remarks>
public sealed record EnrichCompletedArgs(StepBase Step, IStepCompletedEvent BaseEvent, IStepResult Result, PipelineContext Context)
    : IHookOutcome<IStepCompletedEvent>, IHookData<EnrichCompletedArgs>
{
    private IStepCompletedEvent _event = BaseEvent;

    IStepCompletedEvent IHookOutcome<IStepCompletedEvent>.Outcome { get => _event; set => _event = value ?? BaseEvent; }

    private StepCompletedEvent Rebuildable(string member) => _event as StepCompletedEvent
        ?? throw new InvalidOperationException($"args.{member} cannot be written: the completed event is a {_event.GetType().Name}, which the framework cannot rebuild.");

    /// <inheritdoc/>
    public static IReadOnlyList<HookDataMember<EnrichCompletedArgs>> DataMembers { get; } =
    [
        new("step", "object", a => new JObject { ["name"] = a.Step.Name }, properties: HookData.StepProperties),
        new("event", "object", a => new JObject
        {
            ["eventType"] = a._event.EventType,
            ["stepName"] = a._event.StepName,
            ["stepPath"] = (a._event as StepCompletedEvent)?.StepPath,
            ["correlationId"] = a._event.CorrelationId,
            ["timestamp"] = a._event.Timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["success"] = a._event is StepCompletedEvent completed ? completed.Success : null,
            ["durationMs"] = a._event.Duration.TotalMilliseconds,
            ["errorMessage"] = a._event.ErrorMessage,
        }, properties: [new("eventType", "string"), new("stepName", "string"), new("stepPath", "string | null"), new("correlationId", "string | null"),
            new("timestamp", "string"), new("success", "boolean | null"), new("durationMs", "number"), new("errorMessage", "string | null")]),
        new("additionalData", "JSON", a => EnrichData.ToJson(a._event.AdditionalData),
            (a, v) => a._event = a.Rebuildable("additionalData") with { AdditionalData = EnrichData.Merge(a._event.AdditionalData, v, keepNull: true) ?? [] }),
        new("suppressFromUser", "boolean", a => a._event.SuppressFromUser,
            (a, v) => a._event = a.Rebuildable("suppressFromUser") with { SuppressFromUser = EnrichData.Bool(v, "suppressFromUser") }),
        new("result", "JSON", a => a.Result.HasError ? null : HookData.ToJson(a.Result.Value)),
        new("hasError", "boolean", a => a.Result.HasError),
    ];
}

/// <summary>Arguments of a step validator (YAML <c>validators</c>).</summary>
public sealed record ValidateArgs(StepBase Step, IStepResult Result, PipelineContext Context, CancellationToken CancellationToken)
    : IHookData<ValidateArgs>
{
    /// <summary>Typed view of the result's value (<c>ToObject</c> for JSON values).</summary>
    public T? As<T>() => HookValues.As<T>(Result.Value);

    /// <inheritdoc/>
    /// <remarks>What a JavaScript validator receives besides <c>args.vars</c> (architecture §18.7).</remarks>
    public static IReadOnlyList<HookDataMember<ValidateArgs>> DataMembers { get; } =
    [
        new("result", "JSON", a => HookData.ToJson(a.Result.Value)),
        new("step", "object", a => new JObject { ["name"] = a.Step.Name }, properties: HookData.StepProperties),
    ];
}

/// <summary>Arguments of a result validator (YAML <c>resultValidators</c>).</summary>
public sealed record ResultValidateArgs(IStepResult Result, PipelineContext Context, CancellationToken CancellationToken)
    : IHookData<ResultValidateArgs>
{
    /// <summary>The result's value.</summary>
    public object? Value => Result.Value;

    /// <summary>Typed view of the result's value (<c>ToObject</c> for JSON values).</summary>
    public T? As<T>() => HookValues.As<T>(Result.Value);

    /// <inheritdoc/>
    /// <remarks>What a JavaScript result validator receives besides <c>args.vars</c> (architecture §18.7).</remarks>
    public static IReadOnlyList<HookDataMember<ResultValidateArgs>> DataMembers { get; } =
    [
        new("result", "JSON", a => HookData.ToJson(a.Result.Value)),
    ];
}

/// <summary>The data of the enrichment hooks: activity tags and event additional data as JSON, and their writes applied back.</summary>
internal static class EnrichData
{
    /// <summary>A value as JSON, never failing: what JSON cannot represent becomes its text.</summary>
    public static JToken? Safe(object? value)
    {
        try
        {
            return HookData.ToJson(value);
        }
        catch (Exception ex) when (ex is Newtonsoft.Json.JsonException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return value?.ToString();
        }
    }

    /// <summary>A dictionary of additional data as a JSON object (null stays null).</summary>
    public static JToken? ToJson<TValue>(IReadOnlyDictionary<string, TValue>? data)
    {
        if (data == null)
        {
            return null;
        }

        JObject json = [];
        foreach ((string key, TValue value) in data)
        {
            json[key] = Safe(value) ?? JValue.CreateNull();
        }

        return json;
    }

    /// <summary>
    /// The additional data a hook wrote: <paramref name="value"/> must be an object (or null: no data). An entry whose JSON did not change keeps
    /// its original object (a decimal stays a decimal); a new or changed one is the JSON converted (<see cref="HookData.Convert"/> to object); a JSON
    /// null removes the entry unless <paramref name="keepNull"/>.
    /// </summary>
    public static Dictionary<string, object?>? Merge<TValue>(IReadOnlyDictionary<string, TValue>? current, JToken? value, bool keepNull)
    {
        if (value == null || value.Type is JTokenType.Null or JTokenType.Undefined)
        {
            return null;
        }

        if (value is not JObject json)
        {
            throw new InvalidOperationException($"args.additionalData must be an object or null, not a {value.Type}.");
        }

        Dictionary<string, object?> merged = new(StringComparer.Ordinal);
        foreach (JProperty property in json.Properties())
        {
            bool isNull = property.Value.Type is JTokenType.Null or JTokenType.Undefined;
            if (current != null && current.TryGetValue(property.Name, out TValue? original) && JToken.DeepEquals(Safe(original) ?? JValue.CreateNull(), property.Value))
            {
                merged[property.Name] = original;
            }
            else if (!isNull)
            {
                merged[property.Name] = HookData.Convert(property.Value, typeof(object));
            }
            else if (keepNull)
            {
                merged[property.Name] = null;
            }
        }

        return merged;
    }

    /// <summary>A JSON boolean, or an error naming the member.</summary>
    public static bool Bool(JToken? value, string member)
        => value is { Type: JTokenType.Boolean } ? value.Value<bool>() : throw new InvalidOperationException($"args.{member} takes true or false.");

    /// <summary>The tags of the activity as a JSON object; empty without an activity.</summary>
    public static JToken Tags(Activity? activity)
    {
        JObject tags = [];
        foreach ((string key, object? value) in activity?.TagObjects ?? [])
        {
            tags[key] = Safe(value) ?? JValue.CreateNull();
        }

        return tags;
    }

    /// <summary>
    /// Applies the tags a hook left: a tag whose JSON changed or is new is set (<see cref="Activity.SetTag"/>; a scalar as its value, an array or
    /// object as its compact JSON text), a tag that is no longer there or is null is removed. Without an activity nothing happens.
    /// </summary>
    public static void SetTags(Activity? activity, JToken? value)
    {
        if (activity == null)
        {
            return;
        }

        if (value is not JObject tags)
        {
            throw new InvalidOperationException("args.tags must be an object (tag name to value).");
        }

        Dictionary<string, object?> current = [];
        foreach ((string key, object? tag) in activity.TagObjects)
        {
            current[key] = tag;
        }

        foreach ((string key, object? tag) in current)
        {
            if (!tags.ContainsKey(key))
            {
                activity.SetTag(key, null);
            }
        }

        foreach (JProperty property in tags.Properties())
        {
            if (current.TryGetValue(property.Name, out object? original) && JToken.DeepEquals(Safe(original) ?? JValue.CreateNull(), property.Value))
            {
                continue;
            }

            activity.SetTag(property.Name, property.Value switch
            {
                { Type: JTokenType.Null or JTokenType.Undefined } => null,
                JValue scalar => scalar.Value,
                _ => property.Value.ToString(Newtonsoft.Json.Formatting.None),
            });
        }
    }

    /// <summary>Sets the display name of the activity (null or empty: its operation name). Without an activity nothing happens.</summary>
    public static void SetDisplayName(Activity? activity, JToken? value)
    {
        if (activity != null)
        {
            activity.DisplayName = HookData.ToText(value) is { Length: > 0 } text ? text : activity.OperationName;
        }
    }
}

internal static class HookValues
{
    public static T? As<T>(object? value) => value switch
    {
        null => default,
        T typed => typed,
        Newtonsoft.Json.Linq.JToken token => token.ToObject<T>(),
        _ => Newtonsoft.Json.Linq.JToken.FromObject(value).ToObject<T>(),
    };
}

/// <summary>
/// Hooks every <see cref="StepBase"/> accepts (YAML <c>hooks:</c>). Immutable: it is replaced, never
/// mutated, because the step instance is shared by concurrent runs and parallel branches; delegates must
/// be re-entrant and keep their state in the <see cref="PipelineContext"/>.
/// </summary>
/// <remarks>
/// A hook is the default implementation of the matching virtual method, one layer above the built-in
/// behaviour; an override that does not call <c>base</c> replaces the hook too. Notification hooks
/// (<see cref="OnFinalize"/>) fire at the call site and cannot be bypassed. A slot typed <see cref="HookAround{TArgs, T}"/> whose
/// arguments are <see cref="IHookOutcome{T}"/> also binds (from YAML, through <see cref="HookForms.After{TArgs, T}"/>) a hook of the
/// "after" form <c>Func&lt;TArgs, Task&gt;</c>: it runs at the end of the virtual method, once the step's own value is in the arguments,
/// and changes that value instead of calling <c>next</c> (architecture §14.6).
/// </remarks>
public record StepHooks
{
    /// <summary>
    /// After <c>ExecuteAsync</c> of every attempt that returned no error, before validation. The result that goes on must be assignable to
    /// the step's output type. From YAML it binds either form: <c>Func&lt;PostProcessArgs, Task&gt;</c> (runs at the end of
    /// <c>PostProcessAsync</c> and changes <see cref="PostProcessArgs.Result"/>) or this around form.
    /// </summary>
    [Description("After each successful attempt, before validation: may replace args.Result.")]
    [HookVirtual("PostProcessAsync")]
    public HookAround<PostProcessArgs, IStepResult>? PostProcess { get; init; }

    /// <summary>Once, after the last attempt, with the final result. An exception fails the step.</summary>
    [Description("Once, after the last attempt (success or error).")]
    public Func<FinalizeArgs, Task>? OnFinalize { get; init; }

    /// <summary>Adds activity tags before execution. Exceptions are logged and swallowed.</summary>
    [Description("Adds OpenTelemetry tags before execution.")]
    [HookVirtual("EnrichActivityBefore")]
    public Action<EnrichActivityArgs>? EnrichActivityBefore { get; init; }

    /// <summary>Adds activity tags after execution. Exceptions are logged and swallowed.</summary>
    [Description("Adds OpenTelemetry tags after execution.")]
    [HookVirtual("EnrichActivityAfter")]
    public Action<EnrichActivityArgs>? EnrichActivityAfter { get; init; }

    /// <summary>Transforms the started event. Exceptions are logged and swallowed (the base event is used).</summary>
    [Description("Transforms the step-started event.")]
    [HookVirtual("EnrichStartedEvent")]
    public Func<EnrichStartedArgs, StepStartedEvent>? EnrichStarted { get; init; }

    /// <summary>Transforms the completed event. Exceptions are logged and swallowed (the base event is used).</summary>
    [Description("Transforms the step-completed event.")]
    [HookVirtual("EnrichCompletedEvent")]
    public Func<EnrichCompletedArgs, IStepCompletedEvent>? EnrichCompleted { get; init; }
}

/// <summary>Marks a hook slot with the virtual method of the step whose default implementation dispatches it (used by the R1 test and the R3 warning).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class HookVirtualAttribute(string methodName) : Attribute
{
    public string MethodName { get; } = methodName;
}

/// <summary>
/// Declares the <see cref="StepHooks"/> subtype a step type binds its <c>hooks:</c> to. The most derived
/// declaration wins; steps without one use <see cref="StepHooks"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class HooksTypeAttribute(Type hooksType) : Attribute
{
    public Type HooksType { get; } = hooksType;
}
