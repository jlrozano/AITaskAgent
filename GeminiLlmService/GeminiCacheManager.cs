using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace GeminiLlmService;

/// <summary>
/// Manager for Gemini Context Caching operations.
/// Context Caching provides 75-90% discount on cached tokens.
/// </summary>
/// <param name="client">Gemini client instance</param>
/// <param name="logger">Logger instance</param>
public sealed class GeminiCacheManager(Client client, ILogger logger)
{
    private readonly Client _client = client;
    private readonly ILogger _logger = logger;

    /// <summary>
    /// Creates a new context cache with the specified content.
    /// Default minimum token count is 32768 (as per Gemini 1.5 docs), but configurable.
    /// </summary>
    /// <param name="model">Model name (e.g., "gemini-2.5-flash")</param>
    /// <param name="contents">Content to cache (system prompts, documents, etc.)</param>
    /// <param name="displayName">Human-readable name for the cache</param>
    /// <param name="ttl">Time-to-live for the cache (default: 1 hour)</param>
    /// <param name="minTokenCount">Minimum tokens required to create cache (default: 1024 or based on model)</param>
    /// <returns>Created cache information</returns>
    public async Task<CachedContent> CreateAsync(
        string model,
        List<Content> contents,
        string displayName,
        TimeSpan? ttl = null,
        int minTokenCount = 1024)
    {
        // validate token count first to avoid expensive failures
        var tokenCount = await _client.Models.CountTokensAsync(model, contents);
        _logger.LogDebug(
            "Validation: Content has {Count} tokens (Minimum required: {Min})",
            tokenCount.TotalTokens, minTokenCount);

        if (tokenCount.TotalTokens < minTokenCount)
        {
            throw new InvalidOperationException(
                $"Content too small for caching. " +
                $"Has {tokenCount.TotalTokens} tokens, but requires at least {minTokenCount}. " +
                $"Caching is only cost-effective for large contexts.");
        }

        var ttlSeconds = ttl?.TotalSeconds ?? 3600;

        _logger.LogInformation(
            "Creating Gemini cache '{DisplayName}' for model {Model} with TTL {Ttl}s ({Tokens} tokens)",
            displayName, model, ttlSeconds, tokenCount.TotalTokens);

        var config = new CreateCachedContentConfig
        {
            Contents = contents,
            DisplayName = displayName,
            Ttl = $"{ttlSeconds}s"
        };

        var cache = await _client.Caches.CreateAsync(
            model: model,
            config: config);

        _logger.LogInformation(
            "Created cache '{CacheName}' with {TokenCount} tokens, expires at {ExpireTime}",
            cache.Name, cache.UsageMetadata?.TotalTokenCount, cache.ExpireTime);

        return cache;
    }

    /// <summary>
    /// Creates a cache from uploaded files.
    /// </summary>
    /// <param name="model">Model name</param>
    /// <param name="fileUris">List of file URIs (from Files API)</param>
    /// <param name="displayName">Human-readable name</param>
    /// <param name="ttl">Time-to-live</param>
    /// <returns>Created cache information</returns>
    public async Task<CachedContent> CreateFromFilesAsync(
        string model,
        IEnumerable<string> fileUris,
        string displayName,
        TimeSpan? ttl = null)
    {
        var contents = new List<Content>
        {
            new()
            {
                Role = "user",
                Parts = fileUris.Select(uri => new Part
                {
                    FileData = new FileData
                    {
                        FileUri = uri,
                        MimeType = GuessMimeType(uri)
                    }
                }).ToList()
            }
        };

        return await CreateAsync(model, contents, displayName, ttl);
    }

    /// <summary>
    /// Gets information about an existing cache.
    /// </summary>
    /// <param name="cacheName">Cache name (e.g., "cachedContents/abc123")</param>
    /// <returns>Cache information</returns>
    public async Task<CachedContent> GetAsync(string cacheName)
    {
        _logger.LogDebug("Getting cache: {CacheName}", cacheName);

        return await _client.Caches.GetAsync(name: cacheName);
    }

    /// <summary>
    /// Updates the TTL of an existing cache.
    /// </summary>
    /// <param name="cacheName">Cache name</param>
    /// <param name="newTtl">New time-to-live</param>
    /// <returns>Updated cache information</returns>
    public async Task<CachedContent> UpdateTtlAsync(string cacheName, TimeSpan newTtl)
    {
        _logger.LogInformation(
            "Updating cache '{CacheName}' TTL to {Ttl}s",
            cacheName, newTtl.TotalSeconds);

        var config = new UpdateCachedContentConfig
        {
            Ttl = $"{newTtl.TotalSeconds}s"
        };

        return await _client.Caches.UpdateAsync(
            name: cacheName,
            config: config);
    }

    /// <summary>
    /// Deletes a cache.
    /// </summary>
    /// <param name="cacheName">Cache name</param>
    public async Task DeleteAsync(string cacheName)
    {
        _logger.LogInformation("Deleting cache: {CacheName}", cacheName);

        await _client.Caches.DeleteAsync(name: cacheName);
    }

    /// <summary>
    /// Lists all available caches.
    /// </summary>
    /// <param name="pageSize">Number of items per page</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of caches</returns>
    public async IAsyncEnumerable<CachedContent> ListAsync(
        int pageSize = 20,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Listing caches with page size {PageSize}", pageSize);

        var pager = await _client.Caches.ListAsync();

        await foreach (var cache in pager.WithCancellation(cancellationToken))
        {
            yield return cache;
        }
    }

    /// <summary>
    /// Counts the tokens in the cached content.
    /// </summary>
    /// <param name="cacheName">Cache name</param>
    /// <returns>Token count</returns>
    public async Task<int> GetTokenCountAsync(string cacheName)
    {
        var cache = await GetAsync(cacheName);
        return cache.UsageMetadata?.TotalTokenCount ?? 0;
    }

    private static string GuessMimeType(string uri)
    {
        var extension = Path.GetExtension(uri).ToLowerInvariant();
        return extension switch
        {
            ".pdf" => "application/pdf",
            ".json" => "application/json",
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".html" => "text/html",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            _ => "application/octet-stream"
        };
    }
}
