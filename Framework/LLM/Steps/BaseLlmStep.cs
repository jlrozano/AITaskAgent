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
    private int _currentIndex = 0;

    /// <summary>
    /// Active role. Held in a field rather than read from the constructor parameter so that
    /// steps built before their role is known - YAML steps, deserialized by
    /// <c>YamlPipelineFactory</c> before the profile is resolved - can have it supplied later
    /// via <see cref="SetRole"/>.
    /// </summary>
    private LlmRole _role = role;

    /// <summary>
    /// Replaces the role after construction and rewinds the fallback chain. Intended for
    /// deferred resolution; steps constructed with a real role never need it.
    /// Refreshes <see cref="Tools"/> and <see cref="SystemPrompt"/> only when they are still
    /// unset, so anything configured explicitly survives.
    /// </summary>
    protected internal void SetRole(LlmRole newRole)
    {
        _role = newRole ?? throw new ArgumentNullException(nameof(newRole));
        _currentIndex = 0;

        if (Tools.Count == 0 && newRole.Tools is { Count: > 0 })
        {
            Tools = newRole.Tools;
        }

        if (string.IsNullOrWhiteSpace(SystemPrompt))
        {
            SystemPrompt = newRole.SystemPrompt;
        }
    }

    protected ILlmService LlmService => (_currentIndex == 0 ? _role.Provider.Service :
        _role.Fallbacks?[_currentIndex - 1].Provider.Service) ?? new NullLlmService();
    protected string Model => _currentIndex == 0 ? _role.Model :
        _role.Fallbacks?[_currentIndex - 1].Model
        ?? throw new InvalidOperationException("Model not set");
    protected LlmProvider Provider => _currentIndex == 0 ? _role.Provider :
        _role.Fallbacks?[_currentIndex - 1].Provider
        ?? throw new InvalidOperationException("Provider not set");
    protected LlmInferenceSettings Inference =>
        (_currentIndex == 0 ? _role.Inference : _role.Fallbacks?[_currentIndex - 1].Inference)
        ?? Provider.Inference ?? new();

    public int MaxToolIterations { get; init; } = 5;

    public IList<ITool> Tools { get; set; } = role.Tools ?? [];
    public List<IStreamingTagHandler> StreamingHandlers { get; init; } = [];

    // Per-invocation state (reset in FinalizeAsync)
    protected ConversationContext? _conversation = null;
    protected string? _initialBookmark = null;
    protected string _message = "";
    protected LlmRequest? _request = null;

    public string? SystemPrompt { get; set; } = string.IsNullOrWhiteSpace(systemPrompt) ? role.SystemPrompt : systemPrompt;

    public string? Prompt { get; set; } = prompt;

    protected override bool SwitchToNextFallback(IStepResult error, int attempt)
    {
        if (_currentIndex < (_role.Fallbacks?.Count ?? 0))
        {
            _currentIndex++;
            if (string.IsNullOrWhiteSpace(Model) || Provider == null || LlmService == null)
            {
                return false;
            }
            if (Logger.IsEnabled(LogLevel.Warning))
            {
                Logger.LogWarning("Step {StepName} switching to fallback engine {Index}: {Model} ({Provider})",
                    Name, _currentIndex, Model, LlmService.Name);
            }

            if (_conversation != null && _initialBookmark != null)
            {
                _conversation.RestoreBookmark(_initialBookmark);
            }
            return true;
        }
        return false;
    }

    /// <summary>Builds the user message sent to the LLM.</summary>
    protected virtual Task<string?> BuildUserMessageAsync(IStepResult input, PipelineContext context) => Task.FromResult(Prompt);

    /// <summary>Builds the system message. Return null for no system prompt.</summary>
    protected virtual Task<string?> BuildSystemMessageAsync(IStepResult input, PipelineContext context) => Task.FromResult(SystemPrompt);

    protected override async Task FinalizeAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
    {
        if (_conversation == null)
        {
            return;
        }

        if (result is ILlmStepResult llmResult)
        {
            if (StreamingHandlers?.Count > 0 && !string.IsNullOrEmpty(llmResult.AssistantMessage))
            {
                llmResult.AssistantMessage = await ProcessTagsNonStreamingAsync(llmResult.AssistantMessage, context, cancellationToken);
            }
        }

        if (_initialBookmark != null && _message != "")
        {
            // Always restore the bookmark first to rewind to the clean pre-turn state.
            // This removes all mid-retry assistant messages from the conversation.
            _conversation.RestoreBookmark(_initialBookmark);

            if (!result.HasError)
            {
                // Success: write the canonical [user / assistant] pair.
                _conversation.AddUserMessage(_message);
                if (result is ILlmStepResult llmFinal)
                {
                    _conversation.AddAssistantMessage(llmFinal.AssistantMessage);
                }
                // If result is not ILlmStepResult (e.g. custom subclass), the user message is
                // still recorded so the conversation is not left in an inconsistent state.
            }
            // On error: bookmark already restored — user message and any error text are NOT recorded.
        }

        if (result is ILlmStepResult llmRes)
            AddConversationToContext(context, _conversation, llmRes);
        _conversation = null;
        _initialBookmark = null;
        _message = "";
        _request = null;
        _currentIndex = 0; // Reset fallback index for next complete pipeline execution
    }

    protected override async Task<IStepResult> ExecuteAsync(
        IStepResult input,
        PipelineContext context,
        int attempt,
        IStepResult? lastStepResult,
        CancellationToken cancellationToken)
    {
        if (attempt == 1)
        {
            // First attempt ever for this step invocation.
            _conversation = GetConversationContext(context);
            // Create a fresh bookmark at the current (clean) position.
            _initialBookmark = _conversation!.CreateBookmark();

            // Add the user message explicitly here — NOT inside BuildLlmRequestAsync.
            // This keeps BuildLlmRequestAsync free of conversation side-effects and makes
            // the mutation visible and reasoned-about at the call site.
            _message = await BuildUserMessageAsync(input, context) ?? "";
            _conversation.AddUserMessage(_message);

            _request = await BuildLlmRequestAsync(input, context, _conversation!, cancellationToken);

            if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInformation("LLM step {StepName} starting, model: {Model}, conversation size: {MessageCount} messages",
                    Name, Model, _conversation.History.Messages.Count);
            }
            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug("LLM step {StepName} bookmark created: {BookmarkId}", Name, _initialBookmark);
            }
            if (Logger.IsEnabled(LogLevel.Trace))
            {
                Logger.LogTrace("LLM step {StepName} user message: {Message}", Name, _message);
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
            _conversation!.AddUserMessage(errorFeedback);

            // _request.Conversation is a reference to _conversation (same object), so the
            // LLM service will see the updated history automatically. The with-expression
            // just makes the intent explicit and future-proofs against record copy scenarios.
            _request = _request! with { Conversation = _conversation };
        }

        var llmResult = await InvokeLlmWithToolsAsync(_request!, context, 0, cancellationToken);

        if (!llmResult.IsSuccess)
        {
            // Any provider-level error (retriable or fatal) must include the exception.
            // This ensures RetryMiddleware recognizes it as an infrastructure error
            // and immediately triggers SwitchToNextFallback instead of treating it 
            // as a prompt parsing error.
            return CreateErrorResult(llmResult.Error?.Message ?? "Provider error", llmResult.Error);
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
        _conversation!.AddAssistantMessage(primaryCandidate?.Content ?? string.Empty);

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

        if (StreamingHandlers?.Count > 0)
        {
            systemPrompt = EnrichSystemPromptWithStreamingTags(systemPrompt, StreamingHandlers);
        }

        var toolDefinitions = Tools?.Select(t => t.GetDefinition()).ToList();

        var request = new LlmRequest
        {
            // conversation already contains the user message — added explicitly in ExecuteAsync.
            // Do NOT call AddUserMessage here: BuildLlmRequestAsync must be side-effect-free
            // with respect to conversation state.
            Conversation = conversation,
            SystemPrompt = systemPrompt,
            Model = Model,
            Temperature = Inference?.Temperature ?? Provider?.Inference?.Temperature,
            MaxTokens = Inference?.MaxTokens ?? Provider?.Inference?.MaxTokens,
            Tools = toolDefinitions,
            UseStreaming = Inference?.UseStreaming ?? Provider?.Inference?.UseStreaming ?? false,
            BaseUrl = Provider?.BaseUrl,
            ApiKey = Provider?.ApiKey,
            // Automatically merges options from both levels
            Options = MergeOptions(
                Provider?.Inference?.Options,
                Inference?.Options)
        };

        request = ConfigureLlmRequest(request, context);
        request = ConfigureJsonResponse(request, _message);

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
        Metrics.LlmRequests.Add(1,
            new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmModel, Model),
            new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmProvider, LlmService?.Name));

        if (response.TokensUsed.HasValue)
        {
            context.Metadata["TokensUsed"] = response.TokensUsed.Value;
            Metrics.LlmTokens.Add(response.TokensUsed.Value,
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmModel, Model),
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmProvider, LlmService?.Name));
        }

        if (response.CostUsd.HasValue)
        {
            context.Metadata["CostUsd"] = response.CostUsd.Value;
            Metrics.LlmCost.Add((double)response.CostUsd.Value,
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmModel, Model),
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmProvider, LlmService?.Name));
        }
    }

    // ── JSON response configuration ──────────────────────────────────────────

    private LlmRequest ConfigureJsonResponse(LlmRequest request, string userMessage)
    {

        if (Schema == null)
        {
            return request;
        }

        var jsonCapability = (Provider?.Inference?.JsonSupport ?? false) ? JsonResponseCapability.JsonSchema : JsonResponseCapability.None;

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

    private static string EnrichSystemPromptWithStreamingTags(string? basePrompt, List<IStreamingTagHandler> handlers)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(basePrompt))
        {
            sb.AppendLine(basePrompt);
        }

        sb.AppendLine();
        sb.AppendLine("## IN-TEXT STREAMING ACTIONS (NOT TOOLS)");
        sb.AppendLine("You can execute actions directly by typing XML tags in your response. These are NOT functions or tools. DO NOT invoke them via the tool_call mechanism.");
        sb.AppendLine("Simply type the XML tag directly in your text response to execute it.");
        sb.AppendLine();
        sb.AppendLine("**IMPORTANT BEHAVIOR:**");
        sb.AppendLine("- The content inside XML tags (e.g., file content) is INVISIBLE to the user - they only see a brief placeholder.");
        sb.AppendLine("- AFTER using any tag, you MUST provide a clear explanation of what you did, just like you would after a tool call.");
        sb.AppendLine("- Example: After writing a file, say something like: 'He creado el archivo `demo.py` con el código para imprimir números primos del 1 al 100.'");
        sb.AppendLine("- Do NOT just write the tag and stop. Always follow up with a user-friendly explanation.");
        sb.AppendLine();
        sb.AppendLine("### AVAILABLE TAGS (Type directly in chat)");
        foreach (var handler in handlers)
        {
            sb.AppendLine($"- **<{handler.TagName}>**");
        }

        sb.AppendLine();
        sb.AppendLine("### DETAILED INSTRUCTIONS");
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
