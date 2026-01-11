using AITaskAgent.LLM.Conversation.Context;
using System.Collections.Concurrent;

namespace AITaskAgent.LLM.Conversation.Storage;

/// <summary>
/// In-memory implementation of conversation storage for testing and development.
/// </summary>
public class InMemoryConversationStorage : IConversationStorage
{
    private readonly ConcurrentDictionary<string, ConversationContext> _store = [];

    public Task SaveConversationAsync(ConversationContext conversation, CancellationToken cancellationToken = default)
    {
        // Store a clone to prevent external mutations from affecting the stored state immediately
        _store[conversation.ConversationId] = conversation.Clone();
        return Task.CompletedTask;
    }

    public Task<ConversationContext?> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        if (_store.TryGetValue(conversationId, out var conversation))
        {
            // Return a clone so the caller has their own copy
            return Task.FromResult<ConversationContext?>(conversation.Clone());
        }
        return Task.FromResult<ConversationContext?>(null);
    }
}

