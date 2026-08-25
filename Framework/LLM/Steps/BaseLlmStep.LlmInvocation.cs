using AITaskAgent.Core;
using AITaskAgent.Core.Base;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Streaming;
using AITaskAgent.LLM.Support;
using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AITaskAgent.LLM.Steps;

public abstract partial class BaseLlmStep
{
    private static readonly Regex _tagRegex =
        TagRegex();

    private static readonly Regex _attrRegex =
        AttrRegex();

    private async Task<LlmResult> InvokeLlmWithToolsAsync(
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
        if (llmActivity != null)
        {
            llmActivity.SetTag(AITaskAgentConstants.TelemetryTags.LlmModel, Model);
            llmActivity.SetTag(AITaskAgentConstants.TelemetryTags.LlmProvider, LlmService?.Name);
            llmActivity.SetTag("llm.iteration", toolIteration);
            llmActivity.SetTag("llm.has_tools", request.Tools?.Count > 0);
        }

        if (Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug(
                "LLM invoke starting (iteration {Iteration}), model: {Model}, tools available: {ToolCount}",
                toolIteration,
                Model,
                request.Tools?.Count ?? 0);
        }

        var llmStopwatch = Stopwatch.StartNew();
        var result = await InvokeLlmAsync(request, context, Name, cancellationToken);
        llmStopwatch.Stop();

        if (llmActivity != null)
        {
            llmActivity.SetTag(AITaskAgentConstants.TelemetryTags.TokensUsed, result.TokensUsed);
            llmActivity.SetTag("llm.finish_reason", result.FinishReason);
        }

        Metrics.LlmDuration.Record(
            llmStopwatch.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmModel, Model),
            new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmProvider, LlmService?.Name));

        UpdateContextMetrics(context, result);

        var primaryCandidate = result.Candidates.FirstOrDefault();

        if (!result.IsSuccess || primaryCandidate == null)
        {
            if (result.IsSuccess)
            {
                return LlmResult.Failure(new Exception("No response from LLM"), true);
            }
            return result;
        }

        if (primaryCandidate.ToolCalls == null || primaryCandidate.ToolCalls.Count == 0)
        {
            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug(
                    "LLM returned final response (no tool calls), tokens: {Tokens}",
                    result.TokensUsed);
            }

            return result;
        }

        // Canonicaliza la respuesta antes de persistirla en conversación.
        primaryCandidate = CanonicalizeToolCallsResponse(primaryCandidate);

        var toolCalls = primaryCandidate.ToolCalls!;

        if (IsStuckRepeatingToolCalls(toolCalls, out var stuckMessage))
        {
            return StopWithMessage(result, primaryCandidate, stuckMessage);
        }

        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation(
                "LLM requested {ToolCount} canonical tool calls (iteration {Iteration})",
                toolCalls.Count,
                toolIteration + 1);
        }
        if (Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug(
                "Tool calls (iteration {Iteration}): {ToolNames}",
                toolIteration + 1,
                string.Join(", ", toolCalls.Select(c => c.Name)));

            foreach (var call in toolCalls)
            {
                Logger.LogDebug(
                    "  → iteration {Iteration} call: {Name}({Args})",
                    toolIteration + 1,
                    call.Name,
                    call.Arguments);
            }
        }

        _conversation?.AddAssistantMessageWithToolCalls(toolCalls);

        await ExecuteToolCallsAsync(
            Name,
            toolCalls,
            Tools,
            context,
            cancellationToken);

        // Recursively invoke LLM after tool results are added to conversation.
        // We accumulate tokens and cost across iterations to provide an accurate total in the final result.
        var nextResult = await InvokeLlmWithToolsAsync(
            request,
            context,
            toolIteration + 1,
            cancellationToken);

        if (!nextResult.IsSuccess)
        {
            return nextResult;
        }

        return LlmResult.Success(
            nextResult.Candidates,
            tokensUsed: (result.TokensUsed ?? 0) + (nextResult.TokensUsed ?? 0),
            promptTokens: (result.PromptTokens ?? 0) + (nextResult.PromptTokens ?? 0),
            completionTokens: (result.CompletionTokens ?? 0) + (nextResult.CompletionTokens ?? 0),
            costUsd: (result.CostUsd ?? 0) + (nextResult.CostUsd ?? 0),
            model: nextResult.Model,
            nativeResponse: nextResult.NativeResponse
        );
    }

    /// <summary>Keys of the tool calls requested in the previous iteration.</summary>
    private HashSet<string>? _lastToolCallKeys;

    /// <summary>How many consecutive iterations have asked for exactly the same calls.</summary>
    private int _consecutiveIdenticalToolCalls;

    /// <summary>
    /// Detects a model looping on the same tool calls.
    ///
    /// MaxToolIterations already bounds the loop, but waiting for it wastes several round-trips and
    /// tells the model nothing. Comparing each iteration against the previous one cuts the loop as
    /// soon as it is clear no progress is being made.
    ///
    /// Read-only calls get more rope (3 repeats) than mutating ones (1): re-reading a file can be
    /// legitimate exploration, whereas repeating a write with identical arguments cannot.
    /// </summary>
    private bool IsStuckRepeatingToolCalls(List<ToolCall> toolCalls, out string message)
    {
        message = "";

        var keys = toolCalls.Select(LlmHelpers.NormalizeToolCallKey).ToHashSet();

        if (_lastToolCallKeys is null || !keys.SetEquals(_lastToolCallKeys))
        {
            _lastToolCallKeys = keys;
            _consecutiveIdenticalToolCalls = 0;
            return false;
        }

        _consecutiveIdenticalToolCalls++;

        var allReadOnly = toolCalls.All(call =>
            Tools.FirstOrDefault(t => t.Name.Equals(call.Name, StringComparison.OrdinalIgnoreCase))?.IsReadOnly == true);

        var threshold = allReadOnly ? 3 : 1;

        if (_consecutiveIdenticalToolCalls <= threshold)
        {
            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug(
                    "Step {StepName} repeated the same tool calls ({Count}/{Threshold} before stopping)",
                    Name, _consecutiveIdenticalToolCalls, threshold);
            }
            return false;
        }

        var names = string.Join(", ", toolCalls.Select(c => c.Name).Distinct());
        Logger.LogWarning(
            "Step {StepName} stopped: tool calls [{Tools}] repeated {Count} times without progress",
            Name, names, _consecutiveIdenticalToolCalls + 1);

        message =
            $"Execution stopped: [{names}] was called {_consecutiveIdenticalToolCalls + 1} times in a row " +
            "with identical arguments and no progress. Answer with the information you already have.";

        return true;
    }

    /// <summary>
    /// Turns a tool-calling response into a final one carrying <paramref name="message"/>, so the
    /// model learns why it was cut off instead of the step failing opaquely.
    /// </summary>
    private static LlmResult StopWithMessage(LlmResult result, LlmResponse candidate, string message)
    {
        var stopped = candidate with
        {
            Content = string.IsNullOrWhiteSpace(candidate.Content) ? message : candidate.Content,
            ToolCalls = null,
            FinishReason = FinishReason.Stop
        };

        return LlmResult.Success(
            [stopped],
            tokensUsed: result.TokensUsed,
            promptTokens: result.PromptTokens,
            completionTokens: result.CompletionTokens,
            costUsd: result.CostUsd,
            model: result.Model,
            nativeResponse: result.NativeResponse);
    }

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
            if (Logger.IsEnabled(LogLevel.Warning))
            {
                Logger.LogWarning("Deduplicated {Removed} duplicate tool calls from batch of {Total}",
                    toolCalls.Count - uniqueCalls.Count, toolCalls.Count);
            }
        }

        return [.. uniqueCalls.Values];
    }

    private async Task ExecuteToolCallsAsync(
        string stepName,
        List<ToolCall> toolCalls,
        IList<ITool>? availableTools,
        PipelineContext context,
        CancellationToken cancellationToken)
    {

        if (availableTools == null || availableTools.Count == 0)
        {
            if (Logger.IsEnabled(LogLevel.Warning))
            {
                Logger.LogWarning("No tools available, cannot execute tool calls");
            }

            foreach (var toolCall in toolCalls)
            {
                _conversation?.AddToolMessage(toolCall.Id, "Error: No tools available");
            }

            return;
        }

        foreach (var toolCall in toolCalls)
        {
            var tool = availableTools.FirstOrDefault(t => t.Name == toolCall.Name);

            if (tool == null)
            {
                if (Logger.IsEnabled(LogLevel.Error))
                {
                    Logger.LogError("Tool '{ToolName}' not found in registry", toolCall.Name);
                }
                _conversation?.AddToolMessage(toolCall.Id, $"Error: Tool '{toolCall.Name}' not found");
                continue;
            }

            if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInformation("Executing Tool: {ToolName} with arguments: {Arguments}", toolCall.Name, toolCall.Arguments);
            }

            try
            {
                var result = await tool.ExecuteAsync(
                    toolCall.Arguments,
                    context,
                    stepName,
                    Logger,
                    cancellationToken);

                _conversation?.AddToolMessage(toolCall.Id, result ?? string.Empty);
            }
            catch (Exception ex)
            {
                if (Logger.IsEnabled(LogLevel.Error))
                {
                    Logger.LogError(ex, "Tool {ToolName} execution failed", toolCall.Name);
                }
                _conversation?.AddToolMessage(toolCall.Id, $"Error executing tool: {ex.Message}");
            }
        }
    }

    private async Task<LlmResult> InvokeLlmAsync(
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
            promptLog = $"[System]: {(string.IsNullOrEmpty(request.SystemPrompt) ? "<Empty>" : request.SystemPrompt)}\n---\n{promptLog}";

            if (request.Tools?.Count > 0)
            {
                var toolsLog = string.Join(", ", request.Tools.Select(t => t.Name));
                promptLog += $"\n---\n[Tools]: {toolsLog}";
            }

            Logger.LogTrace("[LLM] Step {StepName} Request:\n{Prompts}", stepName, promptLog);
        }

        LlmResult result;
        if (!request.UseStreaming)
        {
            // Direct invocation without internal fallback loop. 
            // The RetryMiddleware handles validation-based retries for structural/semantic failures and step errors without exceptions.
            result = await LlmService.InvokeAsync(request, cancellationToken);
        }
        else
        {
            // Accumulators per choice index to support multiple candidates (n > 1)
            Dictionary<int, StringBuilder> contentBuilders = [];
            Dictionary<int, StringBuilder> reasoningBuilders = [];
            Dictionary<int, Dictionary<int, (string? Id, string? Name, StringBuilder Args)>> choiceToolCallBuilders = [];
            Dictionary<int, FinishReason> finishReasons = [];
            Dictionary<int, string?> rawFinishReasons = [];
            var totalTokens = 0;
            int? promptTokens = null;
            int? completionTokens = null;

            StreamingTagParser tagParser = new(StreamingHandlers ?? []);

            await foreach (var chunk in LlmService.InvokeStreamingAsync(request, cancellationToken))
            {
                var choiceIdx = chunk.ChoiceIndex;

                // Ensure builders exist for this choice
                if (!contentBuilders.ContainsKey(choiceIdx)) contentBuilders[choiceIdx] = new StringBuilder();
                if (!reasoningBuilders.ContainsKey(choiceIdx)) reasoningBuilders[choiceIdx] = new StringBuilder();
                if (!choiceToolCallBuilders.ContainsKey(choiceIdx)) choiceToolCallBuilders[choiceIdx] = [];

                if (!string.IsNullOrEmpty(chunk.Delta))
                {
                    if (chunk.IsThinking)
                    {
                        reasoningBuilders[choiceIdx].Append(chunk.Delta);

                        await context.SendEventAsync(new LlmResponseEvent
                        {
                            StepName = stepName,
                            Content = chunk.Delta,
                            FinishReason = FinishReason.Streaming,
                            IsThinking = true,
                            ChoiceIndex = choiceIdx,
                            CorrelationId = context.CorrelationId
                        }, cancellationToken);
                    }
                    else
                    {
                        var cleanDelta = chunk.Delta;
                        if (!string.IsNullOrEmpty(cleanDelta))
                        {
                            cleanDelta = await tagParser.ProcessChunkAsync(cleanDelta, context, cancellationToken);
                        }

                        if (!string.IsNullOrEmpty(cleanDelta))
                        {
                            contentBuilders[choiceIdx].Append(cleanDelta);

                            await context.SendEventAsync(new LlmResponseEvent
                            {
                                StepName = stepName,
                                Content = cleanDelta,
                                FinishReason = FinishReason.Streaming,
                                IsThinking = false,
                                ChoiceIndex = choiceIdx,
                                CorrelationId = context.CorrelationId
                            }, cancellationToken);
                        }
                    }
                }

                if (chunk.ToolCallUpdates != null)
                {
                    var toolBuilders = choiceToolCallBuilders[choiceIdx];
                    foreach (var update in chunk.ToolCallUpdates)
                    {
                        if (!toolBuilders.TryGetValue(update.Index, out var builder))
                        {
                            builder = (null, null, new StringBuilder());
                        }

                        if (update.ToolCallId != null) builder.Id = update.ToolCallId;
                        if (update.FunctionName != null) builder.Name = update.FunctionName;
                        if (update.FunctionArgumentsUpdate != null) builder.Args.Append(update.FunctionArgumentsUpdate);

                        toolBuilders[update.Index] = builder;
                    }
                }

                if (chunk.IsComplete)
                {
                    if (chunk.FinishReason.HasValue) finishReasons[choiceIdx] = chunk.FinishReason.Value;
                    if (chunk.RawFinishReason != null) rawFinishReasons[choiceIdx] = chunk.RawFinishReason;

                    if (chunk.TokensUsed.HasValue)
                    {
                        totalTokens = chunk.TokensUsed.Value;
                    }
                }

                // El bloque usage (con el reparto prompt/completion) puede llegar en el chunk
                // del finish_reason o en un chunk final separado (Ollama). Captúralo siempre.
                if (chunk.PromptTokens.HasValue) promptTokens = chunk.PromptTokens.Value;
                if (chunk.CompletionTokens.HasValue) completionTokens = chunk.CompletionTokens.Value;
            }

            // Finalize all candidates
            List<LlmResponse> candidates = [];
            foreach (var choiceIdx in contentBuilders.Keys.OrderBy(k => k))
            {
                var content = contentBuilders[choiceIdx].ToString();
                var reasoning = reasoningBuilders[choiceIdx].Length > 0 ? reasoningBuilders[choiceIdx].ToString() : null;
                var fReason = finishReasons.GetValueOrDefault(choiceIdx, FinishReason.Stop);
                var rawFReason = rawFinishReasons.GetValueOrDefault(choiceIdx);

                List<ToolCall> toolCalls = [];
                if (choiceToolCallBuilders.TryGetValue(choiceIdx, out var toolBuilders))
                {
                    foreach (var kvp in toolBuilders.OrderBy(k => k.Key))
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
                }

                candidates.Add(new LlmResponse
                {
                    Index = choiceIdx,
                    Content = content,
                    Reasoning = reasoning,
                    ToolCalls = toolCalls,
                    FinishReason = fReason,
                    RawFinishReason = rawFReason
                });
            }

            // If no candidates were streamed but it was "successful", something is wrong, but we handle it
            if (candidates.Count == 0)
            {
                candidates.Add(new LlmResponse { Index = 0, Content = string.Empty });
            }

            var primaryCandidate = candidates[0];
            var finalReason = primaryCandidate.FinishReason ?? FinishReason.Other;

            await context.SendEventAsync(new LlmResponseEvent
            {
                StepName = stepName,
                Content = string.Empty,
                ToolCalls = primaryCandidate.ToolCalls,
                FinishReason = finalReason,
                RawFinishReason = primaryCandidate.RawFinishReason,
                ChoiceIndex = primaryCandidate.Index,
                TokensUsed = totalTokens,
                Model = Model,
                Provider = LlmService?.Name,
                CorrelationId = context.CorrelationId
            }, cancellationToken);

            result = LlmResult.Success(
                candidates,
                tokensUsed: totalTokens,
                promptTokens: promptTokens,
                completionTokens: completionTokens,
                model: Model);
        }

        // Services report tokens but not cost: they never see the provider's model catalogue.
        // Filling it here covers every provider at once and keeps LlmProvider.Models - which
        // declares the per-million prices - from being configuration nobody reads.
        if (result.CostUsd is null && result.IsSuccess)
        {
            result.CostUsd = Provider.CalculateCost(result.Model ?? Model, result.PromptTokens, result.CompletionTokens);
        }

        // ── Normalización post-ensamblado ──

        foreach (var candidate in result.Candidates)
        {
            if (candidate == null) continue;

            if (Logger.IsEnabled(LogLevel.Trace))
            {
                var toolsInfo = "";
                if (candidate.ToolCalls != null && candidate.ToolCalls.Count > 0)
                {
                    var calls = candidate.ToolCalls.Select(tc => $"{tc.Name}({tc.Arguments})");
                    toolsInfo = $"\n[ToolCalls]: {string.Join(", ", calls)}";
                }

                Logger.LogTrace("[LLM] Step {StepName} Final Response:\n{Content}{ToolsInfo}\n[Finish: {FinishReason}, Tokens: {Tokens}]",
                    stepName, candidate.Content, toolsInfo, candidate.FinishReason, result.TokensUsed);
            }

            if (!request.UseStreaming)
            {
                var reason = candidate.FinishReason ?? FinishReason.Other;
                var rawReason = candidate.RawFinishReason;

                if (reason == FinishReason.Other)
                {
                    if (Logger.IsEnabled(LogLevel.Warning))
                    {
                        Logger.LogWarning("Unknown LLM finish reason: {RawReason}", rawReason);
                    }
                }

                // Send thinking event first if present
                if (!string.IsNullOrEmpty(candidate.Reasoning))
                {
                    await context.SendEventAsync(new LlmResponseEvent
                    {
                        StepName = stepName,
                        Content = candidate.Reasoning,
                        IsThinking = true,
                        ChoiceIndex = candidate.Index,
                        FinishReason = FinishReason.Streaming, // Indicates more is coming
                        CorrelationId = context.CorrelationId
                    }, cancellationToken);
                }

                if (!string.IsNullOrEmpty(candidate.Content) || (candidate.ToolCalls != null && candidate.ToolCalls.Count > 0))
                {
                    await context.SendEventAsync(new LlmResponseEvent
                    {
                        StepName = stepName,
                        Content = candidate.Content ?? string.Empty,
                        ToolCalls = candidate.ToolCalls,
                        FinishReason = reason,
                        RawFinishReason = rawReason,
                        ChoiceIndex = candidate.Index,
                        TokensUsed = result.TokensUsed ?? 0,
                        Model = Model,
                        Provider = LlmService?.Name,
                        CorrelationId = context.CorrelationId
                    }, cancellationToken);
                }
            }
        }

        return result;
    }

    private async Task<string> ProcessTagsNonStreamingAsync(
        string content,
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        if (StreamingHandlers == null || StreamingHandlers.Count == 0 || string.IsNullOrEmpty(content))
        {
            return content ?? string.Empty;
        }

        var handlerDict = StreamingHandlers.ToDictionary(h => h.TagName);
        var tagRegex = _tagRegex;

        StringBuilder sb = new();
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
                    if (Logger.IsEnabled(LogLevel.Error))
                    {
                        Logger.LogError(ex, "Error processing tag {TagName}", tagName);
                    }
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
        {
            sb.Append(content[lastIndex..]);
        }

        return sb.ToString();
    }

    private static Dictionary<string, string> ParseAttributes(string attrString)
    {
        Dictionary<string, string> attributes = new();
        foreach (Match match in _attrRegex.Matches(attrString))
        {
            attributes[match.Groups[1].Value] = match.Groups[2].Value;
        }

        return attributes;
    }

    private LlmResponse CanonicalizeToolCallsResponse(LlmResponse response)
    {
        if (response.ToolCalls == null || response.ToolCalls.Count <= 1)
        {
            return response;
        }

        var uniqueToolCalls = DeduplicateToolCalls(response.ToolCalls);

        return uniqueToolCalls.Count == response.ToolCalls.Count ? response : CloneResponseWithToolCalls(response, uniqueToolCalls);
    }

    private static LlmResponse CloneResponseWithToolCalls(
    LlmResponse response,
    List<ToolCall>? toolCalls)
    {
        return new LlmResponse
        {
            Index = response.Index,
            Content = response.Content,
            Reasoning = response.Reasoning,
            FinishReason = response.FinishReason,
            RawFinishReason = response.RawFinishReason,
            ToolCalls = toolCalls
        };
    }

    [GeneratedRegex(@"<(\w+)([^>]*)>(.*?)</\1>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline, "es-ES")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"(\w+)\s*=\s*[""']([^""']*)[""']", RegexOptions.Compiled, "es-ES")]
    private static partial Regex AttrRegex();
}
