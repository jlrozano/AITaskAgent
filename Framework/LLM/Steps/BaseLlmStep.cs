using AITaskAgent.Configuration;
using AITaskAgent.Core;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Base;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Streaming;
using AITaskAgent.LLM.Support;
using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.Support.JSON;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace AITaskAgent.LLM.Steps;


/// <summary>
/// Non-generic base for ALL LLM steps.
/// Contains the complete execution engine: retry with bookmark, tool loop, telemetry,
/// streaming, JSON response configuration, and parsing.
///
/// Subclasses supply prompts by overriding:
///   - BuildUserMessageAsync  (abstract)
///   - BuildSystemMessageAsync (virtual, default = no system prompt)
///
/// Subclasses customise the request via the existing hook:
///   - ConfigureLlmRequest (virtual)
/// </summary>
public abstract partial class BaseLlmStep(
    string name,
    LlmRole role,
    Type inputType,
    Type outputType,
    string prompt,
    string? systemPrompt = null
    ) : StepBase(name, inputType, outputType), IFallbackable
{
    /// <summary>
    /// Active role. Held in a field rather than read from the constructor parameter so that
    /// steps built before their role is known - YAML steps, deserialized by
    /// <c>YamlPipelineFactory</c> before the profile is resolved - can have it supplied later
    /// via <see cref="SetRole"/>.
    /// </summary>
    private LlmRole _role = role;

    /// <summary>
    /// Replaces the role after construction. Intended for deferred resolution; steps constructed
    /// with a real role never need it. Refreshes <see cref="Tools"/> and
    /// <see cref="SystemPrompt"/> only when they are still unset, so anything configured
    /// explicitly survives.
    /// </summary>
    protected internal void SetRole(LlmRole newRole)
    {
        _role = newRole ?? throw new ArgumentNullException(nameof(newRole));

        if (Tools.Count == 0 && newRole.Tools is { Count: > 0 })
        {
            Tools = newRole.Tools;
        }

        if (string.IsNullOrWhiteSpace(SystemPrompt))
        {
            SystemPrompt = newRole.SystemPrompt;
        }
    }

    protected ILlmService GetLlmService(PipelineContext context)
    {
        var idx = GetState(context).FallbackIndex;
        return (idx == 0 ? _role.Provider.Service :
            _role.Fallbacks?[idx - 1].Provider.Service) ?? new NullLlmService();
    }

    protected string GetModel(PipelineContext context)
    {
        var idx = GetState(context).FallbackIndex;
        return idx == 0 ? _role.Model :
            _role.Fallbacks?[idx - 1].Model
            ?? throw new InvalidOperationException("Model not set");
    }

    protected LlmProvider GetProvider(PipelineContext context)
    {
        var idx = GetState(context).FallbackIndex;
        return idx == 0 ? _role.Provider :
            _role.Fallbacks?[idx - 1].Provider
            ?? throw new InvalidOperationException("Provider not set");
    }

    protected LlmInferenceSettings GetInference(PipelineContext context)
    {
        var idx = GetState(context).FallbackIndex;
        return (idx == 0 ? _role.Inference : _role.Fallbacks?[idx - 1].Inference)
            ?? GetProvider(context).Inference ?? new();
    }

    public int MaxToolIterations { get; init; } = 5;

    public IList<ITool> Tools { get; set; } = role.Tools ?? [];

    /// <summary>
    /// Handlers for in-text action tags (e.g. <c>&lt;write_file&gt;</c>). Their instructions are
    /// injected into the system prompt and every response - streamed or not, reasoning or content -
    /// is scanned for their tags.
    /// </summary>
    public List<ITagHandler> TagHandlers { get; init; } = [];

    /// <summary>
    /// Everything one invocation of this step needs to remember between <see cref="ExecuteAsync"/>
    /// attempts and <see cref="FinalizeAsync"/>. Lives in <see cref="PipelineContext.StepState"/>,
    /// never in an instance field: this same step object is registered once and reused for every
    /// run, and two concurrent runs (or two branches of the same <c>ParallelStep</c>) each get
    /// their own <see cref="PipelineContext"/>, so this is what actually keeps them from
    /// overwriting each other's conversation, bookmark and fallback index mid-flight.
    /// </summary>
    private sealed class InvocationState
    {
        public int FallbackIndex;
        public ConversationContext? Conversation;
        public string? InitialBookmark;
        public string Message = "";
        public LlmRequest? Request;
    }

    /// <summary>
    /// Identifies this step's slot in <see cref="PipelineContext.StepState"/>. The path already
    /// distinguishes concurrent branches and repeated step names at different levels of a
    /// composite pipeline; a context never attached to one (a step invoked directly, as several
    /// samples do) falls back to <see cref="StepBase.Name"/>, which is still unique enough for a
    /// context that only ever runs one step invocation at a time.
    /// </summary>
    private string StateKey(PipelineContext context) =>
        string.IsNullOrEmpty(context.CurrentStepPath) ? Name : context.CurrentStepPath;

    private InvocationState GetState(PipelineContext context)
    {
        var key = StateKey(context);
        if (context.StepState.TryGetValue(key, out var existing) && existing is InvocationState state)
        {
            return state;
        }
        state = new InvocationState();
        context.StepState[key] = state;
        return state;
    }

    public string? SystemPrompt { get; set; } = string.IsNullOrWhiteSpace(systemPrompt) ? role.SystemPrompt : systemPrompt;

    public string? Prompt { get; set; } = prompt;

    protected override bool SwitchToNextFallback(IStepResult error, PipelineContext context, int attempt)
    {
        // Only a provider failure is worth another engine. Anything else (a tool loop, a template or
        // parsing bug, a cancellation) would fail the same way on every fallback, so it is left to
        // the caller - a derived step may still handle it before delegating here.
        if (error.Error?.OriginalException is not LlmProviderException)
        {
            return false;
        }

        var state = GetState(context);
        var fallbackCount = _role.Fallbacks?.Count ?? 0;
        var nextIndex = state.FallbackIndex + 1;

        if (nextIndex > fallbackCount)
        {
            return false;
        }

        var fallback = _role.Fallbacks![nextIndex - 1];

        // Validate BEFORE committing state.FallbackIndex: GetLlmService silently substitutes
        // NullLlmService when Provider.Service is unresolved, so checking it via that method
        // AFTER the switch would never catch a bad fallback - it would just point the step at
        // NullLlmService for the rest of the invocation instead of reporting a clear failure.
        if (string.IsNullOrWhiteSpace(fallback.Model) || fallback.Provider?.Service == null)
        {
            if (Logger.IsEnabled(LogLevel.Warning))
            {
                Logger.LogWarning(
                    "Step {StepName} cannot switch to fallback engine {Index}: model or provider service did not resolve.",
                    Name, nextIndex);
            }
            return false;
        }

        state.FallbackIndex = nextIndex;

        if (Logger.IsEnabled(LogLevel.Warning))
        {
            Logger.LogWarning("Step {StepName} switching to fallback engine {Index}: {Model} ({Provider})",
                Name, state.FallbackIndex, GetModel(context), GetLlmService(context).Name);
        }

        if (state.Conversation != null && state.InitialBookmark != null)
        {
            state.Conversation.RestoreBookmark(state.InitialBookmark);
        }
        return true;
    }

    /// <summary>Builds the user message sent to the LLM.</summary>
    protected virtual Task<string?> BuildUserMessageAsync(IStepResult input, PipelineContext context) => Task.FromResult(Prompt);

    /// <summary>Builds the system message. Return null for no system prompt.</summary>
    protected virtual Task<string?> BuildSystemMessageAsync(IStepResult input, PipelineContext context) => Task.FromResult(SystemPrompt);

    protected override Task FinalizeAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
    {
        var key = StateKey(context);

        if (!context.StepState.TryGetValue(key, out var stored) || stored is not InvocationState state
            || state.Conversation == null)
        {
            context.StepState.Remove(key);
            return Task.CompletedTask;
        }

        // Tags are processed inline in InvokeLlmAsync for both transports, so by the time this runs
        // AssistantMessage already carries the tag-stripped content. Everything below is the
        // conversation compaction to the canonical [user / assistant] pair - which is why a caller
        // that invokes the step directly, without RetryMiddleware, must call FinalizeAsync itself
        // or the tool traffic of every turn accumulates in the conversation forever.

        if (state.InitialBookmark != null)
        {
            // Always restore the bookmark first to rewind to the clean pre-turn state, even when
            // the message ended up empty (BuildUserMessageAsync returned nothing) - skipping the
            // restore in that case used to leave every retry's tool traffic in the conversation
            // forever, since nothing else ever rewinds to this bookmark.
            state.Conversation.RestoreBookmark(state.InitialBookmark);

            if (!result.HasError && state.Message != "")
            {
                // Success: write the canonical [user / assistant] pair.
                state.Conversation.AddUserMessage(state.Message);
                if (result is ILlmStepResult llmFinal)
                {
                    state.Conversation.AddAssistantMessage(llmFinal.AssistantMessage);
                }
                // If result is not ILlmStepResult (e.g. custom subclass), the user message is
                // still recorded so the conversation is not left in an inconsistent state.
            }
            // On error, or when there was no user message to begin with: bookmark already
            // restored — nothing else gets recorded.
        }

        if (result is ILlmStepResult llmRes)
            AddConversationToContext(context, state.Conversation, llmRes);

        // This invocation is done: drop its slot so a later, unrelated invocation of the same
        // step in the same context (e.g. a LoopStep iteration) starts from a clean state.
        context.StepState.Remove(key);

        return Task.CompletedTask;
    }

    protected override async Task<IStepResult> ExecuteAsync(
        IStepResult input,
        PipelineContext context,
        int attempt,
        IStepResult? lastStepResult,
        CancellationToken cancellationToken)
    {
        var state = GetState(context);

        if (attempt == 1)
        {
            // First attempt ever for this step invocation (or the first attempt of a fresh round
            // right after SwitchToNextFallback, which already set state.FallbackIndex on this
            // same state object - left untouched here).
            state.Conversation = GetConversationContext(context);
            // Create a fresh bookmark at the current (clean) position.
            state.InitialBookmark = state.Conversation!.CreateBookmark();

            // The repeat detector compares tool calls within one turn only: a turn that legitimately
            // starts with the same call the previous one ended with must not inherit its count.
            ResetToolCallRepeatTracking();

            // Add the user message explicitly here — NOT inside BuildLlmRequestAsync.
            // This keeps BuildLlmRequestAsync free of conversation side-effects and makes
            // the mutation visible and reasoned-about at the call site.
            state.Message = await BuildUserMessageAsync(input, context) ?? "";
            state.Conversation.AddUserMessage(state.Message);

            state.Request = await BuildLlmRequestAsync(input, context, state.Conversation!, cancellationToken);

            if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInformation("LLM step {StepName} starting, model: {Model}, conversation size: {MessageCount} messages",
                    Name, GetModel(context), state.Conversation.History.Messages.Count);
            }
            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug("LLM step {StepName} bookmark created: {BookmarkId}", Name, state.InitialBookmark);
            }
            if (Logger.IsEnabled(LogLevel.Trace))
            {
                Logger.LogTrace("LLM step {StepName} user message: {Message}", Name, state.Message);
            }
        }
        else
        {
            // Normal retry: the conversation already contains the user message from attempt 1.
            // Only add the error feedback so the LLM can self-correct.
            var errorFeedback = lastStepResult?.Error?.Message
                ?? $"Your response is invalid for {OutputType.Name}.\nPlease ensure you return valid value";

            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug("LLM step {StepName} retry {Attempt}, adding error feedback to conversation: {Error}",
                    Name, attempt, errorFeedback);
            }
            state.Conversation!.AddUserMessage(errorFeedback);

            // state.Request.Conversation is a reference to state.Conversation (same object), so
            // the LLM service will see the updated history automatically. The with-expression
            // just makes the intent explicit and future-proofs against record copy scenarios.
            state.Request = state.Request! with { Conversation = state.Conversation };
        }

        var llmResult = await InvokeLlmWithToolsAsync(state.Request!, context, 0, cancellationToken);

        if (!llmResult.IsSuccess)
        {
            // Any provider-level error (retriable or fatal) must include the exception, typed as
            // LlmProviderException: RetryMiddleware then treats it as an infrastructure error and
            // asks SwitchToNextFallback, which only changes engine for that type. The tool-iteration
            // limit keeps its own type so it is not mistaken for a provider outage.
            var message = llmResult.Error?.Message ?? "Provider error";
            var error = llmResult.Error is ToolIterationLimitExceededException or LlmProviderException
                ? llmResult.Error
                : new LlmProviderException(message, llmResult.Error, llmResult.IsRetriable);
            return CreateErrorResult(message, error);
        }

        var primaryCandidate = llmResult.Candidates.FirstOrDefault();

        if (Logger.IsEnabled(LogLevel.Debug))
        {
            // El reparto prompt/completion hace visible el tamaño real del contexto enviado en
            // CADA intento (incluido el reintento). Si prompt se clava en el cap del proveedor
            // y completion ~0, hubo truncado de ventana (ver warning en OpenAILlmService).
            Logger.LogDebug("LLM step {StepName} received response, tokens: {Tokens} (prompt: {Prompt}, completion: {Completion}), finish reason: {Reason}",
                Name, llmResult.TokensUsed, llmResult.PromptTokens, llmResult.CompletionTokens, primaryCandidate?.FinishReason);
        }
        if (Logger.IsEnabled(LogLevel.Trace))
        {
            Logger.LogTrace("LLM step {StepName} response content: {Content}", Name, primaryCandidate?.Content);
        }

        (var result, var parseError) = await ParseLlmResponseAsync(llmResult, context);

        // Always record the assistant response in the conversation, even when it is empty
        // or malformed. This guarantees proper [user / assistant / user / assistant] role
        // alternation so that any subsequent retry does NOT produce two consecutive [user]
        // messages. Most LLM APIs (Gemini in particular) reject conversations with
        // consecutive same-role messages and return empty/unexpected responses.
        // FinalizeAsync will restore the bookmark and rewrite the tail of the conversation
        // cleanly, so adding here is always safe — it just keeps the mid-retry state valid.
        state.Conversation!.AddAssistantMessage(primaryCandidate?.Content ?? string.Empty);

        if (result == null || parseError != null)
        {
            if (Logger.IsEnabled(LogLevel.Warning))
            {
                Logger.LogWarning(
                    "LLM response parsing failed (attempt {Attempt}/{Max}): {Error}",
                    attempt, MaxRetries, parseError);
            }

            return CreateErrorResult(parseError ?? "Parsing returned null result");
        }

        return result;
    }

    protected virtual ConversationContext GetConversationContext(PipelineContext context) => context.Conversation;

    protected virtual void AddConversationToContext(PipelineContext context, ConversationContext conversation, ILlmStepResult result)
    {
    }

    /// <summary>
    /// Builds the LLM request. Override for full control; use ConfigureLlmRequest for minor tweaks.
    /// </summary>
    protected virtual async Task<LlmRequest> BuildLlmRequestAsync(
        IStepResult input,
        PipelineContext context,
        ConversationContext conversation,
        CancellationToken cancellationToken)
    {


        var systemPrompt = await BuildSystemMessageAsync(input, context) ?? "";

        if (Tools?.Count > 0)
        {
            systemPrompt = EnrichSystemPromptWithToolGuidelines(systemPrompt, Tools);
        }

        if (TagHandlers?.Count > 0)
        {
            systemPrompt = EnrichSystemPromptWithTags(systemPrompt, TagHandlers);
        }

        // The sliding window is configured once, under AITaskAgent:Conversation, and every provider
        // honours it (the OpenAI service used to send the whole history regardless). When the step
        // runs outside DI the request keeps LlmRequest's own defaults.
        var conversationOptions = (context.ServiceProvider.GetService(typeof(IOptions<AITaskAgentOptions>)) as IOptions<AITaskAgentOptions>)
            ?.Value.Conversation;

        var toolDefinitions = Tools?.Select(t => t.GetDefinition()).ToList();

        var provider = GetProvider(context);
        var inference = GetInference(context);

        var request = new LlmRequest
        {
            // conversation already contains the user message — added explicitly in ExecuteAsync.
            // Do NOT call AddUserMessage here: BuildLlmRequestAsync must be side-effect-free
            // with respect to conversation state.
            Conversation = conversation,
            SystemPrompt = systemPrompt,
            Model = GetModel(context),
            Temperature = inference?.Temperature ?? provider?.Inference?.Temperature,
            MaxTokens = inference?.MaxTokens ?? provider?.Inference?.MaxTokens,
            UseMaxCompletionTokens = inference?.UseMaxCompletionTokens ?? provider?.Inference?.UseMaxCompletionTokens ?? false,
            Tools = toolDefinitions,
            UseStreaming = inference?.UseStreaming ?? provider?.Inference?.UseStreaming ?? false,
            BaseUrl = provider?.BaseUrl,
            ApiKey = provider?.ApiKey,
            ApiVersion = provider?.ApiVersion,
            UseSlidingWindow = conversationOptions?.UseSlidingWindow ?? true,
            SlidingWindowMaxTokens = conversationOptions?.SlidingWindowMaxTokens ?? 8192,
            // Automatically merges options from both levels
            Options = MergeOptions(
                provider?.Inference?.Options,
                inference?.Options)
        };

        request = ConfigureLlmRequest(request, context);
        request = ConfigureJsonResponse(request, GetState(context).Message, provider);

        return request;
    }

    /// <summary>
    /// Override to tweak request parameters (temperature, max tokens, profile swap, etc.)
    /// without replacing the full BuildLlmRequestAsync.
    /// </summary>
    protected virtual LlmRequest ConfigureLlmRequest(LlmRequest request, PipelineContext context) => request;

    private static JObject MergeOptions(params JObject?[] optionsList)
    {
        var result = new JObject();
        foreach (var options in optionsList)
        {
            if (options != null)
            {
                result.Merge(options, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Union });
            }
        }
        return result;
    }

    // ── Response parsing ─────────────────────────────────────────────────────

    protected virtual Task<(IStepResult? Result, string? Error)> ParseLlmResponseAsync(
        LlmResult response,
        PipelineContext context)
    {
        IStepResult? result = null;
        string? errorMsg;

        try
        {
            object? valueObj;
            var content = response.Content;

            if (ValueType == null)
            {
                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug("Response type is string, using content directly");
                }
                valueObj = content;
            }
            else if (!ValueType.IsClass || ValueType.IsPrimitive || ValueType == typeof(string))
            {
                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug("Response type is {Type}, converting from string", ValueType.Name);
                }
                valueObj = LlmHelpers.ConvertOrThrow(content, ValueType);
            }
            else
            {
                var cleanContent = LlmHelpers.CleanJsonResponse(content);
                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug("Parsing JSON response as {Type}", ValueType.Name);
                }
                valueObj = JsonConvert.DeserializeObject(cleanContent, ValueType);

                if (valueObj == null)
                {
                    errorMsg = $"Your response could not be parsed as {ValueType.Name}.\nPlease ensure you return valid JSON matching the schema.";
                    return Task.FromResult<(IStepResult?, string?)>((null, errorMsg));
                }
            }

            result = CreateResult(valueObj);
            SetLlmMetrics(result, response);
            return Task.FromResult<(IStepResult?, string?)>((result, null));
        }
        catch (JsonException ex)
        {
            errorMsg = $"Your response contains invalid JSON. Error: {ex.Message}.\nPlease fix the JSON syntax and ensure it matches the required schema.";
        }
        catch (Exception ex)
        {
            errorMsg = $"Failed to parse response: {ex.Message}";
        }

        return Task.FromResult<(IStepResult?, string?)>((result, errorMsg));
    }

    private static void SetLlmMetrics(IStepResult? result, LlmResult response)
    {
        if (result is not null and LlmStepResult llmResult)
        {
            llmResult.AssistantMessage = response.Content;
            llmResult.Reasoning = response.Reasoning;
            llmResult.Model = response.Model;
            llmResult.TokensUsed = response.TokensUsed;
            llmResult.CostUsd = response.CostUsd;
            llmResult.FinishReason = response.FinishReason;
        }
    }

    private void UpdateContextMetrics(PipelineContext context, LlmResult response)
    {
        var model = GetModel(context);
        var llmServiceName = GetLlmService(context)?.Name;

        Metrics.LlmRequests.Add(1,
            new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmModel, model),
            new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmProvider, llmServiceName));

        if (response.TokensUsed.HasValue)
        {
            context.Metadata["TokensUsed"] = response.TokensUsed.Value;
            Metrics.LlmTokens.Add(response.TokensUsed.Value,
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmModel, model),
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmProvider, llmServiceName));
        }

        if (response.CostUsd.HasValue)
        {
            context.Metadata["CostUsd"] = response.CostUsd.Value;
            Metrics.LlmCost.Add((double)response.CostUsd.Value,
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmModel, model),
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmProvider, llmServiceName));
        }
    }

    // ── JSON response configuration ──────────────────────────────────────────

    private LlmRequest ConfigureJsonResponse(LlmRequest request, string userMessage, LlmProvider? provider)
    {

        if (Schema == null)
        {
            return request;
        }

        var jsonCapability = (provider?.Inference?.JsonSupport ?? false) ? JsonResponseCapability.JsonSchema : JsonResponseCapability.None;

        if (Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug("Configuring JSON response for {TypeName} with provider capability: {Capability}",
                ValueType.Name, jsonCapability);
        }

        return jsonCapability switch
        {
            JsonResponseCapability.JsonSchema => request with
            {
                ResponseFormat = new ResponseFormatOptions
                {
                    Type = ResponseFormatType.JsonObject,
                    JsonSchema = Schema
                },
            },

            JsonResponseCapability.JsonObject => request with
            {
                ResponseFormat = new ResponseFormatOptions
                {
                    Type = ResponseFormatType.JsonObject
                },
                SystemPrompt = LlmHelpers.InjectSchemaInSystemPrompt(request.SystemPrompt, Schema),
                Conversation = request.Conversation.AddUserMessage(userMessage)
            },

            _ => request with
            {
                SystemPrompt = LlmHelpers.InjectSchemaInSystemPrompt(request.SystemPrompt, Schema)
            }
        };
    }

    private static string EnrichSystemPromptWithToolGuidelines(string? basePrompt, IList<ITool> tools)
    {
        var guidelines = tools
            //.Where(t => !string.IsNullOrWhiteSpace(t.UsageGuidelines))
            .Select(t => $"- **{t.Name}**: {t.UsageGuidelines ?? t.Description}")
            .ToList();

        if (guidelines.Count == 0)
        {
            return basePrompt ?? string.Empty;
        }

        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(basePrompt))
        {
            sb.AppendLine(basePrompt);
            sb.AppendLine();
        }

        sb.AppendLine("## TOOL USAGE GUIDELINES");
        sb.AppendLine("Be proactive: use tools directly without asking for confirmation.");
        sb.AppendLine();

        foreach (var guideline in guidelines)
        {
            sb.AppendLine(guideline);
        }

        return sb.ToString();
    }

    private static string EnrichSystemPromptWithTags(string? basePrompt, List<ITagHandler> handlers)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(basePrompt))
        {
            sb.AppendLine(basePrompt);
        }

        var tagList = string.Join(", ", handlers.Select(h => $"<{h.TagName}>"));

        sb.AppendLine();
        sb.AppendLine("## IN-TEXT ACTION TAGS (NOT TOOLS)");
        sb.AppendLine($"Besides your tools you have these action tags: {tagList}.");
        sb.AppendLine("A tag is executed by TYPING it literally in your reply text, exactly as documented below. It is not a function:");
        sb.AppendLine("none of these names exists in your tools list, so a tool_call with one of them always fails and wastes a turn.");
        sb.AppendLine();
        sb.AppendLine("**How the user sees it:** the text between the opening and closing tag is hidden and replaced by a one-line");
        sb.AppendLine("reference (e.g. `[write_file: demo.py (1.2 KB) — Prime numbers script]`). So, right after the closing tag, always");
        sb.AppendLine("add one or two sentences saying what you did - never end your reply on the closing tag.");
        sb.AppendLine();
        sb.AppendLine("### TAG REFERENCE");
        foreach (var handler in handlers)
        {
            sb.AppendLine(handler.GetInstructions());
            sb.AppendLine("---");
        }

        return sb.ToString();
    }
}


/// <summary>
/// Generic typed LLM step. Inherits ALL execution logic from BaseLlmStep.
/// </summary>
public class BaseLlmStep<TIn, TOut>(
    string name,
    LlmRole role,
    string prompt,
    string? systemPrompt = null

    ) : BaseLlmStep(
        name,
        role,
        typeof(TIn),
        typeof(TOut),
        prompt,
        systemPrompt
    ), IStep<TIn, TOut>
    where TIn : IStepResult
    where TOut : ILlmStepResult
{
    protected override Task<IStepResult> ExecuteAsync(
        IStepResult input,
        PipelineContext context,
        int attempt,
        IStepResult? lastStepResult,
        CancellationToken cancellationToken)
    {
        if (!input.GetType().IsAssignableTo(typeof(TIn)))
        {
            try
            {
                input = StepResultFactory.CreateStepResult<TIn>(this, input.Value);
            }
            catch
            {
                throw new InvalidOperationException(
                    $"Step '{Name}' expected input type '{typeof(TIn).Name}' but received '{input.GetType().Name}'.");
            }
        }
        return base.ExecuteAsync(input, context, attempt, lastStepResult, cancellationToken);
    }

    // ── Type-safe helpers ────────────────────────────────────────────────────

    new protected TOut CreateResult(object? value = null, IStepError? error = null)
        => (TOut)base.CreateResult(value, error);

    new protected TOut CreateErrorResult(string message, Exception? exception = null)
        => (TOut)base.CreateErrorResult(message, exception);

    // ── IStep<TIn, TOut> explicit implementations ────────────────────────────

    Type IStep.InputType => typeof(TIn);
    Type IStep.OutputType => typeof(TOut);

    async Task<TOut> IStep<TIn, TOut>.ExecuteAsync(
        TIn input,
        PipelineContext context,
        int attempt,
        TOut? lastStepResult,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(input, context, attempt, lastStepResult, cancellationToken);
        return (TOut)result;
    }

}
