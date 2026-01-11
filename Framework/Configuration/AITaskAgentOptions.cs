namespace AITaskAgent.Configuration;

/// <summary>
/// Configuration options for AITaskAgent framework.
/// Centralizes all configurable settings.
/// </summary>
public sealed class AITaskAgentOptions
{
    /// <summary>Observability configuration.</summary>
    public ObservabilityOptions Observability { get; init; } = new();

    /// <summary>Timeout configuration.</summary>
    public TimeoutOptions Timeouts { get; init; } = new();

    /// <summary>Circuit breaker configuration.</summary>
    public CircuitBreakerOptions CircuitBreaker { get; init; } = new();

    /// <summary>Rate limiting configuration.</summary>
    public RateLimitOptions RateLimit { get; init; } = new();

    /// <summary>Conversation management configuration.</summary>
    public ConversationOptions Conversation { get; init; } = new();
}
