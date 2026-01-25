using Google.GenAI.Types;

namespace GeminiLlmService;

/// <summary>
/// Configuration for Gemini-specific built-in tools.
/// These tools are native to Gemini and don't require external implementations.
/// </summary>
public sealed class GeminiToolsConfig
{
    /// <summary>
    /// Enable Google Search Grounding.
    /// When enabled, Gemini can search the web for real-time information
    /// and ground responses with citations.
    /// </summary>
    public bool EnableGoogleSearch { get; init; }

    /// <summary>
    /// Enable Code Execution tool.
    /// When enabled, Gemini can generate and execute Python code in a sandbox.
    /// Includes libraries: NumPy, Pandas, Matplotlib, SciPy, TensorFlow, etc.
    /// </summary>
    public bool EnableCodeExecution { get; init; }

    /// <summary>
    /// Name of the cached content to use for this request.
    /// Using cached content provides 75-90% discount on token costs.
    /// Format: "cachedContents/abc123"
    /// </summary>
    public string? CacheName { get; init; }

    /// <summary>
    /// Dynamic threshold for Google Search (0.0 to 1.0).
    /// Only applies to Gemini 1.5 models with google_search_retrieval.
    /// Higher values mean the model needs more confidence before searching.
    /// </summary>
    public float? SearchDynamicThreshold { get; init; }

    /// <summary>
    /// Builds the list of Gemini Tools based on this configuration.
    /// </summary>
    /// <returns>List of configured tools</returns>
    public List<Tool> BuildTools()
    {
        var tools = new List<Tool>();

        if (EnableGoogleSearch)
        {
            tools.Add(new Tool
            {
                GoogleSearch = new GoogleSearch()
            });
        }

        if (EnableCodeExecution)
        {
            tools.Add(new Tool
            {
                CodeExecution = new ToolCodeExecution()
            });
        }

        return tools;
    }

    /// <summary>
    /// Creates a configuration with Google Search enabled.
    /// </summary>
    public static GeminiToolsConfig WithGoogleSearch() => new()
    {
        EnableGoogleSearch = true
    };

    /// <summary>
    /// Creates a configuration with Code Execution enabled.
    /// </summary>
    public static GeminiToolsConfig WithCodeExecution() => new()
    {
        EnableCodeExecution = true
    };

    /// <summary>
    /// Creates a configuration with a cached context.
    /// </summary>
    /// <param name="cacheName">Name of the cache to use</param>
    public static GeminiToolsConfig WithCache(string cacheName) => new()
    {
        CacheName = cacheName
    };

    /// <summary>
    /// Creates a configuration with Google Search and Code Execution enabled.
    /// </summary>
    public static GeminiToolsConfig WithSearchAndCodeExecution() => new()
    {
        EnableGoogleSearch = true,
        EnableCodeExecution = true
    };
}

/// <summary>
/// Extension methods for applying GeminiToolsConfig to requests.
/// </summary>
public static class GeminiToolsConfigExtensions
{
    /// <summary>
    /// Applies Gemini tools configuration to a GenerateContentConfig.
    /// </summary>
    /// <param name="config">The config to modify</param>
    /// <param name="toolsConfig">The tools configuration to apply</param>
    /// <returns>The modified config</returns>
    public static GenerateContentConfig ApplyGeminiTools(
        this GenerateContentConfig config,
        GeminiToolsConfig toolsConfig)
    {
        var builtInTools = toolsConfig.BuildTools();

        if (builtInTools.Count > 0)
        {
            config.Tools ??= [];
            config.Tools.AddRange(builtInTools);
        }

        // Note: CacheName is handled at the request level, not in config

        return config;
    }

    /// <summary>
    /// Adds Google Search grounding to the config.
    /// </summary>
    public static GenerateContentConfig WithGoogleSearch(this GenerateContentConfig config)
    {
        config.Tools ??= [];
        config.Tools.Add(new Tool { GoogleSearch = new GoogleSearch() });
        return config;
    }

    /// <summary>
    /// Adds Code Execution to the config.
    /// </summary>
    public static GenerateContentConfig WithCodeExecution(this GenerateContentConfig config)
    {
        config.Tools ??= [];
        config.Tools.Add(new Tool { CodeExecution = new ToolCodeExecution() });
        return config;
    }

    /// <summary>
    /// Configures a conversation to use a specific Gemini context cache.
    /// This ensures subsequent turns in the conversation benefit from the cached data.
    /// </summary>
    /// <param name="conversation">The conversation context to modify</param>
    /// <param name="cacheName">Unique name of the Gemini cache (e.g. cachedContents/abc123)</param>
    public static AITaskAgent.LLM.Conversation.Context.ConversationContext UseGeminiCache(
        this AITaskAgent.LLM.Conversation.Context.ConversationContext conversation,
        string cacheName)
    {
        conversation.Metadata["Gemini.CachedContentName"] = cacheName;
        return conversation;
    }
}
