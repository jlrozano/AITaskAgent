using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Conversation.Models;
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
    ILogger<ConversationContext>? logger = null)
{
    private readonly ILogger<ConversationContext> _logger = logger ?? NullLogger<ConversationContext>.Instance;

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
    public ConversationContext AddAssistantMessageWithToolCalls(IEnumerable<ToolCall> toolCalls)
    {
        var toolCallList = toolCalls.ToList();
        History.AddMessage(new Message
        {
            Role = LlmConstants.MessageRoles.Assistant,
            Content = string.Empty,
            ToolCalls = toolCallList
        });
        LastActivityAt = DateTime.UtcNow;
        _logger.LogDebug("Added assistant message with {ToolCallCount} tool calls to conversation {ConversationId}",
            toolCallList.Count, ConversationId);
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
        var bookmark = History.CreateBookmark();
        _logger.LogDebug("Created bookmark {BookmarkId} at position {Position} in conversation {ConversationId}",
            bookmark, History.Messages.Count, ConversationId);
        return bookmark;
    }

    /// <summary>
    /// Restores conversation to a bookmark, removing all messages after it.
    /// </summary>
    public void RestoreBookmark(string bookmark)
    {
        var messagesBefore = History.Messages.Count;
        History.ClearAfterBookmark(bookmark);
        _logger.LogDebug("Restored bookmark {BookmarkId} in conversation {ConversationId}, removed {RemovedCount} messages",
            bookmark, ConversationId, messagesBefore - History.Messages.Count);
    }

    /// <summary>
    /// Gets messages suitable for an LLM request with context management.
    /// </summary>
    public List<Message> GetMessagesForRequest(
        int? maxTokens = null,
        string? fromBookmark = null,
        bool useSlidingWindow = true)
    {
        var messages = fromBookmark != null
            ? History.GetMessagesFromBookmark(fromBookmark)
            : useSlidingWindow
            ? History.GetMessagesWithSlidingWindow(keepFirstN: 2, maxTokens: maxTokens)
            : History.GetRecentMessages(maxTokens);

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
        var clone = new ConversationContext(History.MaxTokens, History.TokenCounter, _logger)
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
