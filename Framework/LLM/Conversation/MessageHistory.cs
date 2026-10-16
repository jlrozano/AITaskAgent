using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using Newtonsoft.Json;

namespace AITaskAgent.LLM.Conversation;

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
    /// Remove message at index position and return it. Returns null if the history is empty.
    /// </summary>
    public Message? RemoveMessage(int index)
    {
        if (index < 0 || index >= _messages.Count)
        {
            return null;
        }

        var removedMessage = _messages[index];
        _messages.RemoveAt(index);
        return removedMessage;
    }

    /// <summary>
    /// Replaces the message at <paramref name="index"/> in place. The count and the bookmarks do not
    /// change: it is a data operation on one message (e.g. swapping an already consumed tool result
    /// for a summary), not a change to the shape of the history.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the history.</exception>
    public void ReplaceMessage(int index, Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (index < 0 || index >= _messages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index,
                $"Index must be between 0 and {_messages.Count - 1}.");
        }

        _messages[index] = message;
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
    /// Gets the most recent messages that fit within the token limit. The last message, together
    /// with the assistant(tool_calls) message it answers if it is a tool result, is always
    /// included even over the limit, and the result never starts with an orphaned tool result.
    /// </summary>
    public List<Message> GetRecentMessages(int? maxTokens = null)
    {
        var limit = maxTokens ?? _maxTokens;
        if (_messages.Count == 0)
        {
            return [];
        }

        var start = FindLastGroupStart(lowerBound: 0);
        var currentTokens = 0;
        for (var i = start; i < _messages.Count; i++)
        {
            currentTokens += EstimateMessageTokens(_messages[i]);
        }

        // Iterate backwards to get most recent messages
        for (var i = start - 1; i >= 0; i--)
        {
            var tokens = EstimateMessageTokens(_messages[i]);
            if (currentTokens + tokens > limit)
            {
                break;
            }

            currentTokens += tokens;
            start = i;
        }

        // A leading tool result lost its assistant(tool_calls) message to the budget; providers
        // reject it (Azure/OpenAI answer 400). The last group starts with a non-tool message, so
        // this stops there at the latest.
        while (start < _messages.Count && _messages[start].Role == LlmConstants.MessageRoles.Tool)
        {
            start++;
        }

        return [.. _messages.Skip(start)];
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
        var headCount = Math.Min(keepFirstN, _messages.Count);

        // The tool results that directly follow the head belong to an assistant(tool_calls)
        // message inside it, and they must travel together: sending the calls without their
        // responses is an invalid request (Azure/OpenAI answer 400, "tool_call_ids did not have
        // response messages"). That is exactly what happened when the head alone - a large prompt
        // - already exceeded the budget: the assistant was always kept, its results never fit.
        // They are kept even if that exceeds the budget; an over-budget request can still succeed,
        // an invalid one cannot.
        while (headCount < _messages.Count && _messages[headCount].Role == LlmConstants.MessageRoles.Tool)
        {
            headCount++;
        }

        for (var i = 0; i < headCount; i++)
        {
            var message = _messages[i];
            result.Add(message);
            currentTokens += EstimateMessageTokens(message);
        }

        // The last group - the last message, plus the assistant(tool_calls) message it answers if
        // it is a tool result - is always sent, even over budget. Otherwise a head that already
        // spends the budget leaves the model without its latest tool results (or the latest user
        // message), and it keeps repeating the same calls without ever seeing their answers.
        List<Message> recentMessages = [];
        var cutIndex = _messages.Count;
        if (headCount < _messages.Count)
        {
            cutIndex = FindLastGroupStart(lowerBound: headCount);
            for (var i = cutIndex; i < _messages.Count; i++)
            {
                recentMessages.Add(_messages[i]);
                currentTokens += EstimateMessageTokens(_messages[i]);
            }
        }

        // Add older messages that fit. cutIndex is the index in _messages of the first message in
        // recentMessages - tracked so the orphan check below can find, by position, the
        // assistant(tool_calls) message a leading tool result answers, instead of assuming it was
        // cut just because a tool message happens to be first.
        for (var i = cutIndex - 1; i >= headCount; i--)
        {
            var message = _messages[i];
            var tokens = EstimateMessageTokens(message);

            if (currentTokens + tokens > limit)
            {
                break;
            }

            recentMessages.Insert(0, message);
            currentTokens += tokens;
            cutIndex = i;
        }

        // A tool result only makes sense right after the assistant message that requested it, and
        // providers reject a `tool` message whose request is missing (OpenAI/Azure answer 400,
        // "tool_call_id did not have response message"). A leading tool message in recentMessages
        // is only actually orphaned when its assistant(tool_calls) message was itself excluded by
        // the budget - not when that assistant fell inside the always-kept first N messages, which
        // is the common case for an early turn (e.g. exactly [user, assistant(tool_calls), tool]).
        while (recentMessages.Count > 0 && recentMessages[0].Role == LlmConstants.MessageRoles.Tool)
        {
            // The owner immediately precedes its run of tool results, however many tool calls it
            // made in one turn - walk back over that whole run to find it.
            var ownerIndex = cutIndex - 1;
            while (ownerIndex >= 0 && _messages[ownerIndex].Role == LlmConstants.MessageRoles.Tool)
            {
                ownerIndex--;
            }

            if (ownerIndex < headCount)
            {
                // Already present in the always-kept head: this tool result is not orphaned.
                break;
            }

            // The assistant that requested it was excluded by the budget: drop this result too.
            recentMessages.RemoveAt(0);
            cutIndex++;
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

    /// <summary>
    /// Index of the first message of the last group: the last message itself or, when it is a
    /// tool result, the assistant(tool_calls) message that precedes its run of tool results.
    /// Never goes below <paramref name="lowerBound"/>.
    /// </summary>
    private int FindLastGroupStart(int lowerBound)
    {
        var i = _messages.Count - 1;
        while (i > lowerBound && _messages[i].Role == LlmConstants.MessageRoles.Tool)
        {
            i--;
        }

        return i;
    }

    private int EstimateMessageTokens(Message message)
    {
        var tokens = _tokenCounter(message.Content ?? string.Empty);

        // An assistant(tool_calls) message can carry all its weight in the tool calls
        // themselves (Content is often empty), so leaving them out lets the sliding window
        // undercount exactly the messages that grow largest in a tool-use loop.
        if (message.ToolCalls is { Count: > 0 })
        {
            foreach (var toolCall in message.ToolCalls)
            {
                tokens += _tokenCounter(toolCall.Name);
                tokens += _tokenCounter(toolCall.Arguments);
            }
        }

        return tokens;
    }

    private static int DefaultTokenCounter(string text)
    {
        // Rough estimation: ~4 characters per token
        return (int)Math.Ceiling(text.Length / 4.0);
    }

}

