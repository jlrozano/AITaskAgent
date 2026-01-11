using AITaskAgent.LLM.Models;
using Newtonsoft.Json;

namespace AITaskAgent.LLM.Conversation.Models;

/// <summary>
/// Manages conversation history with bookmarking and context management.
/// Uses framework-agnostic Message types.
/// </summary>
public sealed class MessageHistory(int maxTokens = 4000, Func<string, int>? tokenCounter = null)
{
    private readonly List<Message> _messages = [];
    private readonly Dictionary<string, int> _bookmarks = [];
    private readonly int _maxTokens = maxTokens;
    private readonly Func<string, int> _tokenCounter = tokenCounter ?? DefaultTokenCounter;

    public IReadOnlyList<Message> Messages => _messages.AsReadOnly();

    [JsonIgnore]
    public IReadOnlyDictionary<string, int> Bookmarks => _bookmarks;

    public int MaxTokens => _maxTokens;

    [JsonIgnore]
    public Func<string, int> TokenCounter => _tokenCounter;

    /// <summary>
    /// Adds a message to the history.
    /// </summary>
    public void AddMessage(Message message)
    {
        _messages.Add(message);
    }

    /// <summary>
    /// Adds multiple messages to the history.
    /// </summary>
    public void AddMessages(IEnumerable<Message> messages)
    {
        _messages.AddRange(messages);
    }

    /// <summary>
    /// Creates a bookmark at the current position.
    /// </summary>
    public string CreateBookmark()
    {
        var name = Guid.NewGuid().ToString();
        _bookmarks[name] = _messages.Count;
        return name;
    }

    /// <summary>
    /// Gets messages from a bookmark to the end.
    /// </summary>
    public List<Message> GetMessagesFromBookmark(string bookmarkName)
    {
        return !_bookmarks.TryGetValue(bookmarkName, out var index)
            ? throw new ArgumentException($"Bookmark '{bookmarkName}' not found", nameof(bookmarkName))
            : [.. _messages.Skip(index)];
    }

    /// <summary>
    /// Gets the most recent messages that fit within the token limit.
    /// </summary>
    public List<Message> GetRecentMessages(int? maxTokens = null)
    {
        var limit = maxTokens ?? _maxTokens;
        List<Message> result = [];
        var currentTokens = 0;

        // Iterate backwards to get most recent messages
        for (var i = _messages.Count - 1; i >= 0; i--)
        {
            var message = _messages[i];
            var tokens = EstimateMessageTokens(message);

            if (currentTokens + tokens > limit && result.Count > 0)
            {
                break;
            }

            result.Insert(0, message);
            currentTokens += tokens;
        }

        return result;
    }

    /// <summary>
    /// Gets messages with sliding window strategy.
    /// Always includes first N messages (context) and last M messages (recent).
    /// </summary>
    public List<Message> GetMessagesWithSlidingWindow(
        int keepFirstN = 2,
        int? maxTokens = null)
    {
        var limit = maxTokens ?? _maxTokens;

        if (_messages.Count <= keepFirstN)
        {
            return [.. _messages];
        }

        List<Message> result = [];
        var currentTokens = 0;

        // Always include first N messages (usually system + initial context)
        for (var i = 0; i < Math.Min(keepFirstN, _messages.Count); i++)
        {
            var message = _messages[i];
            result.Add(message);
            currentTokens += EstimateMessageTokens(message);
        }

        // Add recent messages that fit
        List<Message> recentMessages = [];
        for (var i = _messages.Count - 1; i >= keepFirstN; i--)
        {
            var message = _messages[i];
            var tokens = EstimateMessageTokens(message);

            if (currentTokens + tokens > limit)
            {
                break;
            }

            recentMessages.Insert(0, message);
            currentTokens += tokens;
        }

        result.AddRange(recentMessages);
        return result;
    }

    /// <summary>
    /// Clears all messages.
    /// </summary>
    public void Clear()
    {
        _messages.Clear();
        _bookmarks.Clear();
    }

    /// <summary>
    /// Clears messages after a bookmark.
    /// </summary>
    public void ClearAfterBookmark(string bookmarkName)
    {
        if (!_bookmarks.TryGetValue(bookmarkName, out var index))
        {
            throw new ArgumentException($"Bookmark '{bookmarkName}' not found", nameof(bookmarkName));
        }

        if (index < _messages.Count)
        {
            _messages.RemoveRange(index, _messages.Count - index);
        }

        _bookmarks.Remove(bookmarkName);

    }

    /// <summary>
    /// Copies the state from another MessageHistory instance.
    /// </summary>
    public void CopyFrom(MessageHistory other)
    {
        _messages.Clear();
        _messages.AddRange(other._messages);
        _bookmarks.Clear();
        foreach (var kvp in other._bookmarks)
        {
            _bookmarks[kvp.Key] = kvp.Value;
        }
    }

    /// <summary>
    /// Gets the total token count of all messages.
    /// </summary>
    public int GetTotalTokenCount()
    {
        return _messages.Sum(EstimateMessageTokens);
    }

    private int EstimateMessageTokens(Message message)
    {
        return _tokenCounter(message.Content ?? string.Empty);
    }

    private static int DefaultTokenCounter(string text)
    {
        // Rough estimation: ~4 characters per token
        return (int)Math.Ceiling(text.Length / 4.0);
    }

}

