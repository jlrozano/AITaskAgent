using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using Newtonsoft.Json.Linq;
using System.ComponentModel;

namespace AITaskAgent.LLM.Steps;

/// <summary>Arguments of <c>buildUserMessage</c> / <c>buildSystemMessage</c>.</summary>
public sealed record BuildMessageArgs(BaseLlmStep Step, IStepResult Input, PipelineContext Context)
    : IHookOutcome<string?>, IHookData<BuildMessageArgs>
{
    /// <summary>
    /// The message that is sent; writable. A hook of the after form finds the step's own message here (<c>Prompt</c>, or the rendered
    /// <c>userPrompt</c>/<c>systemPrompt</c>) and changes it; null or empty sends none. Null in an around hook (the step's own one is <c>next</c>).
    /// </summary>
    public string? Message { get; set; }

    string? IHookOutcome<string?>.Outcome { get => Message; set => Message = value; }

    /// <inheritdoc/>
    public static IReadOnlyList<HookDataMember<BuildMessageArgs>> DataMembers { get; } =
    [
        new("step", "object", a => new JObject { ["name"] = a.Step.Name }, properties: HookData.StepProperties),
        new("input", "JSON", a => HookData.ToJson(a.Input.Value)),
        new("message", "string | null", a => a.Message, (a, v) => a.Message = HookData.ToText(v)),
    ];
}

/// <summary>Arguments of <c>configureRequest</c>.</summary>
/// <remarks>
/// A C# hook returns the request to send. A JavaScript hook (data view, 2026-10-24) reads the request as data and changes its writable members; each
/// write builds a new request from the current one (<c>with</c>), through the explicit <see cref="IHookOutcome{T}"/>. They are applied in the order
/// of <see cref="DataMembers"/>: <c>options</c> first (the whole object replaces the options), then <c>model</c>, <c>systemPrompt</c>,
/// <c>temperature</c>, <c>topP</c> and <c>maxTokens</c>, which live in the options too and therefore win over a value written there.
/// </remarks>
public sealed record ConfigureRequestArgs(BaseLlmStep Step, LlmRequest Request, PipelineContext Context)
    : IHookOutcome<LlmRequest>, IHookData<ConfigureRequestArgs>
{
    private LlmRequest _request = Request;

    LlmRequest IHookOutcome<LlmRequest>.Outcome { get => _request; set => _request = value ?? Request; }

    /// <inheritdoc/>
    public static IReadOnlyList<HookDataMember<ConfigureRequestArgs>> DataMembers { get; } =
    [
        new("step", "object", a => new JObject { ["name"] = a.Step.Name }, properties: HookData.StepProperties),
        new("messages", "JSON", a => LlmHookData.Messages(a._request.Conversation.History.Messages)),
        new("tools", "JSON", a => new JArray((a._request.Tools ?? []).Select(t => t.Name))),
        new("options", "JSON", a => a._request.Options.DeepClone(), (a, v) => a._request = LlmHookData.WithOptions(a._request, v)),
        new("model", "string", a => a._request.Model, (a, v) => a._request = a._request with
        {
            Model = HookData.ToText(v) is { Length: > 0 } model && !string.IsNullOrWhiteSpace(model) ? model : throw new InvalidOperationException("args.model cannot be empty."),
        }),
        new("systemPrompt", "string | null", a => a._request.SystemPrompt, (a, v) => a._request = a._request with { SystemPrompt = HookData.ToText(v) }),
        new("temperature", "number | null", a => a._request.Temperature, (a, v) => a._request = a._request with { Temperature = LlmHookData.Number<float>(v, "temperature") }),
        new("topP", "number | null", a => a._request.TopP, (a, v) => a._request = a._request with { TopP = LlmHookData.Number<float>(v, "topP") }),
        new("maxTokens", "number | null", a => a._request.MaxTokens, (a, v) => a._request = a._request with { MaxTokens = LlmHookData.Number<int>(v, "maxTokens") }),
    ];
}

/// <summary>Arguments of <c>selectConversation</c> (added 2026-10-24).</summary>
/// <remarks>
/// <para>Which conversation the step uses in this round: the context's (shared history, <see cref="UseHistory"/> true) or a fresh empty one
/// (false, built with the history's <c>MaxTokens</c> and token counter, as a <c>YamlLlmStep</c> with <c>useConversationHistory: false</c>).
/// <see cref="UseHistory"/> starts with the step's own choice (what <c>GetConversationContext</c> returned: true for a <c>BaseLlmStep</c>, the
/// <c>UseConversationHistory</c> property for a <c>YamlLlmStep</c>); the hook changes it or leaves it.</para>
/// <para>Runs on the first attempt of every round, so again after a provider fallback restarts the invocation: it must be idempotent.</para>
/// </remarks>
public sealed record SelectConversationArgs(
    BaseLlmStep Step, IStepResult Input, PipelineContext Context, IReadOnlyList<Message> History, int Attempt, CancellationToken CancellationToken)
    : IHookData<SelectConversationArgs>
{
    /// <summary>Writable: true uses the context's conversation (the history), false a fresh empty one. Starts with the step's own choice.</summary>
    public bool UseHistory { get; set; } = true;

    /// <inheritdoc/>
    public static IReadOnlyList<HookDataMember<SelectConversationArgs>> DataMembers { get; } =
    [
        new("step", "object", a => new JObject { ["name"] = a.Step.Name }, properties: HookData.StepProperties),
        new("attempt", "number", a => a.Attempt),
        new("input", "JSON", a => HookData.ToJson(a.Input.Value)),
        new("history", "JSON", a => LlmHookData.Messages(a.History)),
        new("useHistory", "boolean", a => a.UseHistory, (a, v) => a.UseHistory = v is { Type: JTokenType.Boolean }
            ? v.Value<bool>()
            : throw new InvalidOperationException("args.useHistory takes true or false.")),
    ];
}

/// <summary>Data of the LLM hooks' views: messages and request options as JSON, and the writes applied back.</summary>
internal static class LlmHookData
{
    /// <summary>Messages as JSON: <c>role</c>, <c>content</c> and, when present, <c>name</c>, <c>toolCallId</c> and <c>toolCalls</c> (<c>id</c>, <c>name</c>, <c>arguments</c>).</summary>
    public static JArray Messages(IEnumerable<Message> messages)
    {
        JArray list = [];
        foreach (Message message in messages)
        {
            JObject item = new() { ["role"] = message.Role, ["content"] = message.Content };
            if (message.Name != null)
            {
                item["name"] = message.Name;
            }

            if (message.ToolCallId != null)
            {
                item["toolCallId"] = message.ToolCallId;
            }

            if (message.ToolCalls is { Count: > 0 } calls)
            {
                item["toolCalls"] = new JArray(calls.Select(c => new JObject { ["id"] = c.Id, ["name"] = c.Name, ["arguments"] = c.Arguments }));
            }

            list.Add(item);
        }

        return list;
    }

    /// <summary>The request with its options replaced by <paramref name="value"/> (an object; the facade properties live there too).</summary>
    public static LlmRequest WithOptions(LlmRequest request, JToken? value)
    {
        if (value is not JObject options)
        {
            throw new InvalidOperationException("args.options must be an object.");
        }

        // The copy constructor clones the options, so emptying the copy's leaves the original untouched.
        LlmRequest copy = request with { };
        copy.Options.RemoveAll();
        foreach (JProperty property in options.Properties())
        {
            copy.Options[property.Name] = property.Value.DeepClone();
        }

        return copy;
    }

    /// <summary>A JSON number (or null) as <typeparamref name="T"/>, or an error naming the member.</summary>
    public static T? Number<T>(JToken? value, string member) where T : struct
        => value == null || value.Type is JTokenType.Null or JTokenType.Undefined ? null
            : value.Type is JTokenType.Integer or JTokenType.Float ? value.ToObject<T>()
            : throw new InvalidOperationException($"args.{member} takes a number or null.");
}

/// <summary>Arguments of <c>parseResponse</c>.</summary>
public sealed record ParseResponseArgs(BaseLlmStep Step, LlmResult Response, PipelineContext Context)
    : IHookOutcome<(IStepResult? Result, string? Error)>, IHookData<ParseResponseArgs>
{
    /// <summary>
    /// The step's result; writable. A hook of the after form finds what the built-in parsing produced (null when it failed) and may replace
    /// it. Null in an around hook.
    /// </summary>
    public IStepResult? Result { get; set; }

    /// <summary>Why the response is not valid (the attempt ends in error and the step retries); writable. Null when the parsing succeeded.</summary>
    public string? Error { get; set; }

    (IStepResult? Result, string? Error) IHookOutcome<(IStepResult? Result, string? Error)>.Outcome
    {
        get => (Result, Error);
        set => (Result, Error) = value;
    }

    /// <inheritdoc/>
    public static IReadOnlyList<HookDataMember<ParseResponseArgs>> DataMembers { get; } =
    [
        new("step", "object", a => new JObject { ["name"] = a.Step.Name }, properties: HookData.StepProperties),
        new("response", "object", a => new JObject
        {
            ["content"] = a.Response.Content,
            ["finishReason"] = a.Response.FinishReason?.ToString(),
            ["model"] = a.Response.Model,
            ["tokensUsed"] = a.Response.TokensUsed,
        }, properties: [new("content", "string"), new("finishReason", "string | null"), new("model", "string | null"), new("tokensUsed", "number | null")]),
        new("result", "JSON", a => a.Result == null ? null : HookData.ToJson(a.Result.Value), (a, v) => a.Result = a.Step.ResultFromJson(v, a.Response)),
        new("error", "string | null", a => a.Error, (a, v) => a.Error = HookData.ToText(v) is { Length: > 0 } text ? text : null),
    ];
}

/// <summary>Arguments of <c>onConversationCreated</c>.</summary>
public sealed record ConversationCreatedArgs(BaseLlmStep Step, PipelineContext Context, ConversationContext Conversation, int Attempt);

/// <summary>Why the tool loop ended without an answer.</summary>
public enum ToolLimitReason
{
    /// <summary><c>MaxToolIterations</c> was reached.</summary>
    IterationLimit,

    /// <summary>The repeat detector or a tool (<c>ToolLoopStopRequestedException</c>) cut the loop.</summary>
    LoopCut,
}

/// <summary>Arguments of <c>onToolLimit</c>.</summary>
/// <remarks>
/// A C# hook returns its <see cref="ToolLimitDecision"/>. A hook in another language (JavaScript) writes <c>args.finalAnswerPrompt</c> instead
/// (the explicit <see cref="IHookOutcome{T}"/>): left null, the decision is <see cref="ToolLimitDecision.Fail"/>.
/// </remarks>
public sealed record ToolLimitArgs(BaseLlmStep Step, PipelineContext Context, ToolLimitReason Reason, string Message, int MaxToolIterations)
    : IHookOutcome<ToolLimitDecision>, IHookData<ToolLimitArgs>
{
    private ToolLimitDecision _decision = ToolLimitDecision.Fail;

    ToolLimitDecision IHookOutcome<ToolLimitDecision>.Outcome { get => _decision; set => _decision = value ?? ToolLimitDecision.Fail; }

    /// <inheritdoc/>
    public static IReadOnlyList<HookDataMember<ToolLimitArgs>> DataMembers { get; } =
    [
        new("step", "object", a => new JObject { ["name"] = a.Step.Name }, properties: HookData.StepProperties),
        new("reason", "string", a => a.Reason.ToString()),
        new("message", "string", a => a.Message),
        new("maxToolIterations", "number", a => a.MaxToolIterations),
        new("finalAnswerPrompt", "string | null", a => a._decision.FinalAnswerPrompt, (a, v) =>
            a._decision = HookData.ToText(v) is { } prompt && !string.IsNullOrWhiteSpace(prompt) ? ToolLimitDecision.FinalAnswer(prompt) : ToolLimitDecision.Fail),
    ];
}

/// <summary>What <c>onToolLimit</c> decides: keep the existing behaviour, or ask the model for a final answer without tools.</summary>
public sealed record ToolLimitDecision
{
    private ToolLimitDecision(string? finalAnswerPrompt) => FinalAnswerPrompt = finalAnswerPrompt;

    /// <summary>The user message of the final-answer call; null for <see cref="Fail"/>.</summary>
    public string? FinalAnswerPrompt { get; }

    /// <summary>Keep today's behaviour: the iteration limit is an error, a loop cut is the answer.</summary>
    public static ToolLimitDecision Fail { get; } = new((string?)null);

    /// <summary>Add <paramref name="prompt"/> as a user message and call the model once more with <c>tool_choice: none</c>.</summary>
    public static ToolLimitDecision FinalAnswer(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return new ToolLimitDecision(prompt);
    }
}

/// <summary>Hooks of the LLM steps (YAML <c>hooks:</c> on an <c>LlmStep</c>), besides the generic <see cref="StepHooks"/>.</summary>
public record LlmStepHooks : StepHooks
{
    /// <summary>
    /// Builds the user message. The step's own message is <c>Prompt</c>, or the rendered <c>userPrompt</c>: <c>next</c> of an around hook, or
    /// <see cref="BuildMessageArgs.Message"/> for a hook of the after form (<c>Func&lt;BuildMessageArgs, Task&gt;</c>), which changes it.
    /// </summary>
    [Description("Builds the user message: args.Message holds the step's own one; change it.")]
    [HookVirtual("BuildUserMessageAsync")]
    public HookAround<BuildMessageArgs, string?>? BuildUserMessage { get; init; }

    /// <summary>Builds the system message: as <see cref="BuildUserMessage"/>, with the step's own system message.</summary>
    [Description("Builds the system message: args.Message holds the step's own one; change it.")]
    [HookVirtual("BuildSystemMessageAsync")]
    public HookAround<BuildMessageArgs, string?>? BuildSystemMessage { get; init; }

    /// <summary>Tweaks the request (temperature, options...) before the JSON response format is applied.</summary>
    [Description("Tweaks the LLM request before it is sent.")]
    [HookVirtual("ConfigureLlmRequest")]
    public Func<ConfigureRequestArgs, LlmRequest>? ConfigureRequest { get; init; }

    /// <summary>
    /// Parses the model response. The built-in parsing is <c>next</c> of an around hook; a hook of the after form
    /// (<c>Func&lt;ParseResponseArgs, Task&gt;</c>) finds its outcome in <see cref="ParseResponseArgs.Result"/> and
    /// <see cref="ParseResponseArgs.Error"/> and may change them.
    /// </summary>
    [Description("Parses the model response: args.Result / args.Error hold the built-in parsing; change them.")]
    [HookVirtual("ParseLlmResponseAsync")]
    public HookAround<ParseResponseArgs, (IStepResult? Result, string? Error)>? ParseResponse { get; init; }

    /// <summary>
    /// Decides the conversation of the round (added 2026-10-24): dispatched at the call site in <c>ExecuteAsync</c>, on the first attempt of every
    /// round, right after <c>GetConversationContext</c> (whose choice is the default of <see cref="SelectConversationArgs.UseHistory"/>) and before the
    /// round's bookmark, <see cref="OnConversationCreated"/> and the user message. Not inside the virtual: an override of
    /// <c>GetConversationContext</c> (<c>YamlLlmStep</c>, <c>StatelessTemplateLlmStep</c>, a host's step) cannot skip it. Must be idempotent: it runs
    /// again after a provider fallback restarts the invocation. An exception is a <see cref="StepHookException"/>: fatal, no retry, no fallback.
    /// </summary>
    [Description("Decides whether the round uses the shared history (args.UseHistory) or a fresh empty conversation; must be idempotent.")]
    public Func<SelectConversationArgs, Task>? SelectConversation { get; init; }

    /// <summary>
    /// Notification, on the first attempt of every round, after the conversation is chosen. Must be idempotent:
    /// it also fires again after a provider fallback restarts the invocation.
    /// </summary>
    [Description("Notification after the conversation is chosen (first attempt of each round); must be idempotent.")]
    public Func<ConversationCreatedArgs, Task>? OnConversationCreated { get; init; }

    /// <summary>Decides what to do when the tool loop ends without an answer.</summary>
    [Description("Decides what to do when the tool loop ends without an answer: fail or ask for a final answer.")]
    public Func<ToolLimitArgs, Task<ToolLimitDecision>>? OnToolLimit { get; init; }
}
