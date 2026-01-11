using AITaskAgent.Core;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Base;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Support;
using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.Observability.Events;
using AITaskAgent.Support.JSON;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NJsonSchema;
using System.Diagnostics;
using System.Text;

namespace AITaskAgent.LLM.Steps;


/// <summary>
/// Base class for steps that interact with LLMs.
/// Provides automatic retry with bookmark, validation, metrics tracking, and recursive tool execution.
/// Default timeout is 5 minutes (longer than regular steps due to LLM response times).
/// </summary>
public class BaseLlmStep<TIn, TOut>(
    ILlmService llmService,
    string name,
    LlmProviderConfig profile,
    Func<TIn, PipelineContext, Task<string>> messageBuilder,
    Func<TIn, PipelineContext, Task<string>>? systemMessageBuilder = null,
    List<ITool>? tools = null,
    Func<TOut, Task<(bool IsValid, string? Error)>>? resultValidator = null
    ) : TypedStep<TIn, TOut>(name)
    where TIn : IStepResult
    where TOut : ILlmStepResult
{
    private readonly ILlmService _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
    private readonly Func<TOut, Task<(bool IsValid, string? Error)>>? _resultValidator = resultValidator;
    private ConversationContext? _conversation = null;
    private string? _initialBookmark = null;
    private string? _message = null;
    private LlmRequest? _request = null;

    /// <summary>
    /// Maximum tool execution iterations to prevent infinite loops.
    /// </summary>
    protected int MaxToolIterations { get; init; } = 5;

    protected override Task FinalizeAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
    {
        if (_conversation == null)
        {
            return Task.CompletedTask;
        }

        // Always add conversation to context (success or error)
        if (result is TOut typedResult)
        {
            AddConversationToContext(context, _conversation, typedResult);
        }

        // ALWAYS clean up conversation (success or error)
        // Restore to INITIAL bookmark (before any modifications) and add clean message + response
        if (_initialBookmark != null && _message != null)
        {
            _conversation.RestoreBookmark(_initialBookmark);
            _conversation.AddUserMessage(_message);

            if (!result.HasError)
            {
                // On success, add assistant's valid response
                _conversation.AddAssistantMessage(((TOut)result).AssistantMessage);
            }
            else
            {
                // On error, add error message to maintain conversation context
                _conversation.AddAssistantMessage($"Error: {result.Error?.Message ?? "Unknown error"}");
            }
        }

        // Reset step-specific state for potential reuse
        _conversation = null;
        _initialBookmark = null;
        _message = null;
        _request = null;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Executes the LLM step with validation retry logic, bookmarks, and recursive tool execution.
    /// Uses attempt and lastStepResult from TypedStep for retry state.
    /// </summary>
    protected override async Task<TOut> ExecuteAsync(
        TIn input,
        PipelineContext context,
        int attempt,
        TOut? lastStepResult,
        CancellationToken cancellationToken)
    {
        // Only initialize conversation ONCE per step invocation (first attempt)
        if (attempt == 1)
        {
            _conversation = GetConversationContext(context);
            _initialBookmark = _conversation.CreateBookmark();  // Before any modifications
            _message = await messageBuilder(input, context);
            _request = await BuildLlmRequestAsync(
                input,
                context,
                _conversation!,
                cancellationToken);

            Logger.LogInformation("LLM step {StepName} starting, model: {Model}, conversation size: {MessageCount} messages",
                Name, profile.Model, _conversation.History.Messages.Count);
            Logger.LogDebug("LLM step {StepName} bookmark created: {BookmarkId}",
                Name, _initialBookmark);
            Logger.LogTrace("LLM step {StepName} user message: {Message}", Name, _message);
        }
        else
        {
            // Retry: add error feedback to conversation for LLM self-correction
            var errorFeedback = lastStepResult?.Error?.Message
                ?? $"Your response is invalid for {typeof(TOut).Name}.\nPlease ensure you return valid value";

            Logger.LogDebug("LLM step {StepName} retry {Attempt}, adding error to conversation: {Error}",
                Name, attempt, errorFeedback);
            _conversation!.AddUserMessage(errorFeedback);
        }

        // Exception handling is done in StepBase. Exception breaks retry loop in StepBase
        // Recursive tool execution - handles all tool calls internally
        var response = await InvokeLlmWithToolsAsync(_request!, context, 0, null, cancellationToken);

        Logger.LogDebug("LLM step {StepName} received response, tokens: {Tokens}, finish reason: {Reason}",
            Name, response.TokensUsed, response.FinishReason);
        Logger.LogTrace("LLM step {StepName} response content: {Content}", Name, response.Content);

        // Parse the final response
        (var result, var parseError) = await ParseLlmResponseAsync(response, context);

        // If parsing failed, return error result
        if (result == null || parseError != null)
        {
            Logger.LogWarning(
                "LLM response parsing failed (attempt {Attempt}/{Max}): {Error}",
                attempt, MaxRetries, parseError);

            // Don't restore - keep tool results for next retry
            return CreateErrorResult(parseError ?? "Parsing returned null result");
        }

        return result;
    }

    /// <summary>
    /// Recursively invokes LLM, executes tools, and re-invokes until no more tool calls.
    /// </summary>
    private async Task<LlmResponse> InvokeLlmWithToolsAsync(
        LlmRequest request,
        PipelineContext context,
        int toolIteration,
        HashSet<string>? previousToolHashes,
        CancellationToken cancellationToken)
    {
        if (toolIteration >= MaxToolIterations)
        {
            throw new InvalidOperationException(
                $"Maximum tool iterations ({MaxToolIterations}) exceeded - possible infinite loop");
        }

        using var llmActivity = Telemetry.Source.StartActivity(
            $"LLM.Invoke",
            ActivityKind.Client);
        llmActivity?.SetTag(AITaskAgentConstants.TelemetryTags.LlmModel, request.Profile.Model);
        llmActivity?.SetTag(AITaskAgentConstants.TelemetryTags.LlmProvider, request.Profile.Provider.ToString());
        llmActivity?.SetTag("llm.iteration", toolIteration);
        llmActivity?.SetTag("llm.has_tools", request.Tools?.Count > 0);

        Logger.LogDebug("LLM invoke starting (iteration {Iteration}), model: {Model}, tools available: {ToolCount}",
            toolIteration, request.Profile.Model, request.Tools?.Count ?? 0);

        // Invoke LLM (HTTP retry handled by ILlmService implementation)
        var llmStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var response = await InvokeLlmAsync(request, context, Name, cancellationToken);
        llmStopwatch.Stop();

        llmActivity?.SetTag(AITaskAgentConstants.TelemetryTags.TokensUsed, response.TokensUsed);
        llmActivity?.SetTag("llm.finish_reason", response.FinishReason);

        // Record LLM duration metric
        Metrics.LlmDuration.Record(llmStopwatch.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmModel, profile.Model),
            new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmProvider, profile.Provider));

        UpdateContextMetrics(context, response);

        // No tool calls? Return final response
        if (response.ToolCalls == null || response.ToolCalls.Count == 0)
        {
            Logger.LogDebug("LLM returned final response (no tool calls), tokens: {Tokens}",
                response.TokensUsed);
            return response;
        }

        // Create normalized hashes for current tool calls to detect loops
        HashSet<string> currentToolHashes = [.. response.ToolCalls.Select(LlmHelpers.NormalizeToolCallKey)];

        // Detect potential loop: same exact tool calls as previous iteration
        if (previousToolHashes != null && currentToolHashes.SetEquals(previousToolHashes))
        {
            Logger.LogWarning(
                "Detected potential tool loop: LLM requested same tools as previous iteration. " +
                "Forcing final response without tools.");

            // Return current response as final (ignore tool calls)
            return new LlmResponse
            {
                Content = response.Content,
                TokensUsed = response.TokensUsed,
                PromptTokens = response.PromptTokens,
                CompletionTokens = response.CompletionTokens,
                CostUsd = response.CostUsd,
                FinishReason = response.FinishReason,
                Model = response.Model,
                NativeResponse = response.NativeResponse,
                ToolCalls = null  // Force no tools
            };
        }

        // Deduplicate tool calls within this batch
        var uniqueToolCalls = DeduplicateToolCalls(response.ToolCalls);

        Logger.LogInformation("LLM requested {ToolCount} tool calls (iteration {Iteration}), executing {UniqueCount} unique",
            response.ToolCalls.Count, toolIteration + 1, uniqueToolCalls.Count);

        // Add the assistant message with tool calls to conversation
        // This is required for the LLM to understand the full context
        context.Conversation?.AddAssistantMessageWithToolCalls(response.ToolCalls);

        // Execute unique tools and add their results
        await ExecuteToolCallsAsync(Name, uniqueToolCalls, response.ToolCalls, tools, context, cancellationToken);

        // Recursive call with current hashes for loop detection
        return await InvokeLlmWithToolsAsync(request, context, toolIteration + 1, currentToolHashes, cancellationToken);
    }

    /// <summary>
    /// Deduplicates tool calls within the same batch based on function name and arguments.
    /// </summary>
    private List<ToolCall> DeduplicateToolCalls(List<ToolCall> toolCalls)
    {
        Dictionary<string, ToolCall> uniqueCalls = [];

        foreach (var toolCall in toolCalls)
        {
            var key = LlmHelpers.NormalizeToolCallKey(toolCall);

            if (!uniqueCalls.ContainsKey(key))
            {
                uniqueCalls[key] = toolCall;
            }
        }

        if (uniqueCalls.Count < toolCalls.Count)
        {
            Logger.LogWarning(
                "Deduplicated {Removed} duplicate tool calls from batch of {Total}",
                toolCalls.Count - uniqueCalls.Count, toolCalls.Count);
        }

        return [.. uniqueCalls.Values];
    }

    /// <summary>
    /// Executes unique tool calls and adds results to conversation following OpenAI protocol.
    /// Handles duplicates by reusing results for the same function+args combination.
    /// </summary>
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

            // Add error messages for all tool calls
            foreach (var toolCall in allToolCalls)
            {
                conversation?.AddToolMessage(toolCall.Id, "Error: No tools available");
            }
            return;
        }

        // Execute unique tools and cache results
        Dictionary<string, string> resultCache = [];

        foreach (var toolCall in uniqueToolCalls)
        {
            var cacheKey = LlmHelpers.NormalizeToolCallKey(toolCall);
            var tool = availableTools.FirstOrDefault(t => t.Name == toolCall.Name);

            if (tool == null)
            {
                Logger.LogError("Tool '{ToolName}' not found in registry", toolCall.Name);
                resultCache[cacheKey] = $"Error: Tool '{toolCall.Name}' not found";

                // Send error event manually since tool doesn't exist
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
                // LlmTool.ExecuteAsync now handles all observability internally
                // (traces, events, metrics, hooks)
                var result = await tool.ExecuteAsync(
                    toolCall.Arguments,
                    context,
                    stepName,
                    Logger,
                    cancellationToken);

                resultCache[cacheKey] = result ?? string.Empty;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Tool {ToolName} execution failed", toolCall.Name);
                resultCache[cacheKey] = $"Error executing tool: {ex.Message}";
                // Note: LlmTool.ExecuteAsync already sent the failure event
            }
        }

        // Add results to conversation for ALL tool calls (including duplicates)
        // OpenAI protocol requires a response for each tool_call_id
        foreach (var toolCall in allToolCalls)
        {
            var cacheKey = LlmHelpers.NormalizeToolCallKey(toolCall);
            var result = resultCache.GetValueOrDefault(cacheKey, "Error: Result not found");
            conversation?.AddToolMessage(toolCall.Id, result);
        }
    }

    /// <summary>
    /// Invokes LLM with optional streaming based on request configuration.
    /// Always sends traces/stream to context observers when available.
    /// HTTP retry logic is handled by the ILlmService implementation.
    /// </summary>
    private async Task<LlmResponse> InvokeLlmAsync(
        LlmRequest request,
        PipelineContext context,
        string stepName,
        CancellationToken cancellationToken
        )
    {
        // LOG input prompts at Debug level
        if (Logger.IsEnabled(LogLevel.Debug))
        {
            var prompts = request.Conversation.GetMessagesForRequest(
                maxTokens: request.SlidingWindowMaxTokens,
                useSlidingWindow: request.UseSlidingWindow);

            var promptLog = string.Join("\n---\n", prompts.Select(m => $"[{m.Role}]: {m.Content}"));
            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                promptLog = $"[System]: {request.SystemPrompt}\n---\n{promptLog}";
            }
            Logger.LogDebug("[LLM] Step {StepName} Request:\n{Prompts}", stepName, promptLog);
        }

        // If streaming is not requested, invoke directly
        if (!request.UseStreaming)
        {
            var response = await _llmService.InvokeAsync(request, cancellationToken);

            // Send complete response to observers
            if (!string.IsNullOrEmpty(response.Content))
            {
                // LOG full response content at Debug level for traceability
                Logger.LogDebug("[LLM] Step {StepName} Final Response:\n{Content}\n[Finish: {FinishReason}, Tokens: {Tokens}]",
                    stepName, response.Content, response.FinishReason, response.TokensUsed);

                // Parse finish reason to enum
                var (reason, rawReason) = FinishReasonExtensions.Parse(response.FinishReason);
                if (reason == FinishReason.Other)
                {
                    Logger.LogWarning("Unknown LLM finish reason: {RawReason}", rawReason);
                }

                // Send to EventChannel if available
                await context.SendEventAsync(new LlmResponseEvent
                {
                    StepName = stepName,
                    Content = response.Content,
                    FinishReason = reason,
                    RawFinishReason = rawReason,
                    TokensUsed = response.TokensUsed ?? 0,
                    Model = profile.Model,
                    Provider = profile.Provider,
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

        // Streaming mode: accumulate content and send to observers
        var contentBuilder = new StringBuilder();
        Dictionary<int, (string? Id, string? Name, StringBuilder Args)> toolCallBuilders = [];
        string? finishReason = null;
        var totalTokens = 0;

        await foreach (var chunk in _llmService.InvokeStreamingAsync(request, cancellationToken))
        {
            if (!string.IsNullOrEmpty(chunk.Delta))
            {
                contentBuilder.Append(chunk.Delta);

                // Send streaming chunk to EventChannel (FinishReason.Streaming = chunk)
                await context.SendEventAsync(new LlmResponseEvent
                {
                    StepName = stepName,
                    Content = chunk.Delta,
                    FinishReason = FinishReason.Streaming,
                    CorrelationId = context.CorrelationId
                }, cancellationToken);
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
                    {
                        toolCallBuilders[update.Index] = (update.ToolCallId, builder.Item2, builder.Item3);
                    }

                    if (update.FunctionName != null)
                    {
                        toolCallBuilders[update.Index] = (builder.Item1, update.FunctionName, builder.Item3);
                    }

                    if (update.FunctionArgumentsUpdate != null)
                    {
                        builder.Args.Append(update.FunctionArgumentsUpdate);
                    }
                }
            }

            if (chunk.IsComplete)
            {
                finishReason = chunk.FinishReason;
                // Capture tokens from final chunk if available
                if (chunk.TokensUsed.HasValue)
                {
                    totalTokens = chunk.TokensUsed.Value;
                }
            }
        }

        // Send final event with FinishReason to signal stream end
        var (finalReason, finalRawReason) = FinishReasonExtensions.Parse(finishReason);
        if (finalReason == FinishReason.Other)
        {
            Logger.LogWarning("Unknown LLM finish reason in streaming: {RawReason}", finalRawReason);
        }

        await context.SendEventAsync(new LlmResponseEvent
        {
            StepName = stepName,
            Content = string.Empty, // Final event has empty content, just signals completion
            FinishReason = finalReason,
            RawFinishReason = finalRawReason,
            TokensUsed = totalTokens,
            Model = profile.Model,
            Provider = profile.Provider,
            CorrelationId = context.CorrelationId
        }, cancellationToken);

        List<ToolCall> toolCalls = [];
        foreach (var kvp in toolCallBuilders.OrderBy(k => k.Key))
        {
            (var id, var name, var args) = kvp.Value;
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
            {
                toolCalls.Add(new ToolCall
                {
                    Id = id,
                    Name = name,
                    Arguments = args?.ToString() ?? string.Empty
                });
            }
        }

        return new LlmResponse
        {
            Content = contentBuilder.ToString(),
            ToolCalls = toolCalls,
            FinishReason = finishReason ?? "stop",
            Model = request.Profile.Model,
            TokensUsed = totalTokens  // Use tokens from final chunk
        };
    }


    protected virtual ConversationContext GetConversationContext(PipelineContext context) => context.Conversation;

    protected virtual void AddConversationToContext(PipelineContext context, ConversationContext conversation, ILlmStepResult result)
    {
    }

    /// <summary>
    /// Builds the LLM request for this step.
    /// Can be overridden for custom request building logic.
    /// </summary>
    protected virtual async Task<LlmRequest> BuildLlmRequestAsync(
        TIn input,
        PipelineContext context,
        ConversationContext conversation,
        CancellationToken cancellationToken)
    {
        // Build system prompt if provided
        string? systemPrompt = null;
        if (systemMessageBuilder != null)
        {
            systemPrompt = await systemMessageBuilder(input, context);
        }

        // Convert tools to ToolDefinition format if provided
        var toolDefinitions = tools?.Select(t => t.GetDefinition()).ToList();

        var request = new LlmRequest()
        {
            Conversation = conversation, // <- must be conversation reference in order to maintain conversation state. Llm service will use this to build the conversation context. 
            SystemPrompt = systemPrompt,
            Profile = profile,
            Temperature = profile.Temperature,
            MaxTokens = profile.MaxTokens,
            TopP = profile.TopP,
            TopK = profile.TopK,
            FrequencyPenalty = profile.FrequencyPenalty,
            PresencePenalty = profile.PresencePenalty,
            Tools = toolDefinitions
        };

        // Allow derived classes to customize request parameters (temperature, tokens, etc.)
        request = ConfigureLlmRequest(request, context);

        // Configure JSON response format based on TOut and profile capabilities
        request = ConfigureJsonResponse(request, _message!);

        return request;
    }

    protected virtual LlmRequest ConfigureLlmRequest(LlmRequest request, PipelineContext context)
    {
        return request;
    }

    /// <summary>
    /// Parses the LLM response into the typed result.
    /// Default implementation deserializes JSON to T and creates TOut with metrics.
    /// Override for custom parsing logic.
    /// </summary>
    /// <returns>Tuple of (result, error). If parsing fails, result is null and error contains the message.</returns>
    protected virtual Task<(TOut? Result, string? Error)> ParseLlmResponseAsync(
        LlmResponse response,
        PipelineContext context)
    {
        TOut? result = default;

        string? errorMsg;
        try
        {
            object? valueObj;

            if (ValueType == null)
            {
                // String: use content directly
                Logger.LogDebug("Response type is string, using content directly");
                valueObj = response.Content;
            }
            else if (!ValueType.IsClass || ValueType.IsPrimitive || ValueType == typeof(string))
            {
                // Primitive types (int, bool, DateTime, etc.): convert from string
                Logger.LogDebug("Response type is {Type}, converting from string", ValueType.Name);
                valueObj = LlmHelpers.ConvertOrThrow(response.Content, ValueType);
            }
            else
            {
                // Complex types: parse as JSON
                var cleanContent = LlmHelpers.CleanJsonResponse(response.Content);
                Logger.LogDebug("Parsing JSON response as {Type}", ValueType.Name);

                valueObj = JsonConvert.DeserializeObject(cleanContent, ValueType);

                if (valueObj == null)
                {
                    errorMsg = $"Your response could not be parsed as {ValueType.Name}.\nPlease ensure you return valid JSON matching the schema.";
                    return Task.FromResult<(TOut?, string?)>((default, errorMsg));
                }
            }

            result = CreateResult(valueObj);

            SetLlmMetrics(result, response);
            return Task.FromResult<(TOut?, string?)>((result, null));
        }
        catch (JsonException ex)
        {
            errorMsg = $"Your response contains invalid JSON. Error: {ex.Message}.\nPlease fix the JSON syntax and ensure it matches the required schema.";
        }
        catch (Exception ex)
        {
            errorMsg = $"Failed to parse response: {ex.Message}";
        }

        return Task.FromResult<(TOut?, string?)>((result, errorMsg));
    }

    /// <summary>
    /// Sets LLM metrics on the result if it implements ILlmStepResult.
    /// </summary>
    private static void SetLlmMetrics(TOut? result, LlmResponse response)
    {
        if (result is not null and LlmStepResult llmResult)
        {
            // AssistantMessage has setter
            llmResult.AssistantMessage = response.Content;
            llmResult.Model = response.Model;
            llmResult.TokensUsed = response.TokensUsed;
            llmResult.CostUsd = response.CostUsd;
            llmResult.FinishReason = response.FinishReason;
        }
    }

    /// <summary>
    /// Updates context with LLM metrics using native .NET Meters.
    /// </summary>
    private void UpdateContextMetrics(PipelineContext context, LlmResponse response)
    {
        // Record LLM request
        Metrics.LlmRequests.Add(1,
            new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmModel, profile.Model),
            new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmProvider, profile.Provider));

        if (response.TokensUsed.HasValue)
        {
            context.Metadata["TokensUsed"] = response.TokensUsed.Value;
            Metrics.LlmTokens.Add(response.TokensUsed.Value,
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmModel, profile.Model),
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmProvider, profile.Provider));
        }

        if (response.CostUsd.HasValue)
        {
            context.Metadata["CostUsd"] = response.CostUsd.Value;
            Metrics.LlmCost.Add((double)response.CostUsd.Value,
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmModel, profile.Model),
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmProvider, profile.Provider));
        }
    }

    #region JSON Response Configuration

    /// <summary>
    /// Configures the JSON response format based on TOut and the LLM profile capabilities.
    /// </summary>
    private LlmRequest ConfigureJsonResponse(
        LlmRequest request,
        string userMessage)
    {
        // Only configure JSON if TOut requires it (is a class that needs JSON parsing)
        if (Schema == null)
        {
            // IMPORTANT: Even if no JSON schema is required, we MUST add the user message to the conversation.
            return request with
            {
                Conversation = request.Conversation.AddUserMessage(userMessage)
            };
        }

        Logger.LogDebug(
            "Configuring JSON response for {TypeName} with profile capability: {Capability}",
            ValueType.Name, request.Profile.JsonCapability);

        return request.Profile.JsonCapability switch
        {
            // LLM supports JSON Schema natively - use structured output
            JsonResponseCapability.JsonSchema => request with
            {
                ResponseFormat = new ResponseFormatOptions
                {
                    Type = ResponseFormatType.JsonObject,
                    JsonSchema = Schema
                },
                Conversation = request.Conversation.AddUserMessage(userMessage)
            },

            // LLM supports JSON Object but not schema - inject schema in system prompt
            JsonResponseCapability.JsonObject => request with
            {
                ResponseFormat = new ResponseFormatOptions
                {
                    Type = ResponseFormatType.JsonObject
                },
                SystemPrompt = LlmHelpers.InjectSchemaInSystemPrompt(request.SystemPrompt, Schema),
                Conversation = request.Conversation.AddUserMessage(userMessage)
            },

            // LLM doesn't support JSON natively - inject schema in user message
            _ => request with
            {
                Conversation = request.Conversation.AddUserMessage(LlmHelpers.InjectSchemaInUserMessage(userMessage, Schema))
            }
        };

    }


    #endregion
}
