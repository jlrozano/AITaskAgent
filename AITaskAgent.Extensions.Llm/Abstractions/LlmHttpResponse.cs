using System.Net;

namespace AITaskAgent.LLM.Abstractions;

/// <summary>
/// Response wrapper that includes HTTP metadata for retry logic.
/// </summary>
public sealed record LlmHttpResponse<TResponse>
{
    public required TResponse Data { get; init; }
    public required HttpStatusCode StatusCode { get; init; }
    public Dictionary<string, string> Headers { get; init; } = [];
    public bool IsSuccess { get => (int)StatusCode is >= 200 and < 300; }
}
