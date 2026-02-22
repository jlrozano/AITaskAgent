using AITaskAgent.Core;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Base;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Streaming;
using AITaskAgent.LLM.Support;
using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.Observability.Events;
using AITaskAgent.Support.JSON;
using AITaskAgent.Support.Template;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NJsonSchema;
using NJsonSchema.CodeGeneration;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

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
public abstract class BaseLlmStep(
    ILlmService llmService,
    string name,
    Type inputType,
    Type outputType,
    LlmProviderConfig profile,
    List<ITool>? tools = null,
    int maxToolIterations = 5,
    List<IStreamingTagHandler>? streamingHandlers = null,
    ITemplateProvider? templateProvider = null
) : StepBase(name, inputType, outputType)
{
    protected readonly ILlmService LlmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
    protected readonly LlmProviderConfig Profile = profile;
    protected readonly ITemplateProvider? TemplateProvider = templateProvider;

    private readonly List<ITool>? _tools = tools;
    private readonly List<IStreamingTagHandler>? _streamingHandlers = streamingHandlers;

    // Per-invocation state (reset in FinalizeAsync)
    private ConversationContext? _conversation = null;
    private string? _initialBookmark = null;
    private string? _message = null;
    private LlmRequest? _request = null;

    // Loop detection state
    private int _consecutiveIdenticalToolCalls = 0;
    private HashSet<string>? _lastToolCallHashes = null;

    private static readonly HashSet<string> ReadOnlyTools = ["view_file", "grep_search", "list_dir", "find_by_name", "view_file_outline", "view_code_item"];

    protected int MaxToolIterations { get; init; } = maxToolIterations;

    // ── Template resolution ──────────────────────────────────────────────────

    protected Task<string> ResolvePromptAsync(string rawPrompt, IStepResult input, PipelineContext context)
    {
        var model = new { inputData = input.Value, context };
        if (rawPrompt.StartsWith('@') && TemplateProvider != null)
        {
            var templateName = rawPrompt[1..];
            return Task.FromResult(TemplateProvider.Render(templateName, model) ?? rawPrompt);
        }
        return Task.FromResult(JsonTemplateEngine.Render(rawPrompt, model, strictMode: false) ?? rawPrompt);
    }

    // ── Prompt builders (override to supply prompts) ─────────────────────────

    /// <summary>Builds the user message sent to the LLM.</summary>
    protected abstract Task<string> BuildUserMessageAsync(IStepResult input, PipelineContext context);

    /// <summary>Builds the system message. Return null for no system prompt.</summary>
    protected virtual Task<string?> BuildSystemMessageAsync(IStepResult input, PipelineContext context)
        => Task.FromResult<string?>(null);

    // ── Core execution ───────────────────────────────────────────────────────

    protected override Task FinalizeAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
    {
        if (_conversation == null)
            return Task.CompletedTask;

        if (result is ILlmStepResult llmResult)
            AddConversationToContext(context, _conversation, llmResult);

        if (_initialBookmark != null && _message != null)
        {
            _conversation.RestoreBookmark(_initialBookmark);
            _conversation.AddUserMessage(_message);

            if (!result.HasError)
                _conversation.AddAssistantMessage(((ILlmStepResult)result).AssistantMessage);
            else
                _conversation.AddAssistantMessage($"Error: {result.Error?.Message ?? "Unknown error"}");
        }

        _conversation = null;
        _initialBookmark = null;
        _message = null;
        _request = null;

        return Task.CompletedTask;
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
            _conversation = GetConversationContext(context);
            _initialBookmark = _conversation.CreateBookmark();
            _message = await BuildUserMessageAsync(input, context);
            _request = await BuildLlmRequestAsync(input, context, _conversation!, cancellationToken);

            Logger.LogInformation("LLM step {StepName} starting, model: {Model}, conversation size: {MessageCount} messages",
                Name, Profile.Model, _conversation.History.Messages.Count);
            Logger.LogDebug("LLM step {StepName} bookmark created: {BookmarkId}", Name, _initialBookmark);
            Logger.LogTrace("LLM step {StepName} user message: {Message}", Name, _message);
        }
        else
        {
            var errorFeedback = lastStepResult?.Error?.Message
                ?? $"Your response is invalid for {OutputType.Name}.\nPlease ensure you return valid value";

            Logger.LogDebug("LLM step {StepName} retry {Attempt}, adding error to conversation: {Error}",
                Name, attempt, errorFeedback);
            _conversation!.AddUserMessage(errorFeedback);
        }

        var response = await InvokeLlmWithToolsAsync(_request!, context, 0, cancellationToken);

        Logger.LogDebug("LLM step {StepName} received response, tokens: {Tokens}, finish reason: {Reason}",
            Name, response.TokensUsed, response.FinishReason);
        Logger.LogTrace("LLM step {StepName} response content: {Content}", Name, response.Content);

        var (result, parseError) = await ParseLlmResponseAsync(response, context);

        if (result == null || parseError != null)
        {
            Logger.LogWarning(
                "LLM response parsing failed (attempt {Attempt}/{Max}): {Error}",
                attempt, MaxRetries, parseError);

            return CreateErrorResult(parseError ?? "Parsing returned null result");
        }

        return result;
    }

    // ── Tool execution loop ──────────────────────────────────────────────────

    private async Task<LlmResponse> InvokeLlmWithToolsAsync(
        LlmRequest request,
        PipelineContext context,
        int toolIteration,
        CancellationToken cancellationToken)
    {
        if (toolIteration >= MaxToolIterations)
        {
            throw new InvalidOperationException(
                $"Maximum tool iterations ({MaxToolIterations}) exceeded - possible infinite loop");
        }

        using var llmActivity = Telemetry.Source.StartActivity("LLM.Invoke", ActivityKind.Client);
        llmActivity?.SetTag(AITaskAgentConstants.TelemetryTags.LlmModel, request.Profile.Model);
        llmActivity?.SetTag(AITaskAgentConstants.TelemetryTags.LlmProvider, request.Profile.Provider.ToString());
        llmActivity?.SetTag("llm.iteration", toolIteration);
        llmActivity?.SetTag("llm.has_tools", request.Tools?.Count > 0);

        Logger.LogDebug("LLM invoke starting (iteration {Iteration}), model: {Model}, tools available: {ToolCount}",
            toolIteration, request.Profile.Model, request.Tools?.Count ?? 0);

        var llmStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var response = await InvokeLlmAsync(request, context, Name, cancellationToken);
        llmStopwatch.Stop();

        llmActivity?.SetTag(AITaskAgentConstants.TelemetryTags.TokensUsed, response.TokensUsed);
        llmActivity?.SetTag("llm.finish_reason", response.FinishReason);

        Metrics.LlmDuration.Record(llmStopwatch.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmModel, Profile.Model),
            new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmProvider, Profile.Provider));

        UpdateContextMetrics(context, response);

        if (response.ToolCalls == null || response.ToolCalls.Count == 0)
        {
            Logger.LogDebug("LLM returned final response (no tool calls), tokens: {Tokens}", response.TokensUsed);
            return response;
        }

        HashSet<string> currentToolHashes = [.. response.ToolCalls.Select(LlmHelpers.NormalizeToolCallKey)];

        if (_lastToolCallHashes != null && currentToolHashes.SetEquals(_lastToolCallHashes))
        {
            _consecutiveIdenticalToolCalls++;

            var toolName = response.ToolCalls.FirstOrDefault()?.Name ?? string.Empty;
            var isReadOnly = ReadOnlyTools.Contains(toolName);
            var threshold = isReadOnly ? 3 : 1;

            if (_consecutiveIdenticalToolCalls > threshold)
            {
                Logger.LogWarning(
                    "Tool loop detected: {ToolName} called {Count} times consecutively (threshold: {Threshold})",
                    toolName, _consecutiveIdenticalToolCalls + 1, threshold + 1);

                var fallbackContent = !string.IsNullOrWhiteSpace(response.Content)
                    ? response.Content
                    : $"Error: Execution stopped because '{toolName}' was called {_consecutiveIdenticalToolCalls + 1} times consecutively without progress.";

                return new LlmResponse
                {
                    Content = fallbackContent,
                    TokensUsed = response.TokensUsed,
                    PromptTokens = response.PromptTokens,
                    CompletionTokens = response.CompletionTokens,
                    CostUsd = response.CostUsd,
                    FinishReason = FinishReason.Stop,
                    Model = response.Model,
                    NativeResponse = response.NativeResponse,
                    ToolCalls = null
                };
            }

            Logger.LogDebug("Same tool called again: {ToolName} (consecutive: {Count}/{Threshold})",
                toolName, _consecutiveIdenticalToolCalls + 1, threshold + 1);
        }
        else
        {
            if (_consecutiveIdenticalToolCalls > 0)
                Logger.LogDebug("Tool changed, resetting loop counter (was: {Count})", _consecutiveIdenticalToolCalls);

            _consecutiveIdenticalToolCalls = 0;
            _lastToolCallHashes = currentToolHashes;
        }

        var uniqueToolCalls = DeduplicateToolCalls(response.ToolCalls);

        Logger.LogInformation("LLM requested {ToolCount} tool calls (iteration {Iteration}), executing {UniqueCount} unique",
            response.ToolCalls.Count, toolIteration + 1, uniqueToolCalls.Count);

        context.Conversation?.AddAssistantMessageWithToolCalls(response.ToolCalls);

        await ExecuteToolCallsAsync(Name, uniqueToolCalls, response.ToolCalls, _tools, context, cancellationToken);

        return await InvokeLlmWithToolsAsync(request, context, toolIteration + 1, cancellationToken);
    }

    private List<ToolCall> DeduplicateToolCalls(List<ToolCall> toolCalls)
    {
        Dictionary<string, ToolCall> uniqueCalls = [];

        foreach (var toolCall in toolCalls)
        {
            var key = LlmHelpers.NormalizeToolCallKey(toolCall);
            if (!uniqueCalls.ContainsKey(key))
                uniqueCalls[key] = toolCall;
        }

        if (uniqueCalls.Count < toolCalls.Count)
        {
            Logger.LogWarning("Deduplicated {Removed} duplicate tool calls from batch of {Total}",
                toolCalls.Count - uniqueCalls.Count, toolCalls.Count);
        }

        return [.. uniqueCalls.Values];
    }

    private async Task ExecuteToolCallsAsync(
        string stepName,
        List<ToolCall> uniqueToolCalls,
        List<ToolCall> allToolCalls,
        List<ITool>? availableTools,
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        var conversation = context.Conversation;

        if (availableTools == null || availableTools.Count == 0)
        {
            Logger.LogWarning("No tools available, cannot execute tool calls");
            foreach (var toolCall in allToolCalls)
                conversation?.AddToolMessage(toolCall.Id, "Error: No tools available");
            return;
        }

        Dictionary<string, string> resultCache = [];

        foreach (var toolCall in uniqueToolCalls)
        {
            var cacheKey = LlmHelpers.NormalizeToolCallKey(toolCall);
            var tool = availableTools.FirstOrDefault(t => t.Name == toolCall.Name);

            if (tool == null)
            {
                Logger.LogError("Tool '{ToolName}' not found in registry", toolCall.Name);
                resultCache[cacheKey] = $"Error: Tool '{toolCall.Name}' not found";

                await context.SendEventAsync(new ToolCompletedEvent
                {
                    StepName = stepName,
                    ToolName = toolCall.Name,
                    Success = false,
                    Duration = TimeSpan.Zero,
                    ErrorMessage = "Tool not found",
                    CorrelationId = context.CorrelationId
                }, cancellationToken);
                continue;
            }

            try
            {
                var result = await tool.ExecuteAsync(toolCall.Arguments, context, stepName, Logger, cancellationToken);
                resultCache[cacheKey] = result ?? string.Empty;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Tool {ToolName} execution failed", toolCall.Name);
                resultCache[cacheKey] = $"Error executing tool: {ex.Message}";
            }
        }

        foreach (var toolCall in allToolCalls)
        {
            var cacheKey = LlmHelpers.NormalizeToolCallKey(toolCall);
            var result = resultCache.GetValueOrDefault(cacheKey, "Error: Result not found");
            conversation?.AddToolMessage(toolCall.Id, result);
        }
    }

    // ── LLM invocation (non-streaming + streaming) ───────────────────────────

    private async Task<LlmResponse> InvokeLlmAsync(
        LlmRequest request,
        PipelineContext context,
        string stepName,
        CancellationToken cancellationToken)
    {
        if (Logger.IsEnabled(LogLevel.Trace))
        {
            var prompts = request.Conversation.GetMessagesForRequest(
                maxTokens: request.SlidingWindowMaxTokens,
                useSlidingWindow: request.UseSlidingWindow);

            var promptLog = string.Join("\n---\n", prompts.Select(m => $"[{m.Role}]: {m.Content}"));
            if (!string.IsNullOrEmpty(request.SystemPrompt))
                promptLog = $"[System]: {request.SystemPrompt}\n---\n{promptLog}";

            Logger.LogTrace("[LLM] Step {StepName} Request:\n{Prompts}", stepName, promptLog);
        }

        if (!request.UseStreaming)
        {
            var response = await LlmService.InvokeAsync(request, cancellationToken);

            if (_streamingHandlers?.Count > 0 && !string.IsNullOrEmpty(response.Content))
                response = await ProcessTagsNonStreamingAsync(response, context, cancellationToken);

            if (!string.IsNullOrEmpty(response.Content))
            {
                Logger.LogTrace("[LLM] Step {StepName} Final Response:\n{Content}\n[Finish: {FinishReason}, Tokens: {Tokens}]",
                    stepName, response.Content, response.FinishReason, response.TokensUsed);

                var reason = response.FinishReason ?? FinishReason.Other;
                var rawReason = response.RawFinishReason;

                if (reason == FinishReason.Other)
                    Logger.LogWarning("Unknown LLM finish reason: {RawReason}", rawReason);

                await context.SendEventAsync(new LlmResponseEvent
                {
                    StepName = stepName,
                    Content = response.Content,
                    FinishReason = reason,
                    RawFinishReason = rawReason,
                    TokensUsed = response.TokensUsed ?? 0,
                    Model = Profile.Model,
                    Provider = Profile.Provider,
                    CorrelationId = context.CorrelationId
                }, cancellationToken);
            }
            else if (response.ToolCalls?.Count > 0)
            {
                Logger.LogDebug("[LLM] Step {StepName} Tool Calls:\n{ToolCalls}\n[Finish: {FinishReason}]",
                    stepName,
                    string.Join("\n", response.ToolCalls.Select(tc => $"- {tc.Name}({tc.Arguments})")),
                    response.FinishReason);
            }

            return response;
        }

        // ── Streaming mode ───────────────────────────────────────────────────
        var contentBuilder = new StringBuilder();
        Dictionary<int, (string? Id, string? Name, StringBuilder Args)> toolCallBuilders = [];
        FinishReason? finishReason = null;
        string? rawFinishReason = null;
        var totalTokens = 0;

        StreamingTagParser? tagParser = _streamingHandlers?.Count > 0
            ? new StreamingTagParser(_streamingHandlers)
            : null;

        await foreach (var chunk in LlmService.InvokeStreamingAsync(request, cancellationToken))
        {
            if (!string.IsNullOrEmpty(chunk.Delta))
            {
                string processedDelta = chunk.Delta;

                if (tagParser != null)
                    processedDelta = await tagParser.ProcessChunkAsync(chunk.Delta, context, cancellationToken);

                if (!chunk.IsThinking && !string.IsNullOrEmpty(processedDelta))
                    contentBuilder.Append(processedDelta);

                if (!string.IsNullOrEmpty(processedDelta))
                {
                    await context.SendEventAsync(new LlmResponseEvent
                    {
                        StepName = stepName,
                        Content = processedDelta,
                        FinishReason = FinishReason.Streaming,
                        IsThinking = chunk.IsThinking,
                        CorrelationId = context.CorrelationId
                    }, cancellationToken);
                }
            }

            if (chunk.ToolCallUpdates != null)
            {
                foreach (var update in chunk.ToolCallUpdates)
                {
                    if (!toolCallBuilders.TryGetValue(update.Index, out var builder))
                    {
                        builder = (null, null, new StringBuilder());
                        toolCallBuilders[update.Index] = builder;
                    }

                    if (update.ToolCallId != null)
                        toolCallBuilders[update.Index] = (update.ToolCallId, builder.Item2, builder.Item3);

                    if (update.FunctionName != null)
                        toolCallBuilders[update.Index] = (builder.Item1, update.FunctionName, builder.Item3);

                    if (update.FunctionArgumentsUpdate != null)
                        builder.Args.Append(update.FunctionArgumentsUpdate);
                }
            }

            if (chunk.IsComplete)
            {
                finishReason = chunk.FinishReason;
                rawFinishReason = chunk.RawFinishReason;
                if (chunk.TokensUsed.HasValue)
                    totalTokens = chunk.TokensUsed.Value;
            }
        }

        var finalReason = finishReason ?? FinishReason.Stop;
        if (finalReason == FinishReason.Other)
            Logger.LogWarning("Unknown LLM finish reason in streaming: {RawReason}", rawFinishReason);

        await context.SendEventAsync(new LlmResponseEvent
        {
            StepName = stepName,
            Content = string.Empty,
            FinishReason = finalReason,
            RawFinishReason = rawFinishReason,
            TokensUsed = totalTokens,
            Model = Profile.Model,
            Provider = Profile.Provider,
            CorrelationId = context.CorrelationId
        }, cancellationToken);

        List<ToolCall> toolCalls = [];
        foreach (var kvp in toolCallBuilders.OrderBy(k => k.Key))
        {
            (var id, var toolName, var args) = kvp.Value;
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(toolName))
            {
                toolCalls.Add(new ToolCall
                {
                    Id = id,
                    Name = toolName,
                    Arguments = args?.ToString() ?? string.Empty
                });
            }
        }

        return new LlmResponse
        {
            Content = contentBuilder.ToString(),
            ToolCalls = toolCalls,
            FinishReason = finishReason ?? FinishReason.Stop,
            RawFinishReason = rawFinishReason,
            Model = request.Profile.Model,
            TokensUsed = totalTokens
        };
    }

    private async Task<LlmResponse> ProcessTagsNonStreamingAsync(
        LlmResponse response,
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        if (_streamingHandlers == null || _streamingHandlers.Count == 0 || string.IsNullOrEmpty(response.Content))
            return response;

        var content = response.Content;
        var handlerDict = _streamingHandlers.ToDictionary(h => h.TagName);
        var tagRegex = new Regex(@"<(\w+)([^>]*)>(.*?)</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var sb = new StringBuilder();
        var lastIndex = 0;

        foreach (Match match in tagRegex.Matches(content))
        {
            sb.Append(content[lastIndex..match.Index]);

            var tagName = match.Groups[1].Value;
            var attrString = match.Groups[2].Value;
            var tagContent = match.Groups[3].Value;

            if (handlerDict.TryGetValue(tagName, out var handler))
            {
                var attributes = ParseAttributes(attrString);
                string placeholder;
                try
                {
                    var result = await handler.OnCompleteTagAsync(attributes, tagContent, context, cancellationToken);
                    placeholder = result ?? string.Empty;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error processing tag {TagName}", tagName);
                    placeholder = $"[Error processing tag {tagName}: {ex.Message}]";
                }
                sb.Append(placeholder);
            }
            else
            {
                sb.Append(match.Value);
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < content.Length)
            sb.Append(content[lastIndex..]);

        return new LlmResponse
        {
            Content = sb.ToString(),
            TokensUsed = response.TokensUsed,
            PromptTokens = response.PromptTokens,
            CompletionTokens = response.CompletionTokens,
            CostUsd = response.CostUsd,
            FinishReason = response.FinishReason,
            RawFinishReason = response.RawFinishReason,
            ToolCalls = response.ToolCalls,
            Choices = response.Choices,
            Model = response.Model,
            NativeResponse = response.NativeResponse
        };
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
            sb.AppendLine($"- **<{handler.TagName}>**");
        sb.AppendLine();
        sb.AppendLine("### DETAILED INSTRUCTIONS");
        foreach (var handler in handlers)
        {
            sb.AppendLine(handler.GetInstructions());
            sb.AppendLine("---");
        }

        return sb.ToString();
    }

    private static Dictionary<string, string> ParseAttributes(string attrString)
    {
        var attributes = new Dictionary<string, string>();
        var attrRegex = new Regex(@"(\w+)=""([^""]*)""");
        foreach (Match match in attrRegex.Matches(attrString))
            attributes[match.Groups[1].Value] = match.Groups[2].Value;
        return attributes;
    }

    // ── Overridable hooks ────────────────────────────────────────────────────

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
        string? systemPrompt = await BuildSystemMessageAsync(input, context);

        if (_tools?.Count > 0)
            systemPrompt = EnrichSystemPromptWithToolGuidelines(systemPrompt, _tools);

        if (_streamingHandlers?.Count > 0)
            systemPrompt = EnrichSystemPromptWithStreamingTags(systemPrompt, _streamingHandlers);

        var toolDefinitions = _tools?.Select(t => t.GetDefinition()).ToList();

        var request = new LlmRequest
        {
            Conversation = conversation,
            SystemPrompt = systemPrompt,
            Profile = Profile,
            Temperature = Profile.Temperature,
            MaxTokens = Profile.MaxTokens,
            TopP = Profile.TopP,
            TopK = Profile.TopK,
            FrequencyPenalty = Profile.FrequencyPenalty,
            PresencePenalty = Profile.PresencePenalty,
            Tools = toolDefinitions,
            UseStreaming = Profile.UseStreaming
        };

        request = ConfigureLlmRequest(request, context);
        request = ConfigureJsonResponse(request, _message!);

        return request;
    }

    /// <summary>
    /// Override to tweak request parameters (temperature, max tokens, profile swap, etc.)
    /// without replacing the full BuildLlmRequestAsync.
    /// </summary>
    protected virtual LlmRequest ConfigureLlmRequest(LlmRequest request, PipelineContext context) => request;

    // ── Response parsing ─────────────────────────────────────────────────────

    protected virtual Task<(IStepResult? Result, string? Error)> ParseLlmResponseAsync(
        LlmResponse response,
        PipelineContext context)
    {
        IStepResult? result = null;
        string? errorMsg;

        try
        {
            object? valueObj;

            if (ValueType == null)
            {
                Logger.LogDebug("Response type is string, using content directly");
                valueObj = response.Content;
            }
            else if (!ValueType.IsClass || ValueType.IsPrimitive || ValueType == typeof(string))
            {
                Logger.LogDebug("Response type is {Type}, converting from string", ValueType.Name);
                valueObj = LlmHelpers.ConvertOrThrow(response.Content, ValueType);
            }
            else
            {
                var cleanContent = LlmHelpers.CleanJsonResponse(response.Content);
                Logger.LogDebug("Parsing JSON response as {Type}", ValueType.Name);
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

    private static void SetLlmMetrics(IStepResult? result, LlmResponse response)
    {
        if (result is not null and LlmStepResult llmResult)
        {
            llmResult.AssistantMessage = response.Content;
            llmResult.Model = response.Model;
            llmResult.TokensUsed = response.TokensUsed;
            llmResult.CostUsd = response.CostUsd;
            llmResult.FinishReason = response.FinishReason;
        }
    }

    private void UpdateContextMetrics(PipelineContext context, LlmResponse response)
    {
        Metrics.LlmRequests.Add(1,
            new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmModel, Profile.Model),
            new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmProvider, Profile.Provider));

        if (response.TokensUsed.HasValue)
        {
            context.Metadata["TokensUsed"] = response.TokensUsed.Value;
            Metrics.LlmTokens.Add(response.TokensUsed.Value,
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmModel, Profile.Model),
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmProvider, Profile.Provider));
        }

        if (response.CostUsd.HasValue)
        {
            context.Metadata["CostUsd"] = response.CostUsd.Value;
            Metrics.LlmCost.Add((double)response.CostUsd.Value,
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmModel, Profile.Model),
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmProvider, Profile.Provider));
        }
    }

    // ── JSON response configuration ──────────────────────────────────────────

    private LlmRequest ConfigureJsonResponse(LlmRequest request, string userMessage)
    {
        if (Schema == null)
        {
            return request with
            {
                Conversation = request.Conversation.AddUserMessage(userMessage)
            };
        }

        Logger.LogDebug("Configuring JSON response for {TypeName} with profile capability: {Capability}",
            ValueType.Name, request.Profile.JsonCapability);

        return request.Profile.JsonCapability switch
        {
            JsonResponseCapability.JsonSchema => request with
            {
                ResponseFormat = new ResponseFormatOptions
                {
                    Type = ResponseFormatType.JsonObject,
                    JsonSchema = Schema
                },
                Conversation = request.Conversation.AddUserMessage(userMessage)
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
                Conversation = request.Conversation.AddUserMessage(LlmHelpers.InjectSchemaInUserMessage(userMessage, Schema))
            }
        };
    }

    private static string EnrichSystemPromptWithToolGuidelines(string? basePrompt, List<ITool> tools)
    {
        var guidelines = tools
            .Where(t => !string.IsNullOrWhiteSpace(t.UsageGuidelines))
            .Select(t => $"- **{t.Name}**: {t.UsageGuidelines}")
            .ToList();

        if (guidelines.Count == 0)
            return basePrompt ?? string.Empty;

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
            sb.AppendLine(guideline);

        return sb.ToString();
    }
}


/// <summary>
/// Generic typed LLM step. Inherits ALL execution logic from BaseLlmStep.
/// Only responsibility: compile-time type safety.
///   - Passes typeof(TIn) / typeof(TOut) to the base constructor.
///   - Adapts typed delegates to the BuildUserMessageAsync / BuildSystemMessageAsync virtuals.
///   - Coerces IStepResult inputs to TIn when needed.
/// </summary>
public class BaseLlmStep<TIn, TOut>(
    ILlmService llmService,
    string name,
    LlmProviderConfig profile,
    Func<TIn, PipelineContext, Task<string>>? messageBuilder = null,
    Func<TIn, PipelineContext, Task<string>>? systemMessageBuilder = null,
    List<ITool>? tools = null,
    int maxToolIterations = 5,
    List<IStreamingTagHandler>? streamingHandlers = null,
    ITemplateProvider? templateProvider = null
    ) : BaseLlmStep(llmService, name, typeof(TIn), typeof(TOut), profile, tools, maxToolIterations, streamingHandlers, templateProvider),
      IStep<TIn, TOut>
    where TIn : IStepResult
    where TOut : ILlmStepResult
{
    // ── Wire typed delegates to the virtual hooks ────────────────────────────

    protected override Task<string> BuildUserMessageAsync(IStepResult input, PipelineContext context)
    {
        if (messageBuilder == null)
            throw new InvalidOperationException(
                $"Step '{Name}' must provide a messageBuilder delegate or override BuildUserMessageAsync.");
        return messageBuilder((TIn)input, context);
    }

    protected override async Task<string?> BuildSystemMessageAsync(IStepResult input, PipelineContext context)
        => systemMessageBuilder != null ? await systemMessageBuilder((TIn)input, context) : null;

    // ── Input type coercion ──────────────────────────────────────────────────

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
