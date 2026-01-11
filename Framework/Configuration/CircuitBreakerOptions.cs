namespace AITaskAgent.Configuration;

/// <summary>
/// Circuit breaker configuration options.
/// </summary>
public sealed class CircuitBreakerOptions
{
    /// <summary>Number of consecutive failures before opening circuit.</summary>
    public int FailureThreshold { get; init; } = 5;

    /// <summary>Duration in seconds the circuit remains open.</summary>
    public int OpenDurationSeconds { get; init; } = 60;
}
