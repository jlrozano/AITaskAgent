using AITaskAgent.LLM.Conversation.Context;

namespace AITaskAgent.LLM.Conversation.Storage;

/// <summary>
/// Interface for persisting conversation state.
/// </summary>
public interface IConversationStorage
{
    /// <summary>
    /// Saves the conversation state.
    /// </summary>
    Task SaveConversationAsync(ConversationContext conversation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a conversation by ID.
    /// </summary>
    Task<ConversationContext?> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default);
}

