# GeminiLlmService for AITaskAgent

GeminiLlmService is an LLM provider implementation for the AITaskAgent framework, using the official Google.GenAI SDK. This service enables Google Gemini features like Context Caching, Google Search Grounding, and the Files API.

## Features

- Google Search Grounding: Retrieval of real-time information with citations.
- Context Caching: Significant cost reduction for requests using large, repetitive contexts.
- Files API: Management of large documents (PDFs, Videos, Images) outside the standard token window.
- Built-in RAG (File Search): Managed retrieval-augmented generation.
- Streaming Support: Real-time response delivery.
- Resilience: Integrated retry policies and rate limiting.

## Configuration

Register the Gemini provider in your application configuration:

```json
{
  "AITaskAgent": {
    "LlmProviders": {
      "Providers": {
        "Gemini": {
          "Provider": "Gemini",
          "ApiKey": "${GEMINI_API_KEY}",
          "Model": "gemini-2.5-flash",
          "Temperature": 0.7,
          "MaxTokens": 8192
        }
      },
      "DefaultProvider": "Gemini"
    }
  }
}
```

## Context Caching in Conversations

Context Caching allows you to store a large block of information (minimum 1024 tokens) on Google's infrastructure. Once cached, you can refer to this content in subsequent conversation turns without re-sending the full data, which reduces latency and provides 75-90% discount on input token costs.

### Managing the Cache

The service provides a `CacheManager` accessible through `svc.CacheManager`.

1. Creation: Upload content to create a cache.
   ```csharp
   var cache = await svc.CacheManager.CreateAsync(model, contents, "my-cache", TimeSpan.FromHours(1));
   ```
2. Persistence: Caches have a Time-To-Live (TTL). Use `UpdateTtlAsync` to extend it if needed.
3. Cleanup: Use `DeleteAsync` to remove the cache manually when the conversation or session ends.

### Using the Cache in Conversations

The core utility of caching is attaching it to a long-running conversation so the LLM always has access to the "foundation" data.

#### Automatic Attachment via Metadata

You can link a conversation to a cache using the provided extension method. Once linked, every request made with that `ConversationContext` will automatically include the cache reference.

```csharp
using GeminiLlmService;

// Create or retrieve your cache ID
string cacheName = "cachedContents/abc123...";

// Link the conversation to the cache
conversation.UseGeminiCache(cacheName);

// All subsequent calls will now use the cached content automatically
var response = await svc.InvokeAsync(request);
```

#### Manual Metadata Control

If you prefer not to use extension methods, you can set the metadata key directly:

```csharp
conversation.Metadata["Gemini.CachedContentName"] = "cachedContents/xyz...";
```

## Project Structure

- GeminiLlmService.cs: Core service implementation of ILlmService.
- GeminiCacheManager.cs: API for creating and managing cached content.
- GeminiFileManager.cs: API for the Gemini Files management.
- GeminiToolsConfig.cs: Configuration for native tools and conversation extensions.
- GeminiTypeConverters.cs: Logic for mapping between framework and SDK models.

## Limitations

Context Caching is currently a paid feature. Using it on a Free Tier API key will result in a "Quota exceeded (limit=0)" error. The library includes pre-validation to ensure content meets the minimum 1024 token requirement before attempting to create a cache.
