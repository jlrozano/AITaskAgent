using System.Text;

namespace AITaskAgent.Support.Template;

/// <summary>
/// File-based template provider with LRU + TTL + size-based caching.
/// Templates are loaded from markdown (.md) files.
/// </summary>
/// <param name="folderPath">Path to folder containing template files</param>
/// <param name="enableCache">Enable caching (disable for development/testing)</param>
/// <param name="ttl">Time-to-live for cached items (default: 5 minutes)</param>
/// <param name="maxCacheSizeBytes">Maximum cache size in bytes (default: 1MB)</param>
/// <param name="validateTemplates">Enable strict template validation (throws on missing properties without defaults)</param>
public sealed class FileTemplateProvider(
    string folderPath,
    bool enableCache = true,
    TimeSpan? ttl = null,
    long? maxCacheSizeBytes = null,
    bool validateTemplates = false) : ITemplateProvider
{
    private const long DefaultMaxCacheSizeBytes = 1_048_576; // 1MB
    private const double CacheSizeMargin = 1.1; // 10% margin before eviction
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, CachedTemplate> _cache = [];
    private readonly TimeSpan _ttl = ttl ?? DefaultTtl;
    private readonly long _maxCacheSizeBytes = maxCacheSizeBytes ?? DefaultMaxCacheSizeBytes;
    private long _currentCacheSizeBytes;

    /// <summary>
    /// Gets a template by name. Returns null if not found.
    /// </summary>
    /// <param name="name">Template name (without .md extension)</param>
    public string? GetTemplate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!enableCache)
        {
            return LoadTemplateFromFile(name);
        }

        lock (_cacheLock)
        {
            // Check cache
            if (_cache.TryGetValue(name, out var cached))
            {
                // Check TTL
                if (DateTime.UtcNow - cached.LoadedAt <= _ttl)
                {
                    // Update last accessed time (LRU)
                    cached.LastAccessedAt = DateTime.UtcNow;
                    return cached.Content;
                }

                // Expired, remove from cache
                RemoveFromCache(name);
            }

            // Load from file
            var content = LoadTemplateFromFile(name);
            if (content is null)
                return null;

            // Add to cache
            AddToCache(name, content);
            return content;
        }
    }

    private string? LoadTemplateFromFile(string name)
    {
        var filePath = Path.Combine(folderPath, $"{name}.md");
        // throw if not exists
        return File.ReadAllText(filePath, Encoding.UTF8);

    }

    private void AddToCache(string name, string content)
    {
        var sizeBytes = Encoding.UTF8.GetByteCount(content);
        var now = DateTime.UtcNow;

        var cached = new CachedTemplate(content, now, now, sizeBytes);
        _cache[name] = cached;
        _currentCacheSizeBytes += sizeBytes;

        // Evict if over limit + margin
        var sizeLimit = (long)(_maxCacheSizeBytes * CacheSizeMargin);
        if (_currentCacheSizeBytes > sizeLimit)
        {
            EvictLruItems();
        }
    }

    private void RemoveFromCache(string name)
    {
        if (_cache.Remove(name, out var cached))
        {
            _currentCacheSizeBytes -= cached.SizeBytes;
        }
    }

    private void EvictLruItems()
    {
        // Sort by last accessed time (oldest first)
        var itemsToEvict = _cache
            .OrderBy(kvp => kvp.Value.LastAccessedAt)
            .ToList();

        // Evict until under limit
        foreach (var (name, _) in itemsToEvict)
        {
            if (_currentCacheSizeBytes <= _maxCacheSizeBytes)
                break;

            RemoveFromCache(name);
        }
    }

    public string? Render(string name, object model)
    {
        var template = GetTemplate(name);
        return JsonTemplateEngine.Render(template ?? string.Empty, model, strictMode: validateTemplates);
    }

    private sealed record CachedTemplate(
        string Content,
        DateTime LoadedAt,
        DateTime LastAccessedAt,
        int SizeBytes)
    {
        public DateTime LastAccessedAt { get; set; } = LastAccessedAt;
    }
}
