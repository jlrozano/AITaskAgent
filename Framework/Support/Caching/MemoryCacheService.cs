using Microsoft.Extensions.Caching.Memory;

namespace AITaskAgent.Support.Caching;

/// <summary>
/// In-memory cache service implementation, independent of DI.
/// </summary>
public sealed class MemoryCacheService : ICacheService
{
    private readonly IMemoryCache _cache;

    /// <summary>
    /// Creates a new MemoryCacheService with its own internal MemoryCache instance.
    /// </summary>
    public MemoryCacheService()
    {
        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    public T? Get<T>(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _cache.TryGetValue(key, out T? value) ? value : default;
    }

    public void Set<T>(string key, T value, TimeSpan? expiration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        MemoryCacheEntryOptions options = new();
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

        T? value = await factory();
        Set(key, value, expiration);
        return value;
    }
}

