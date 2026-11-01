namespace AITaskAgent.Configuration;

/// <summary>
/// Rate limiting configuration options.
/// </summary>
public sealed class RateLimitOptions
{
    /// <summary>Maximum tokens in the bucket.</summary>
    public int MaxTokens { get; init; } = 100;

    /// <summary>Refill interval in milliseconds.</summary>
    public int RefillIntervalMs { get; init; } = 1000;

    /// <summary>Tokens added per refill interval.</summary>
    public int TokensPerRefill { get; init; } = 10;
}
