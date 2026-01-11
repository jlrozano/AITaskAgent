using AITaskAgent.LLM.Conversation.Models;
using System.Collections.Concurrent;

namespace PipelineVisualizer.Services;

/// <summary>
/// Manages conversation history persistence.
/// </summary>
public sealed class ContextStore
{
    private readonly ConcurrentDictionary<string, MessageHistory> _histories = new();

    /// <summary>
    /// Gets message history by conversation ID.
    /// </summary>
    public MessageHistory? GetHistory(string conversationId)
    {
        return _histories.TryGetValue(conversationId, out var history) ? history : null;
    }

    /// <summary>
    /// Saves or updates history for a conversation.
    /// </summary>
    public void SaveHistory(string conversationId, MessageHistory history)
    {
        _histories[conversationId] = history;
    }

    /// <summary>
    /// Removes a context.
    /// </summary>
    public void RemoveContext(string conversationId)
    {
        _histories.TryRemove(conversationId, out _);
    }
}
