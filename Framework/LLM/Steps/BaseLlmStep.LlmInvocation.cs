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

namespace AITaskAgent.LLM.Steps;

public abstract partial class BaseLlmStep
{
    private async Task<LlmResult> InvokeLlmWithToolsAsync(
        LlmRequest request,
        PipelineContext context,
        int toolIteration,
        CancellationToken cancellationToken)
    {
        if (toolIteration >= MaxToolIterations)
        {
            // Typed so it is never taken for a provider outage: SwitchToNextFallback only changes
            // engine on LlmProviderException, so the same tool loop is not replayed against every
            // fallback. A derived step can still recognise it and pick another strategy - the
            // conversation keeps every tool result until the bookmark is restored.
            Logger.LogWarning(
                "Step {StepName} stopped: maximum tool iterations ({Max}) exceeded - possible infinite loop",
                Name, MaxToolIterations);

            return LlmResult.Failure(new ToolIterationLimitExceededException(Name, MaxToolIterations), false);
        }

        var model = GetModel(context);
        var llmServiceName = GetLlmService(context)?.Name;

        using var llmActivity = Telemetry.Source.StartActivity("LLM.Invoke", ActivityKind.Client);
        if (llmActivity != null)
        {
            llmActivity.SetTag(AITaskAgentConstants.TelemetryTags.LlmModel, model);
            llmActivity.SetTag(AITaskAgentConstants.TelemetryTags.LlmProvider, llmServiceName);
            llmActivity.SetTag("llm.iteration", toolIteration);
            llmActivity.SetTag("llm.has_tools", request.Tools?.Count > 0);
        }

        if (Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug(
                "LLM invoke starting (iteration {Iteration}), model: {Model}, tools available: {ToolCount}",
                toolIteration,
                model,
                request.Tools?.Count ?? 0);
        }

        var llmStopwatch = Stopwatch.StartNew();
        LlmResult result;
        try
        {
            result = await InvokeLlmAsync(request, context, Name, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A provider that throws (broken stream, open circuit...) instead of returning a Failure
            // is still a provider failure: typing it here is what lets SwitchToNextFallback tell it
            // apart from a failure of the step's own logic. A requested cancellation propagates as is.
            Logger.LogError(ex, "LLM invocation of step {StepName} threw (iteration {Iteration})", Name, toolIteration);
            return LlmResult.Failure(new LlmProviderException(ex.Message, ex, isRetriable: true), true);
        }
        llmStopwatch.Stop();

        if (llmActivity != null)
        {
            llmActivity.SetTag(AITaskAgentConstants.TelemetryTags.TokensUsed, result.TokensUsed);
            llmActivity.SetTag("llm.finish_reason", result.FinishReason);
        }

        Metrics.LlmDuration.Record(
            llmStopwatch.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmModel, model),
            new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.LlmProvider, llmServiceName));

        UpdateContextMetrics(context, result);

        var primaryCandidate = result.Candidates.FirstOrDefault();

        if (!result.IsSuccess || primaryCandidate == null)
        {
            if (result.IsSuccess)
            {
                // A successful call that carries no candidate leaves nothing to work with. Log it:
                // otherwise this failure surfaces to the user with no trace of where it came from.
                Logger.LogError(
                    "LLM reported success with no candidates (model: {Model}, iteration: {Iteration}, "
                    + "tokens: {Tokens}). Check the provider response logged by the LLM service.",
                    model,
                    toolIteration,
                    result.TokensUsed);

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

        // The text goes in with the tool calls. It carries the references of any tag that already
        // ran this turn, so the model can see in its own history that the action is done - dropping
        // it (the old behaviour) is what let the model repeat a write it had already performed.
        GetState(context).Conversation?.AddAssistantMessageWithToolCalls(toolCalls, primaryCandidate.Content);

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

    /// <summary>
    /// Tag names executed while processing the most recent LLM response, whichever channel carried
    /// them. Reset on every call to <see cref="InvokeLlmAsync"/> and read right after by
    /// <see cref="ExecuteToolCallsAsync"/>, which runs before the next invocation.
    /// </summary>
    private readonly HashSet<string> _tagsExecutedInLastResponse = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keys of the tool calls requested in the previous iteration.</summary>
    private HashSet<string>? _lastToolCallKeys;

    /// <summary>How many consecutive iterations have asked for exactly the same calls.</summary>
    private int _consecutiveIdenticalToolCalls;

    /// <summary>Forgets the previous turn's tool calls; called at the start of every turn.</summary>
    private void ResetToolCallRepeatTracking()
    {
        _lastToolCallKeys = null;
        _consecutiveIdenticalToolCalls = 0;
    }

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
        var conversation = GetState(context).Conversation;

        if (availableTools == null || availableTools.Count == 0)
        {
            if (Logger.IsEnabled(LogLevel.Warning))
            {
                Logger.LogWarning("No tools available, cannot execute tool calls");
            }

            foreach (var toolCall in toolCalls)
            {
                conversation?.AddToolMessage(toolCall.Id, "Error: No tools available");
            }

            return;
        }

        foreach (var toolCall in toolCalls)
        {
            var tool = availableTools.FirstOrDefault(t => t.Name == toolCall.Name);

            if (tool == null)
            {
                // A model calling a tag as if it were a tool isn't a normal recoverable tool
                // failure (wrong path, missing file) - it ignored the instructions already sitting
                // in its own system prompt (see EnrichSystemPromptWithTags). A generic
                // "not found" doesn't fix that: it doesn't point back at those instructions, so nothing
                // changes and the model repeats the exact same broken call. Restating the tag's own
                // GetInstructions() verbatim - not a paraphrase - gives it the exact usage spec again,
                // right when it is needed, instead of trusting it to go re-read a system prompt that
                // may be several turns and tool results back by now.
                var matchingTag = TagHandlers?.FirstOrDefault(
                    h => h.TagName.Equals(toolCall.Name, StringComparison.OrdinalIgnoreCase));

                string errorMessage;
                if (matchingTag != null && _tagsExecutedInLastResponse.Contains(toolCall.Name))
                {
                    // The model wrote the tag AND called it as a tool in the same response, so the
                    // action already happened. Restating GetInstructions() here would tell it to
                    // write the tag "now" - which it just did - and those contradictory
                    // instructions are what kept the loop alive: it cannot comply with both, so it
                    // retried the call until the repeat detector killed the step.
                    errorMessage =
                        $"'{toolCall.Name}' already ran: you wrote the tag in this same response and " +
                        "the action completed - its result is recorded in your message above. Do not " +
                        "call it as a tool and do not write the tag again. Just tell the user what " +
                        "you did.";

                    Logger.LogInformation(
                        "Model called '{ToolName}' as a tool, but its tag already ran in this response - "
                        + "told it the action is done instead of restating the instructions.",
                        toolCall.Name);
                }
                else if (matchingTag != null)
                {
                    errorMessage =
                        $"Error: '{toolCall.Name}' is not a callable tool. It is a tag documented in your " +
                        "own system prompt, under \"IN-TEXT ACTION TAGS (NOT TOOLS)\" - check it. " +
                        "Here are its instructions again:\n\n" +
                        matchingTag.GetInstructions() +
                        "\n\nWrite the tag directly in your response text now, with the actual content " +
                        "inside it - do not call it as a function.";

                    Logger.LogWarning(
                        "Model called '{ToolName}' as a tool - it is a registered tag. Restated its instructions.",
                        toolCall.Name);
                }
                else
                {
                    errorMessage = $"Error: Tool '{toolCall.Name}' not found";

                    Logger.LogError("Tool '{ToolName}' not found in registry", toolCall.Name);
                }

                conversation?.AddToolMessage(toolCall.Id, errorMessage);
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

                conversation?.AddToolMessage(toolCall.Id, result ?? string.Empty);
            }
            catch (Exception ex)
            {
                if (Logger.IsEnabled(LogLevel.Error))
                {
                    Logger.LogError(ex, "Tool {ToolName} execution failed", toolCall.Name);
                }
                conversation?.AddToolMessage(toolCall.Id, $"Error executing tool: {ex.Message}");
            }
        }
    }

    private async Task<LlmResult> InvokeLlmAsync(
        LlmRequest request,
        PipelineContext context,
        string stepName,
        CancellationToken cancellationToken)
    {
        // Scoped to this single response: ExecuteToolCallsAsync reads it right after this method
        // returns, before the next invocation clears it again.
        _tagsExecutedInLastResponse.Clear();

        var llmService = GetLlmService(context);
        var model = GetModel(context);
        var provider = GetProvider(context);

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
            result = await llmService.InvokeAsync(request, cancellationToken);

            // A batch response is the streaming case with one chunk per channel: the same scanner
            // runs over the reasoning and then the content, so tags behave identically whichever
            // transport and whichever channel carried them. Tag handling must not depend on
            // FinalizeAsync, which only RetryMiddleware calls - a step invoked directly (bypassing
            // IPipeline, as several samples do) would otherwise never get its tags processed at all.
            //
            // Only the primary candidate is scanned: it is the one the step goes on to use, and a
            // tag is an action with side effects - running the alternatives' tags too would write
            // n versions of the same file and let the last one win.
            if (result.IsSuccess && TagHandlers?.Count > 0 && result.Candidates.Count > 0)
            {
                var candidate = result.Candidates[0];
                var scanner = new StreamingTagParser(TagHandlers, Logger);

                string? purgedReasoning = null;
                if (!string.IsNullOrEmpty(candidate.Reasoning))
                {
                    purgedReasoning = await scanner.ProcessChunkAsync(candidate.Reasoning, context, cancellationToken)
                        + await scanner.FlushAsync(context, cancellationToken);
                }

                // A tag cannot span channels, hence the flush above; whatever ran while scanning
                // the reasoning is referenced from the content, which is what gets persisted - the
                // reasoning itself never is.
                var reasoningReferences = scanner.CompletedTagReferences.ToList();

                var purgedContent = string.IsNullOrEmpty(candidate.Content)
                    ? string.Empty
                    : await scanner.ProcessChunkAsync(candidate.Content, context, cancellationToken);
                purgedContent += await scanner.FlushAsync(context, cancellationToken);
                purgedContent = AppendReferences(purgedContent, reasoningReferences);

                _tagsExecutedInLastResponse.UnionWith(scanner.ExecutedTags);

                var processedCandidates = new List<LlmResponse>(result.Candidates)
                {
                    [0] = candidate with
                    {
                        Content = purgedContent,
                        Reasoning = purgedReasoning ?? candidate.Reasoning
                    }
                };

                result = LlmResult.Success(
                    processedCandidates,
                    tokensUsed: result.TokensUsed,
                    promptTokens: result.PromptTokens,
                    completionTokens: result.CompletionTokens,
                    costUsd: result.CostUsd,
                    model: result.Model,
                    nativeResponse: result.NativeResponse);
            }
        }
        else
        {
            // Accumulators per choice index to support multiple candidates (n > 1)
            Dictionary<int, StringBuilder> contentBuilders = [];
            Dictionary<int, StringBuilder> reasoningBuilders = [];
            Dictionary<int, Dictionary<int, (string? Id, string? Name, StringBuilder Args, NativeObject? Native)>> choiceToolCallBuilders = [];
            Dictionary<int, FinishReason> finishReasons = [];
            Dictionary<int, string?> rawFinishReasons = [];
            var totalTokens = 0;
            int? promptTokens = null;
            int? completionTokens = null;

            // The scanner's state machine spans chunks, and only the primary choice (index 0) gets
            // one: it is the choice the step goes on to use, and a tag is an action with side
            // effects that the discarded alternatives must not trigger. Every delta of that choice
            // goes through it in arrival order, reasoning or not - a tag is a tag wherever the model
            // wrote it. While a tag is open nothing is emitted; its placeholder is emitted where the
            // tag closed. Other choices' text passes through untouched.
            Dictionary<int, StreamingTagParser> tagScanners = [];
            Dictionary<int, bool> lastChannelWasThinking = [];
            Dictionary<int, List<string>> reasoningReferences = [];
            var handlers = TagHandlers ?? [];

            StreamingTagParser? ScannerFor(int idx)
            {
                if (idx != 0 || handlers.Count == 0)
                {
                    return null;
                }

                if (!tagScanners.TryGetValue(idx, out var scanner))
                {
                    tagScanners[idx] = scanner = new StreamingTagParser(handlers, Logger);
                }
                return scanner;
            }

            // Emits purged text and files it under the channel it came from.
            async Task EmitPurgedAsync(int idx, string text, bool isThinking)
            {
                if (string.IsNullOrEmpty(text)) return;

                (isThinking ? reasoningBuilders[idx] : contentBuilders[idx]).Append(text);

                await context.SendEventAsync(new LlmResponseEvent
                {
                    StepName = stepName,
                    Content = text,
                    FinishReason = FinishReason.Streaming,
                    IsThinking = isThinking,
                    ChoiceIndex = idx,
                    CorrelationId = context.CorrelationId
                }, cancellationToken);
            }

            await foreach (var chunk in llmService.InvokeStreamingAsync(request, cancellationToken))
            {
                var choiceIdx = chunk.ChoiceIndex;

                // Ensure builders exist for this choice
                if (!contentBuilders.ContainsKey(choiceIdx)) contentBuilders[choiceIdx] = new StringBuilder();
                if (!reasoningBuilders.ContainsKey(choiceIdx)) reasoningBuilders[choiceIdx] = new StringBuilder();
                if (!choiceToolCallBuilders.ContainsKey(choiceIdx)) choiceToolCallBuilders[choiceIdx] = [];
                if (!reasoningReferences.ContainsKey(choiceIdx)) reasoningReferences[choiceIdx] = [];

                if (!string.IsNullOrEmpty(chunk.Delta))
                {
                    var scanner = ScannerFor(choiceIdx);

                    if (scanner == null)
                    {
                        await EmitPurgedAsync(choiceIdx, chunk.Delta, chunk.IsThinking);
                    }
                    else
                    {
                        // A tag cannot span channels: when the stream switches between reasoning and
                        // content, whatever is still open was never a tag and goes out as plain text.
                        if (lastChannelWasThinking.TryGetValue(choiceIdx, out var previousWasThinking)
                            && previousWasThinking != chunk.IsThinking)
                        {
                            await EmitPurgedAsync(choiceIdx, await scanner.FlushAsync(context, cancellationToken), previousWasThinking);
                        }
                        lastChannelWasThinking[choiceIdx] = chunk.IsThinking;

                        var referencesBefore = scanner.CompletedTagReferences.Count;
                        var purged = await scanner.ProcessChunkAsync(chunk.Delta, context, cancellationToken);
                        await EmitPurgedAsync(choiceIdx, purged, chunk.IsThinking);

                        // References produced while scanning reasoning get appended to the content at
                        // the end: the reasoning is not persisted, the record of the action must be.
                        if (chunk.IsThinking)
                        {
                            for (var i = referencesBefore; i < scanner.CompletedTagReferences.Count; i++)
                            {
                                reasoningReferences[choiceIdx].Add(scanner.CompletedTagReferences[i]);
                            }
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
                            builder = (null, null, new StringBuilder(), null);
                        }

                        if (update.ToolCallId != null) builder.Id = update.ToolCallId;
                        if (update.FunctionName != null) builder.Name = update.FunctionName;
                        if (update.FunctionArgumentsUpdate != null) builder.Args.Append(update.FunctionArgumentsUpdate);
                        if (update.Native != null) builder.Native = update.Native;

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

            // End of stream: a tag still open was never a tag - its text goes out as plain text on
            // the channel it was on, and no handler runs (nothing was opened, so nothing leaks).
            foreach (var (choiceIdx, scanner) in tagScanners)
            {
                var wasThinking = lastChannelWasThinking.GetValueOrDefault(choiceIdx, false);
                await EmitPurgedAsync(choiceIdx, await scanner.FlushAsync(context, cancellationToken), wasThinking);
                _tagsExecutedInLastResponse.UnionWith(scanner.ExecutedTags);
            }

            // Finalize all candidates
            List<LlmResponse> candidates = [];
            foreach (var choiceIdx in contentBuilders.Keys.OrderBy(k => k))
            {
                var content = AppendReferences(
                    contentBuilders[choiceIdx].ToString(),
                    reasoningReferences.GetValueOrDefault(choiceIdx) ?? []);
                var reasoning = reasoningBuilders[choiceIdx].Length > 0 ? reasoningBuilders[choiceIdx].ToString() : null;

                var fReason = finishReasons.GetValueOrDefault(choiceIdx, FinishReason.Stop);
                var rawFReason = rawFinishReasons.GetValueOrDefault(choiceIdx);

                List<ToolCall> toolCalls = [];
                if (choiceToolCallBuilders.TryGetValue(choiceIdx, out var toolBuilders))
                {
                    foreach (var kvp in toolBuilders.OrderBy(k => k.Key))
                    {
                        (var id, var toolName, var args, var native) = kvp.Value;
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(toolName))
                        {
                            toolCalls.Add(new ToolCall
                            {
                                Id = id,
                                Name = toolName,
                                Arguments = args?.ToString() ?? string.Empty,
                                Native = native
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
                Model = model,
                Provider = llmService?.Name,
                CorrelationId = context.CorrelationId
            }, cancellationToken);

            result = LlmResult.Success(
                candidates,
                tokensUsed: totalTokens,
                promptTokens: promptTokens,
                completionTokens: completionTokens,
                model: model);
        }

        // Services report tokens but not cost: they never see the provider's model catalogue.
        // Filling it here covers every provider at once and keeps LlmProvider.Models - which
        // declares the per-million prices - from being configuration nobody reads.
        if (result.CostUsd is null && result.IsSuccess)
        {
            result.CostUsd = provider.CalculateCost(result.Model ?? model, result.PromptTokens, result.CompletionTokens);
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
                        Model = model,
                        Provider = llmService?.Name,
                        CorrelationId = context.CorrelationId
                    }, cancellationToken);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Appends to <paramref name="content"/> the references of tags that ran while scanning the
    /// reasoning. The reasoning itself is shown to the user but never persisted; the record of an
    /// action it carried must survive in the content, or the model cannot see in its own history
    /// that the action is done.
    /// </summary>
    /// <param name="content">Purged response content.</param>
    /// <param name="reasoningReferences">Placeholders produced by tags found in the reasoning.</param>
    private static string AppendReferences(string content, IReadOnlyList<string> reasoningReferences)
    {
        var references = string.Join("\n", reasoningReferences.Where(r => !string.IsNullOrEmpty(r)));

        if (references.Length == 0)
        {
            return content;
        }

        return string.IsNullOrEmpty(content) ? references : $"{content}\n{references}";
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

}
