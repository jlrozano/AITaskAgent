using AITaskAgent.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITaskAgent.LLM.Conversation.Context;

/// <summary>
/// Context for managing a conversation with history and metadata.
/// Uses framework-agnostic Message types.
/// </summary>
public sealed class ConversationContext(
    int maxTokens = 4000,
    Func<string, int>? tokenCounter = null,
    ILogger<ConversationContext>? logger = null,
    ConversationOptions? conversationOptions = null)
{
    private readonly ILogger<ConversationContext> _logger = logger ?? NullLogger<ConversationContext>.Instance;

    // Falls back to ConversationOptions' own defaults (KeepFirstNMessages: 2, UseBookmarks: true)
    // when no options are supplied, matching the behavior every call site had before these were
    // configurable, while letting a caller that does wire IOptions<AITaskAgentOptions>.Conversation
    // (or ConversationOptions directly) actually change it instead of hitting a hardcoded value.
    private readonly int _keepFirstNMessages = conversationOptions?.KeepFirstNMessages ?? new ConversationOptions().KeepFirstNMessages;
    private readonly bool _useBookmarks = conversationOptions?.UseBookmarks ?? new ConversationOptions().UseBookmarks;

    /// <summary>Unique identifier for this conversation.</summary>
    public string ConversationId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>The message history.</summary>
    public MessageHistory History { get; } = new MessageHistory(maxTokens, tokenCounter);

    /// <summary>Metadata associated with this conversation.</summary>
    public Dictionary<string, object?> Metadata { get; init; } = [];

    /// <summary>When the conversation started.</summary>
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Last activity timestamp.</summary>
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Adds a user message to the conversation.
    /// </summary>
    public ConversationContext AddUserMessage(string content)
    {
        History.AddMessage(new Message
        {
            Role = LlmConstants.MessageRoles.User,
            Content = content
        });
        LastActivityAt = DateTime.UtcNow;
        _logger.LogTrace("Added user message to conversation {ConversationId}, length: {Length}",
            ConversationId, content.Length);
        return this;
    }

    /// <summary>
    /// Adds an assistant message to the conversation.
    /// </summary>
    public ConversationContext AddAssistantMessage(string content)
    {
        History.AddMessage(new Message
        {
            Role = LlmConstants.MessageRoles.Assistant,
            Content = content
        });
        LastActivityAt = DateTime.UtcNow;
        _logger.LogTrace("Added assistant message to conversation {ConversationId}, length: {Length}",
            ConversationId, content.Length);
        return this;
    }

    /// <summary>
    /// Adds an assistant message with tool calls to the conversation.
    /// </summary>
    /// <param name="toolCalls">The tool calls requested by the assistant.</param>
    /// <param name="content">
    /// Text the assistant produced alongside the tool calls. Models can do both in one response,
    /// and dropping the text loses the record of any in-text action (a &lt;write_file&gt; tag, say)
    /// that already ran: the model then cannot tell from the history that it is done, and repeats
    /// itself. Pass the processed text - tag bodies replaced by their reference - not the raw one.
    /// </param>
    public ConversationContext AddAssistantMessageWithToolCalls(
        IEnumerable<ToolCall> toolCalls,
        string? content = null)
    {
        List<ToolCall> toolCallList = toolCalls.ToList();
        History.AddMessage(new Message
        {
            Role = LlmConstants.MessageRoles.Assistant,
            Content = content ?? string.Empty,
            ToolCalls = toolCallList
        });
        LastActivityAt = DateTime.UtcNow;
        _logger.LogDebug(
            "Added assistant message with {ToolCallCount} tool calls to conversation {ConversationId}, "
            + "content length: {Length}",
            toolCallList.Count, ConversationId, content?.Length ?? 0);
        return this;
    }

    /// <summary>
    /// Replaces the message at <paramref name="index"/> in the history (see
    /// <see cref="MessageHistory.ReplaceMessage"/>) and updates <see cref="LastActivityAt"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the history.</exception>
    public ConversationContext ReplaceMessage(int index, Message message)
    {
        History.ReplaceMessage(index, message);
        LastActivityAt = DateTime.UtcNow;
        _logger.LogTrace("Replaced message {Index} in conversation {ConversationId}", index, ConversationId);
        return this;
    }

    /// <summary>
    /// Adds a tool response message to the conversation.
    /// </summary>
    public ConversationContext AddToolMessage(string toolCallId, string content)
    {
        History.AddMessage(new Message
        {
            Role = LlmConstants.MessageRoles.Tool,
            ToolCallId = toolCallId,
            Content = content
        });
        LastActivityAt = DateTime.UtcNow;
        _logger.LogTrace("Added tool response to conversation {ConversationId}, toolCallId: {ToolCallId}",
            ConversationId, toolCallId);
        return this;
    }

    /// <summary>
    /// Adds a system message to the conversation.
    /// </summary>
    public ConversationContext AddSystemMessage(string content)
    {
        History.AddMessage(new Message
        {
            Role = LlmConstants.MessageRoles.System,
            Content = content
        });
        LastActivityAt = DateTime.UtcNow;
        _logger.LogTrace("Added system message to conversation {ConversationId}", ConversationId);
        return this;
    }

    /// <summary>
    /// Adds a raw Message directly (for full control).
    /// </summary>
    public void AddMessage(Message message)
    {
        History.AddMessage(message);
        LastActivityAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Creates a bookmark at the current position in the conversation.
    /// </summary>
    public string CreateBookmark()
    {
        string bookmark = History.CreateBookmark();
        _logger.LogDebug("Created bookmark {BookmarkId} at position {Position} in conversation {ConversationId}",
            bookmark, History.Messages.Count, ConversationId);
        return bookmark;
    }

    /// <summary>
    /// Restores conversation to a bookmark, removing all messages after it.
    /// </summary>
    public void RestoreBookmark(string bookmark)
    {
        int messagesBefore = History.Messages.Count;
        History.ClearAfterBookmark(bookmark);
        _logger.LogDebug("Restored bookmark {BookmarkId} in conversation {ConversationId}, removed {RemovedCount} messages",
            bookmark, ConversationId, messagesBefore - History.Messages.Count);
    }

    /// <summary>
    /// Gets messages suitable for an LLM request with context management.
    /// With <paramref name="useSlidingWindow"/> false the whole history is returned and
    /// <paramref name="maxTokens"/> is ignored: if it exceeds the model's context, the provider's
    /// explicit error is preferable to silently dropping the initial prompt, which is what trimming
    /// to the most recent messages did.
    /// </summary>
    public List<Message> GetMessagesForRequest(
        int? maxTokens = null,
        string? fromBookmark = null,
        bool useSlidingWindow = true)
    {
        var messages = fromBookmark != null && _useBookmarks
            ? History.GetMessagesFromBookmark(fromBookmark)
            : useSlidingWindow
            ? History.GetMessagesWithSlidingWindow(keepFirstN: _keepFirstNMessages, maxTokens: maxTokens)
            : [.. History.Messages];

        if (useSlidingWindow && messages.Count < History.Messages.Count)
        {
            _logger.LogDebug("Sliding window applied in conversation {ConversationId}: {ReturnedCount}/{TotalCount} messages",
                ConversationId, messages.Count, History.Messages.Count);
        }

        return messages;
    }

    /// <summary>
    /// Creates a deep copy of the conversation context.
    /// </summary>
    public ConversationContext Clone()
    {
        ConversationContext clone = new(
            History.MaxTokens,
            History.TokenCounter,
            _logger,
            new ConversationOptions { KeepFirstNMessages = _keepFirstNMessages, UseBookmarks = _useBookmarks })
        {
            ConversationId = ConversationId, // Keep same ID to represent same logical conversation in parallel branches
            Metadata = new Dictionary<string, object?>(Metadata),
            StartedAt = StartedAt,
            LastActivityAt = LastActivityAt
        };

        clone.History.CopyFrom(History);
        _logger.LogDebug("Cloned conversation {ConversationId} with {MessageCount} messages",
            ConversationId, History.Messages.Count);
        return clone;
    }
}
