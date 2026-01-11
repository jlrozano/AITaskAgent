namespace AITaskAgent.Support.Caching;

/// <summary>
/// Service for caching data.
/// </summary>
public interface ICacheService
{
    /// <summary>
    /// Gets a cached value.
    /// </summary>
    T? Get<T>(string key);

    /// <summary>
    /// Sets a cached value with optional expiration.
    /// </summary>
    void Set<T>(string key, T value, TimeSpan? expiration = null);

    /// <summary>
    /// Removes a cached value.
    /// </summary>
    void Remove(string key);

    /// <summary>
    /// Clears all cached values.
    /// </summary>
    void Clear();

    /// <summary>
    /// Gets or creates a cached value.
    /// </summary>
    Task<T> GetOrCreateAsync<T>(
        string key,
        Func<Task<T>> factory,
        TimeSpan? expiration = null);
}

