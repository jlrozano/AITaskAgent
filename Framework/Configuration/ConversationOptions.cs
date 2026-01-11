namespace AITaskAgent.Configuration;

/// <summary>
/// Conversation management configuration options.
/// </summary>
public sealed class ConversationOptions
{
    /// <summary>Maximum tokens allowed in conversation context.</summary>
    public int MaxTokens { get; init; } = 8192;

    /// <summary>Enable bookmarks for conversation rollback.</summary>
    public bool UseBookmarks { get; init; } = true;

    /// <summary>Enable sliding window for context management.</summary>
    public bool UseSlidingWindow { get; init; } = true;

    /// <summary>Number of initial messages to always keep.</summary>
    public int KeepFirstNMessages { get; init; } = 2;

    /// <summary>Maximum tokens for sliding window.</summary>
    public int SlidingWindowMaxTokens { get; init; } = 8192;
}
