using Microsoft.Extensions.Logging;

namespace AITaskAgent.Resilience;

/// <summary>
/// Retry policy configuration and execution.
/// </summary>
public sealed class RetryPolicy
{
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);
    public double BackoffMultiplier { get; init; } = 2.0;
    public bool UseJitter { get; init; } = true;

    /// <summary>
    /// Predicate to determine if an exception should trigger a retry.
    /// </summary>
    public Func<Exception, bool>? ShouldRetry { get; init; }

    public static RetryPolicy Default => new();

    public static RetryPolicy Aggressive => new()
    {
        MaxAttempts = 5,
        InitialDelay = TimeSpan.FromMilliseconds(500),
        BackoffMultiplier = 1.5
    };

    public static RetryPolicy Conservative => new()
    {
        MaxAttempts = 2,
        InitialDelay = TimeSpan.FromSeconds(2),
        BackoffMultiplier = 3.0
    };

    /// <summary>
    /// Executes an action with retry logic.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        Func<int, Task<T>> action,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var attempt = 0;
        var delay = InitialDelay;
        Exception? lastException = null;

        while (attempt < MaxAttempts)
        {
            attempt++;

            try
            {
                return await action(attempt);
            }
            catch (Exception ex) when (attempt < MaxAttempts && ShouldRetryException(ex))
            {
                lastException = ex;

                logger?.LogWarning(ex,
                    "Attempt {Attempt}/{MaxAttempts} failed, retrying in {Delay}ms",
                    attempt, MaxAttempts, delay.TotalMilliseconds);

                await Task.Delay(delay, cancellationToken);

                // Calculate next delay with exponential backoff
                delay = TimeSpan.FromMilliseconds(
                    Math.Min(delay.TotalMilliseconds * BackoffMultiplier, MaxDelay.TotalMilliseconds));

                // Add jitter to prevent thundering herd
                if (UseJitter)
                {
                    var jitter = (Random.Shared.NextDouble() * 0.3) + 0.85; // 85-115%
                    delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * jitter);
                }
            }
        }

        throw new InvalidOperationException(
            $"Operation failed after {MaxAttempts} attempts",
            lastException);
    }

    private bool ShouldRetryException(Exception ex)
    {
        if (ShouldRetry != null)
        {
            return ShouldRetry(ex);
        }

        // Default: retry on transient errors
        return ex is HttpRequestException
            or TimeoutException
            or TaskCanceledException;
    }
}

