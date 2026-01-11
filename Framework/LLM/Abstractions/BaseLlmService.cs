using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using System.Net;

namespace AITaskAgent.LLM.Abstractions;

/// <summary>
/// Base class for LLM service implementations.
/// Handles common concerns: message conversion, HTTP retries, and error handling.
/// </summary>
/// <typeparam name="TMessage">Provider-specific message type (e.g., ChatMessage for OpenAI)</typeparam>
/// <typeparam name="TToolCall">Provider-specific tool call type (e.g., ChatToolCall for OpenAI)</typeparam>
public abstract class BaseLlmService<TMessage, TToolCall>(
    int maxRetries = 3,
    TimeSpan? initialRetryDelay = null) : ILlmService
{
    private readonly int _maxRetries = maxRetries;
    private readonly TimeSpan _initialRetryDelay = initialRetryDelay ?? TimeSpan.FromSeconds(1);

    #region Abstract Methods - Provider Implementation

    /// <summary>
    /// Converts framework Message to provider-specific message type.
    /// </summary>
    protected abstract TMessage ConvertMessage(Message message);

    /// <summary>
    /// Converts framework ToolCall to provider-specific tool call type.
    /// </summary>
    protected abstract TToolCall ConvertToolCall(ToolCall toolCall);

    /// <summary>
    /// Converts framework ToolDefinition to provider-specific tool definition.
    /// </summary>
    protected abstract object ConvertToolDefinition(ToolDefinition toolDefinition);

    /// <summary>
    /// Invokes the provider's LLM API with retry logic.
    /// Returns HTTP response wrapper for retry decision making.
    /// </summary>
    protected abstract Task<LlmHttpResponse<LlmResponse>> InvokeProviderAsync(
        IEnumerable<TMessage> messages,
        LlmRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Invokes the provider's streaming LLM API.
    /// </summary>
    protected abstract IAsyncEnumerable<LlmStreamChunk> InvokeProviderStreamingAsync(
        IEnumerable<TMessage> messages,
        LlmRequest request,
        CancellationToken cancellationToken);

    #endregion

    #region Public ILlmService Implementation

    public async Task<LlmResponse> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var providerMessages = ConvertConversation(request.Conversation);

        var attempt = 0;
        var delay = _initialRetryDelay;
        Exception? lastException = null;

        while (true)
        {
            attempt++;

            try
            {
                var response = await InvokeProviderAsync(
                    providerMessages,
                    request,
                    cancellationToken);

                if (response.IsSuccess)
                {
                    return response.Data;
                }

                // Check if we should retry based on status code
                if (!ShouldRetry(response.StatusCode, attempt))
                {
                    throw new HttpRequestException(
                        $"LLM request failed with status {response.StatusCode} after {attempt} attempts");
                }

                // Calculate retry delay from headers or use exponential backoff
                delay = CalculateRetryDelay(response.Headers, delay, attempt);
            }
            catch (Exception ex) when (attempt < _maxRetries && IsRetriableException(ex))
            {
                // Network errors, timeouts, etc.
                lastException = ex;
                delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2); // Exponential backoff
            }

            if (attempt >= _maxRetries)
            {
                throw lastException ?? new InvalidOperationException("LLM request failed after maximum retries");
            }

            await Task.Delay(delay, cancellationToken);
        }
    }

    public async IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(
        LlmRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var providerMessages = ConvertConversation(request.Conversation);

        // Create a resettable timeout for streaming activity
        // Timeout resets on each chunk received to handle long-running streams
        using var streamingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var activityTimeout = TimeSpan.FromSeconds(30); // Reset timeout on each chunk
        streamingCts.CancelAfter(activityTimeout);

        await foreach (var chunk in InvokeProviderStreamingAsync(providerMessages, request, streamingCts.Token))
        {
            // Reset timeout on each chunk - stream is still active
            streamingCts.CancelAfter(activityTimeout);
            yield return chunk;
        }
    }

    public abstract int EstimateTokenCount(string text);
    public abstract int GetMaxContextTokens(string? model = null);

    #endregion

    #region Protected Helper Methods

    /// <summary>
    /// Converts the framework conversation to provider-specific message list.
    /// </summary>
    protected IEnumerable<TMessage> ConvertConversation(ConversationContext? conversation)
    {
        if (conversation == null)
        {
            return [];
        }

        var messages = conversation.GetMessagesForRequest();
        return messages.Select(ConvertMessage);
    }

    /// <summary>
    /// Determines if an HTTP status code warrants a retry.
    /// </summary>
    protected virtual bool ShouldRetry(HttpStatusCode statusCode, int attemptNumber)
    {
        return attemptNumber < _maxRetries && statusCode switch
        {
            HttpStatusCode.TooManyRequests => true,        // 429
            HttpStatusCode.ServiceUnavailable => true,     // 503
            HttpStatusCode.GatewayTimeout => true,         // 504
            HttpStatusCode.RequestTimeout => true,         // 408
            _ when (int)statusCode >= 500 => true,         // Other 5xx errors
            _ => false
        };
    }

    /// <summary>
    /// Determines if an exception is retriable (network issues, timeouts).
    /// </summary>
    protected virtual bool IsRetriableException(Exception ex)
    {
        return ex is HttpRequestException
            or TaskCanceledException
            or TimeoutException;
    }

    /// <summary>
    /// Calculates retry delay from response headers (Retry-After) or exponential backoff.
    /// </summary>
    protected virtual TimeSpan CalculateRetryDelay(
        Dictionary<string, string> headers,
        TimeSpan currentDelay,
        int attemptNumber)
    {
        // Check for Retry-After header
        if (headers.TryGetValue("Retry-After", out var retryAfter))
        {
            // Retry-After can be seconds or HTTP date
            if (int.TryParse(retryAfter, out var seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }

            if (DateTimeOffset.TryParse(retryAfter, out var retryDate))
            {
                var delay = retryDate - DateTimeOffset.UtcNow;
                return delay > TimeSpan.Zero ? delay : _initialRetryDelay;
            }
        }

        // Check for X-RateLimit-Reset (Unix timestamp)
        if (headers.TryGetValue("X-RateLimit-Reset", out var resetTime))
        {
            if (long.TryParse(resetTime, out var unixTimestamp))
            {
                var resetDate = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp);
                var delay = resetDate - DateTimeOffset.UtcNow;
                return delay > TimeSpan.Zero ? delay : _initialRetryDelay;
            }
        }

        // Exponential backoff with jitter
        var jitter = Random.Shared.NextDouble() * 0.3; // 0-30% jitter
        return TimeSpan.FromMilliseconds(
            currentDelay.TotalMilliseconds * 2 * (1 + jitter));
    }

    #endregion
}
