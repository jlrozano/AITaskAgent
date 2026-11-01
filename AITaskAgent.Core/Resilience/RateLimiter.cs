using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITaskAgent.Resilience;

/// <summary>
/// Token bucket rate limiter for API calls.
/// S-07: Uses SemaphoreSlim signaling instead of spin-wait with Task.Delay(50ms).
/// </summary>
public sealed class RateLimiter : IDisposable
{
    private readonly int _maxTokens;
    private readonly TimeSpan _refillInterval;
    private readonly int _tokensPerRefill;
    private readonly ILogger<RateLimiter> _logger;

    private int _availableTokens;
    private DateTime _lastRefill;
    private readonly Lock _lock = new();
    private readonly Timer? _refillTimer;

    // S-07: Signal released on each refill so waiters wake up immediately instead of spinning
    private readonly SemaphoreSlim _refillSignal = new(0, int.MaxValue);

    public RateLimiter(
        int maxTokens = 10,
        TimeSpan? refillInterval = null,
        int? tokensPerRefill = null,
        ILogger<RateLimiter>? logger = null)
    {
        _maxTokens = maxTokens;
        _refillInterval = refillInterval ?? TimeSpan.FromSeconds(1);
        _tokensPerRefill = tokensPerRefill ?? maxTokens;
        _logger = logger ?? NullLogger<RateLimiter>.Instance;
        _availableTokens = maxTokens;
        _lastRefill = DateTime.UtcNow;

        _refillTimer = new Timer(RefillTimerCallback, null, _refillInterval, _refillInterval);
    }

    private void RefillTimerCallback(object? state)
    {
        lock (_lock)
        {
            DateTime now = DateTime.UtcNow;
            TimeSpan elapsed = now - _lastRefill;

            if (elapsed >= _refillInterval)
            {
                int refills = (int)(elapsed / _refillInterval);
                int tokensToAdd = refills * _tokensPerRefill;
                int oldTokens = _availableTokens;

                _availableTokens = Math.Min(_availableTokens + tokensToAdd, _maxTokens);
                _lastRefill = now;

                if (tokensToAdd > 0 && _availableTokens > oldTokens)
                {
                    _logger.LogTrace("Rate limiter refilled: {OldTokens} -> {NewTokens} (+{Added})",
                        oldTokens, _availableTokens, _availableTokens - oldTokens);

                    // S-07: Signal all waiting callers that tokens are available
                    _refillSignal.Release(_availableTokens - oldTokens);
                }
            }
        }
    }

    /// <summary>
    /// Waits until a token is available, then consumes it.
    /// </summary>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        bool waitStarted = false;

        while (true)
        {
            lock (_lock)
            {
                RefillTokens();

                if (_availableTokens > 0)
                {
                    _availableTokens--;

                    if (waitStarted)
                    {
                        _logger.LogDebug("Rate limiter token acquired after waiting, {Available} remaining",
                            _availableTokens);
                    }
                    else
                    {
                        _logger.LogTrace("Rate limiter token acquired immediately, {Available} remaining",
                            _availableTokens);
                    }

                    return;
                }
            }

            if (!waitStarted)
            {
                waitStarted = true;
                _logger.LogDebug("Rate limiter exhausted, waiting for token (max: {MaxTokens})", _maxTokens);
            }

            // S-07: Wait for refill signal instead of spinning every 50ms
            try
            {
                await _refillSignal.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }
    }

    /// <summary>
    /// Tries to acquire a token without waiting.
    /// </summary>
    public bool TryAcquire()
    {
        lock (_lock)
        {
            RefillTokens();

            if (_availableTokens > 0)
            {
                _availableTokens--;
                _logger.LogTrace("Rate limiter token acquired (TryAcquire), {Available} remaining",
                    _availableTokens);
                return true;
            }

            _logger.LogDebug("Rate limiter token rejected (TryAcquire), bucket exhausted");
            return false;
        }
    }

    private void RefillTokens()
    {
        DateTime now = DateTime.UtcNow;
        TimeSpan elapsed = now - _lastRefill;

        if (elapsed >= _refillInterval)
        {
            int refills = (int)(elapsed / _refillInterval);
            int tokensToAdd = refills * _tokensPerRefill;
            int oldTokens = _availableTokens;

            _availableTokens = Math.Min(_availableTokens + tokensToAdd, _maxTokens);
            _lastRefill = now;

            if (tokensToAdd > 0 && _availableTokens > oldTokens)
            {
                _logger.LogTrace("Rate limiter refilled: {OldTokens} -> {NewTokens} (+{Added})",
                    oldTokens, _availableTokens, _availableTokens - oldTokens);

                // S-07: Signal all waiting callers that tokens are available
                _refillSignal.Release(_availableTokens - oldTokens);
            }
        }
    }

    public int AvailableTokens
    {
        get
        {
            lock (_lock)
            {
                RefillTokens();
                return _availableTokens;
            }
        }
    }

    public void Dispose()
    {
        _refillSignal.Dispose();
        _refillTimer?.Dispose();
    }
}
