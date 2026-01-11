using Microsoft.Extensions.Caching.Memory;

namespace AITaskAgent.Support.Caching;

/// <summary>
/// In-memory cache service implementation.
/// </summary>
public sealed class MemoryCacheService(IMemoryCache memoryCache) : ICacheService
{
    private readonly IMemoryCache _cache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));

    public T? Get<T>(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _cache.TryGetValue(key, out T? value) ? value : default;
    }

    public void Set<T>(string key, T value, TimeSpan? expiration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var options = new MemoryCacheEntryOptions();
        if (expiration.HasValue)
        {
            options.AbsoluteExpirationRelativeToNow = expiration.Value;
        }

        _cache.Set(key, value, options);
    }

    public void Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _cache.Remove(key);
    }

    public void Clear()
    {
        if (_cache is MemoryCache mc)
        {
            mc.Compact(1.0); // Remove all entries
        }
    }

    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<Task<T>> factory,
        TimeSpan? expiration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);

        if (_cache.TryGetValue(key, out T? cached))
        {
            return cached!;
        }

        var value = await factory();
        Set(key, value, expiration);
        return value;
    }
}

