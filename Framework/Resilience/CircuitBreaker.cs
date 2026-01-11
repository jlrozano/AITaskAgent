using Microsoft.Extensions.Logging;

namespace AITaskAgent.Resilience;

/// <summary>
/// Circuit breaker to prevent cascading failures.
/// </summary>
public sealed class CircuitBreaker(
    int failureThreshold = 5,
    TimeSpan? openDuration = null,
    ILogger? logger = null)
{
    private readonly int _failureThreshold = failureThreshold;
    private readonly TimeSpan _openDuration = openDuration ?? TimeSpan.FromMinutes(1);
    private readonly ILogger? _logger = logger;

    private int _consecutiveFailures;
    private DateTime? _openedAt;
    private CircuitState _state = CircuitState.Closed;
    private readonly Lock _lock = new();

    public CircuitState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Executes an action through the circuit breaker.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(Func<Task<T>> action)
    {
        EnsureCircuitClosed();

        try
        {
            var result = await action();
            OnSuccess();
            return result;
        }
        catch (Exception ex)
        {
            OnFailure(ex);
            throw;
        }
    }

    private void EnsureCircuitClosed()
    {
        lock (_lock)
        {
            if (_state == CircuitState.Open)
            {
                if (DateTime.UtcNow - _openedAt >= _openDuration)
                {
                    _logger?.LogInformation("Circuit breaker entering half-open state");
                    _state = CircuitState.HalfOpen;
                }
                else
                {
                    throw new CircuitBreakerOpenException(
                        $"Circuit breaker is open. Opened at {_openedAt}");
                }
            }
        }
    }

    private void OnSuccess()
    {
        lock (_lock)
        {
            if (_state == CircuitState.HalfOpen)
            {
                _logger?.LogInformation("Circuit breaker closing after successful attempt");
                _state = CircuitState.Closed;
            }

            _consecutiveFailures = 0;
        }
    }

    private void OnFailure(Exception ex)
    {
        lock (_lock)
        {
            _consecutiveFailures++;

            if (_consecutiveFailures >= _failureThreshold)
            {
                _state = CircuitState.Open;
                _openedAt = DateTime.UtcNow;

                _logger?.LogWarning(ex,
                    "Circuit breaker opened after {Failures} consecutive failures",
                    _consecutiveFailures);
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _state = CircuitState.Closed;
            _consecutiveFailures = 0;
            _openedAt = null;
            _logger?.LogInformation("Circuit breaker manually reset");
        }
    }
}

