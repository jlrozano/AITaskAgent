using AITaskAgent.LLM.Models;

namespace AITaskAgent.LLM.Abstractions;

/// <summary>
/// Service for interacting with Large Language Models.
/// </summary>
public interface ILlmService
{
    /// <summary>
    /// Invokes the LLM with a request and returns the complete response.
    /// </summary>
    Task<LlmResponse> InvokeAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes the LLM with streaming response.
    /// </summary>
    IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Estimates token count for text (useful for context management).
    /// </summary>
    int EstimateTokenCount(string text);

    /// <summary>
    /// Gets the maximum context window size for the model.
    /// </summary>
    int GetMaxContextTokens(string? model = null);
}

