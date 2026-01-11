namespace AITaskAgent.Resilience;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Token bucket rate limiter for API calls.
/// </summary>
public sealed class RateLimiter(
    int maxTokens = 10,
    TimeSpan? refillInterval = null,
    int? tokensPerRefill = null,
    ILogger<RateLimiter>? logger = null)
{
    private readonly int _maxTokens = maxTokens;
    private readonly TimeSpan _refillInterval = refillInterval ?? TimeSpan.FromSeconds(1);
    private readonly int _tokensPerRefill = tokensPerRefill ?? maxTokens;
    private readonly ILogger<RateLimiter> _logger = logger ?? NullLogger<RateLimiter>.Instance;

    private int _availableTokens = maxTokens;
    private DateTime _lastRefill = DateTime.UtcNow;
    private readonly Lock _lock = new();

    /// <summary>
    /// Waits until a token is available, then consumes it.
    /// </summary>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        var waitStarted = false;

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

            // Wait a bit before checking again
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
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
        var now = DateTime.UtcNow;
        var elapsed = now - _lastRefill;

        if (elapsed >= _refillInterval)
        {
            var refills = (int)(elapsed / _refillInterval);
            var tokensToAdd = refills * _tokensPerRefill;
            var oldTokens = _availableTokens;

            _availableTokens = Math.Min(_availableTokens + tokensToAdd, _maxTokens);
            _lastRefill = now;

            if (tokensToAdd > 0 && _availableTokens > oldTokens)
            {
                _logger.LogTrace("Rate limiter refilled: {OldTokens} -> {NewTokens} (+{Added})",
                    oldTokens, _availableTokens, _availableTokens - oldTokens);
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
}
