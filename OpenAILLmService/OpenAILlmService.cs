using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Support;
using AITaskAgent.Resilience;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace OpenAILLmService;

/// <summary>
/// OpenAI implementation of ILlmService using the official OpenAI library.
/// </summary>
public sealed class OpenAILlmService : ILlmService
{
    private readonly ConcurrentDictionary<string, OpenAIClient> _clients = new();
    private readonly ILogger<OpenAILlmService> _logger;
    private readonly RateLimiter? _rateLimiter;
    private readonly RetryPolicy _retryPolicy;

    /// <summary>
    /// HTTP status codes that indicate transient errors and should be retried.
    /// </summary>
    private static readonly HashSet<int> TransientHttpStatusCodes = [429, 500, 502, 503, 504];

    /// <summary>
    /// Creates a new OpenAI LLM service instance.
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="rateLimiter">Optional rate limiter for request throttling</param>
    /// <param name="retryPolicy">Optional retry policy for transient errors (defaults to 3 attempts with exponential backoff)</param>
    public OpenAILlmService(
        ILogger<OpenAILlmService> logger,
        RateLimiter? rateLimiter = null,
        RetryPolicy? retryPolicy = null)
    {
        _logger = logger;
        _rateLimiter = rateLimiter;
        _retryPolicy = retryPolicy ?? new RetryPolicy
        {
            MaxAttempts = 3,
            InitialDelay = TimeSpan.FromSeconds(2),
            MaxDelay = TimeSpan.FromSeconds(60),
            BackoffMultiplier = 2.0,
            ShouldRetry = IsTransientError
        };

        _logger.LogInformation("OpenAILlmService initialized. LLM configuration comes from request profiles.");
    }

    private (OpenAIClient Client, string Model) GetClientAndModelForRequest(LlmRequest request)
    {
        var profile = request.Profile;

        // Use BaseUrl + Model as unique cache key for the client
        var cacheKey = $"{profile.BaseUrl}|{profile.Model}";

        _logger.LogDebug("Getting client for model: {Model}, provider: {Provider}, baseUrl: {BaseUrl}",
            profile.Model, profile.Provider, profile.BaseUrl);

        // Try to get cached client
        if (_clients.TryGetValue(cacheKey, out var cachedClient))
        {
            _logger.LogDebug("Using cached client for {Model}", profile.Model);
            return (cachedClient, profile.Model);
        }

        // Create new client
        _logger.LogInformation("Creating new OpenAI client for model: {Model}, provider: {Provider}, baseUrl: {BaseUrl}",
            profile.Model, profile.Provider, profile.BaseUrl);

        var clientOptions = new OpenAIClientOptions()
        {
            Endpoint = new Uri(profile.BaseUrl),
            NetworkTimeout = TimeSpan.FromMinutes(30)
        };

        var newClient = new OpenAIClient(new ApiKeyCredential(profile.ApiKey), clientOptions);

        // Cache it
        _clients.TryAdd(cacheKey, newClient);

        return (newClient, profile.Model);
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public async Task<LlmResponse> InvokeAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_rateLimiter != null)
        {
            await _rateLimiter.WaitAsync(cancellationToken);
        }

        // Get client and model from profile
        (var client, var model) = GetClientAndModelForRequest(request);

        _logger.LogDebug("Invoking OpenAI model {Model} (provider: {Provider}) with conversation of {MessageCount} total messages",
            model, request.Profile.Provider, request.Conversation.History.Messages.Count);

        var chatMessages = BuildChatMessages(request);
        var chatOptions = BuildChatOptions(request);

        var chatClient = client.GetChatClient(model);

        // Execute with retry for transient errors
        return await _retryPolicy.ExecuteAsync(async attempt =>
        {
            try
            {
                ChatCompletion response;
                try
                {
                    var completion = await chatClient.CompleteChatAsync(chatMessages, chatOptions, cancellationToken);
                    response = completion.Value;
                }
                catch (ArgumentOutOfRangeException ex) when (ex.Message.Contains("ChatFinishReason"))
                {
                    // OpenRouter and other providers may return non-standard finish_reason values
                    // that the OpenAI SDK doesn't recognize (e.g., "end_turn").
                    // Extract the actual finish_reason value for logging
                    var unknownReason = ex.ParamName ?? "UNKNOWN";

                    _logger.LogError(
                        "Provider returned unsupported finish_reason: '{UnknownReason}'. " +
                        "This model may not be fully compatible with OpenAI SDK. " +
                        "Exception: {ExceptionMessage}",
                        unknownReason, ex.Message);

                    // Since we can't get the actual response due to deserialization failure,
                    // we need to make another call or handle this differently.
                    // For now, throw a more informative error.
                    throw new InvalidOperationException(
                        $"Provider returned unsupported finish_reason: '{unknownReason}'. " +
                        $"This model may not be fully compatible with OpenAI SDK. " +
                        $"Consider using a different model or provider. Error: {ex.Message}", ex);
                }

                var usage = response.Usage;

                // Extract content from ContentUpdate
                var content = string.Empty;
                if (response.Content != null && response.Content.Count > 0)
                {
                    var textContent = response.Content.FirstOrDefault(c => c.Kind == ChatMessageContentPartKind.Text);
                    if (textContent != null)
                    {
                        content = textContent.Text ?? string.Empty;
                    }
                }

                var (finishReason, rawFinishReason) = FinishReasonExtensions.Parse(response.FinishReason.ToString());

                // Log raw response for debugging (similar to GeminiLlmService)
                _logger.LogDebug(
                    "OpenAI raw response - Model: {Model}, FinishReason: {FinishReason}, ToolCalls: {ToolCallCount}",
                    response.Model,
                    response.FinishReason,
                    response.ToolCalls?.Count ?? 0);

                var llmResponse = new LlmResponse
                {
                    Content = content,
                    TokensUsed = usage.TotalTokenCount,
                    PromptTokens = usage.InputTokenCount,
                    CompletionTokens = usage.OutputTokenCount,
                    CostUsd = CalculateCost(request.Profile, usage.InputTokenCount, usage.OutputTokenCount),
                    FinishReason = finishReason,
                    RawFinishReason = rawFinishReason,
                    Model = response.Model,
                    ToolCalls = response.ToolCalls?.Select(tc => tc.ToFrameworkToolCall()).ToList(),
                    NativeResponse = response
                };

                _logger.LogInformation(
                    "OpenAI response: {Tokens} tokens, ${Cost:F4}, finish: {Reason}",
                    llmResponse.TokensUsed, llmResponse.CostUsd, llmResponse.FinishReason);

                return llmResponse;
            }
            catch (ClientResultException ex)
            {
                _logger.LogError(ex, "OpenAI API error: {Status} - {Message}", ex.Status, ex.Message);
                throw new InvalidOperationException($"Failed to call OpenAI API: {ex.Message}", ex);
            }
        }, _logger, cancellationToken);
    }

    /// <summary>
    /// Determines if an exception represents a transient error that should be retried.
    /// </summary>
    private static bool IsTransientError(Exception ex)
    {
        // Check for ClientResultException with transient HTTP status codes
        if (ex is InvalidOperationException ioe && ioe.InnerException is ClientResultException cre)
        {
            return TransientHttpStatusCodes.Contains(cre.Status);
        }

        // Also retry on network-related exceptions
        return ex is HttpRequestException or TimeoutException;
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_rateLimiter != null)
        {
            await _rateLimiter.WaitAsync(cancellationToken);
        }

        // Get client and model from profile
        (var client, var model) = GetClientAndModelForRequest(request);

        var chatMessages = BuildChatMessages(request);
        var chatOptions = BuildChatOptions(request);

        var chatClient = client.GetChatClient(model);

        // Retry logic for stream initialization - get enumerator and first chunk with retry
        var (enumerator, firstUpdate) = await _retryPolicy.ExecuteAsync(async _ =>
        {
            try
            {
                var streamingResponse = chatClient.CompleteChatStreamingAsync(chatMessages, chatOptions, cancellationToken);
                var enumeratorResult = EnumerateWithExceptionHandling(streamingResponse, cancellationToken).GetAsyncEnumerator(cancellationToken);

                // Try to get first element to verify connection works
                if (await enumeratorResult.MoveNextAsync())
                {
                    return (enumeratorResult, enumeratorResult.Current);
                }

                // Empty stream - return enumerator with null first
                return (enumeratorResult, (StreamingChatCompletionUpdate?)null);
            }
            catch (ClientResultException ex)
            {
                _logger.LogError(ex, "OpenAI Streaming API error: {Status} - {Message}", ex.Status, ex.Message);
                throw new InvalidOperationException($"Failed to start OpenAI stream: {ex.Message}", ex);
            }
        }, _logger, cancellationToken);

        await using (enumerator)
        {
            var hasYieldedContent = false;

            // Process first update if exists
            if (firstUpdate != null)
            {
                var chunk = ProcessStreamUpdate(firstUpdate, ref hasYieldedContent);
                if (chunk != null)
                {
                    yield return chunk;
                }
            }
            else
            {
                // Empty stream - enumerator already at end, don't call MoveNextAsync
                yield break;
            }

            // Continue with remaining updates (no retry - stream already established)
            while (await enumerator.MoveNextAsync())
            {
                var update = enumerator.Current;

                if (update == null)
                {
                    // Exception occurred, yield final chunk
                    if (hasYieldedContent)
                    {
                        yield return new LlmStreamChunk
                        {
                            Delta = string.Empty,
                            IsComplete = true,
                            FinishReason = FinishReason.Stop
                        };
                    }
                    yield break;
                }

                var chunk = ProcessStreamUpdate(update, ref hasYieldedContent);
                if (chunk != null)
                {
                    yield return chunk;
                }
            }
        }
    }

    /// <summary>
    /// Processes a streaming update and returns a chunk to yield.
    /// </summary>
    private LlmStreamChunk? ProcessStreamUpdate(StreamingChatCompletionUpdate update, ref bool hasYieldedContent)
    {
        var contentUpdate = update.ContentUpdate.FirstOrDefault();

        // Safely get finish reason, handling unknown values from non-standard providers
        FinishReason? finishReason = null;
        string? rawFinishReason = null;
        var isComplete = false;

        if (update.FinishReason.HasValue)
        {
            try
            {
                (finishReason, rawFinishReason) = FinishReasonExtensions.Parse(update.FinishReason.Value.ToString());
                isComplete = true;
            }
            catch (ArgumentOutOfRangeException ex) when (ex.Message.Contains("ChatFinishReason"))
            {
                // Handle unknown finish reasons from non-standard providers
                var unknownReason = ex.ParamName ?? update.FinishReason?.ToString() ?? "UNKNOWN";

                _logger.LogWarning(
                    "Unknown finish_reason in stream: '{UnknownReason}', treating as 'stop'. Exception: {ExceptionMessage}",
                    unknownReason, ex.Message);

                finishReason = FinishReason.Other;
                rawFinishReason = unknownReason;
                isComplete = true;
            }
        }

        hasYieldedContent = true;

        return new LlmStreamChunk
        {
            Delta = contentUpdate?.Text ?? string.Empty,
            IsComplete = isComplete,
            FinishReason = finishReason,
            RawFinishReason = rawFinishReason,
            ToolCallUpdates = update.ToolCallUpdates?.Select(u => u.ToFrameworkToolCallUpdate()).ToList(),
            NativeChunk = update
        };
    }

    private async IAsyncEnumerable<StreamingChatCompletionUpdate?> EnumerateWithExceptionHandling(
        AsyncCollectionResult<StreamingChatCompletionUpdate> streamingResponse,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var enumerator = streamingResponse.GetAsyncEnumerator(cancellationToken);
        var hasMore = true;
        var exceptionOccurred = false;

        while (hasMore && !exceptionOccurred)
        {
            StreamingChatCompletionUpdate? currentUpdate = null;
            var shouldYield = false;

            try
            {
                hasMore = await enumerator.MoveNextAsync();

                if (hasMore)
                {
                    currentUpdate = enumerator.Current;
                    shouldYield = true;
                }
            }
            catch (ArgumentOutOfRangeException ex) when (ex.Message.Contains("ChatFinishReason"))
            {
                // Deserialization failed due to unknown finish reason from non-standard provider
                var unknownReason = ex.ParamName ?? "UNKNOWN";

                _logger.LogWarning(
                    "Stream deserialization failed due to unknown finish_reason: '{UnknownReason}', ending stream gracefully. Exception: {ExceptionMessage}",
                    unknownReason, ex.Message);

                exceptionOccurred = true;
            }

            if (shouldYield && currentUpdate != null)
            {
                yield return currentUpdate;
            }
        }

        // If exception occurred, signal end of stream
        if (exceptionOccurred)
        {
            yield return null;
        }
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    public int EstimateTokenCount(string text)
    {
        // Rough estimation: ~4 characters per token for English
        // More accurate would use tiktoken library
        return (int)Math.Ceiling(text.Length / 4.0);
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="model"></param>
    /// <returns></returns>
    public int GetMaxContextTokens(string? model = null)
    {
        // Return conservative default - actual limits should be configured per profile
        return 8192;
    }

    private List<ChatMessage> BuildChatMessages(LlmRequest request)
    {
        List<ChatMessage> messages = [];

        // Add system prompt if provided
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(ChatMessage.CreateSystemMessage(request.SystemPrompt));
        }

        // Get conversation messages using the built-in context management
        // This applies sliding window or recent messages strategy based on request settings
        var conversationMessages = request.Conversation.GetMessagesForRequest(
            maxTokens: request.SlidingWindowMaxTokens,
            useSlidingWindow: request.UseSlidingWindow);

        messages.AddRange(conversationMessages.Select(m => m.ToChatMessage()));

        _logger.LogDebug(
            "Built {TotalCount} messages for LLM: system={HasSystem}, conversation={ConvCount} (sliding window: {UseSW}, max tokens: {MaxTokens})",
            messages.Count,
            !string.IsNullOrWhiteSpace(request.SystemPrompt),
            conversationMessages.Count,
            request.UseSlidingWindow,
            request.SlidingWindowMaxTokens);

        return messages;
    }

    private static ChatCompletionOptions BuildChatOptions(LlmRequest request)
    {
        var options = new ChatCompletionOptions();

        if (request.Temperature.HasValue)
        {
            options.Temperature = (float)request.Temperature.Value;
        }

        if (request.MaxTokens.HasValue)
        {
            options.MaxOutputTokenCount = request.MaxTokens.Value;
        }

        if (request.TopP.HasValue)
        {
            options.TopP = (float)request.TopP.Value;
        }

        if (request.FrequencyPenalty.HasValue)
        {
            options.FrequencyPenalty = (float)request.FrequencyPenalty.Value;
        }

        if (request.PresencePenalty.HasValue)
        {
            options.PresencePenalty = (float)request.PresencePenalty.Value;
        }

        if (request.User != null)
        {
            options.EndUserId = request.User;
        }

        if (request.Stop != null && request.Stop.Length > 0)
        {
            foreach (var stopSeq in request.Stop)
            {
                options.StopSequences.Add(stopSeq);
            }
        }

        // Response format handling
        if (request.ResponseFormat != null && request.ResponseFormat.Type == ResponseFormatType.JsonObject)
        {
            options.ResponseFormat = request.ResponseFormat.JsonSchema == null
                ? ChatResponseFormat.CreateJsonObjectFormat()
                : ChatResponseFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: "custom_schema",
                    jsonSchema: BinaryData.FromString(request.ResponseFormat.JsonSchema.ToJson()));
            // else: default "text" format, no need to set anything
        }

        if (request.Tools != null && request.Tools.Count > 0)
        {
            foreach (var tool in request.Tools)
            {
                options.Tools.Add(tool.ToChatTool());
            }
        }

        // NOTE: TopK is not directly supported by OpenAI ChatCompletionOptions
        // It's used by models like Gemini. If needed, it can be passed via
        // request.AdditionalParameters for provider-specific handling.

        return options;
    }

    private static decimal CalculateCost(LlmProviderConfig profile, int promptTokens, int completionTokens)
    {
        // Use profile-configured pricing if available
        if (profile.InputTokenPricePerMillion.HasValue && profile.OutputTokenPricePerMillion.HasValue)
        {
            var promptCost = promptTokens / 1_000_000m * profile.InputTokenPricePerMillion.Value;
            var completionCost = completionTokens / 1_000_000m * profile.OutputTokenPricePerMillion.Value;
            return promptCost + completionCost;
        }

        // No pricing configured - return 0
        return 0;
    }
}
