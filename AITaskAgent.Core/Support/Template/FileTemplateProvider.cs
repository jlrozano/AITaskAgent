using System.Text;

namespace AITaskAgent.Support.Template;

/// <summary>
/// File-based template provider with LRU + TTL + size-based caching.
/// Templates are loaded from markdown (.md) files.
/// </summary>
/// <param name="folderPath">Path to folder containing template files</param>
/// <param name="templateEngine">Optional template engine for rendering (default: JsonTemplateEngine)</param>
/// <param name="enableCache">Enable caching (disable for development/testing)</param>
/// <param name="ttl">Time-to-live for cached items (default: 5 minutes)</param>
/// <param name="maxCacheSizeBytes">Maximum cache size in bytes (default: 1MB)</param>
/// <param name="validateTemplates">Enable strict template validation (throws on not template exists)</param>
public sealed class FileTemplateProvider(
    string folderPath,
    // Optional: the provider already falls back to TinyTemplateRendered when none is supplied, so
    // requiring it positionally only broke every existing call site for no gain.
    ITemplateRendered? templateEngine = null,
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
    private readonly ITemplateRendered? _templateEngine = templateEngine;

    /// <summary>
    /// Gets a template by name. Returns null if not found.
    /// </summary>
    /// <param name="name">Template name, with or without the .md extension ("x" and "x.md" both load x.md)</param>
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
            {
                return null;
            }

            // Add to cache
            AddToCache(name, content);
            return content;
        }
    }

    private string? LoadTemplateFromFile(string name)
    {
        // Tolerate names that already carry the extension: TemplateLlmStep's defaults are
        // "{name}_prompt.md" / "{name}_system.md", and appending ".md" again looked for "X.md.md".
        var fileName = name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.md";

        string targetPath;
        try
        {
            targetPath = Path.GetFullPath(Path.Combine(folderPath, fileName));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Not a usable file name (callers may probe with inline prompt text): a miss, not a crash.
            return null;
        }

        var safeBasePath = Path.GetFullPath(folderPath) + Path.DirectorySeparatorChar;

        // Null on a miss (missing file or a path-traversal attempt) is the contract GetTemplate
        // and ITemplateProvider both document - returning the raw name here made a missing
        // template silently render as its own filename instead of failing loud.
        return targetPath.StartsWith(safeBasePath, StringComparison.OrdinalIgnoreCase) && File.Exists(targetPath)
            ? File.ReadAllText(targetPath, Encoding.UTF8)
            : null;
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
        foreach ((var name, var _) in itemsToEvict)
        {
            if (_currentCacheSizeBytes <= _maxCacheSizeBytes)
            {
                break;
            }

            RemoveFromCache(name);
        }
    }

    /// <summary>
    /// Renders template <paramref name="name"/> against <paramref name="model"/>. Returns null
    /// (matching <see cref="GetTemplate"/>'s contract) when the template file is missing, unless
    /// <c>validateTemplates</c> is set, in which case a miss throws
    /// <see cref="FileNotFoundException"/> instead of silently propagating null downstream. Either
    /// way, a missing template never renders as its own name - that was the bug: callers received
    /// the literal template name back as if it were valid content, with no signal anything failed.
    /// </summary>
    public string? Render(string name, object model)
    {
        var template = GetTemplate(name);
        if (template is null)
        {
            return validateTemplates
                ? throw new FileNotFoundException($"Template '{name}' was not found under '{folderPath}'.", name)
                : null;
        }

        return _templateEngine == null
            ? TinyTemplateRendered.Render(template, model, false, folderPath)
            : _templateEngine.Render(template, model);
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
