using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.Observability;

namespace AITaskAgent.Core.Models;

/// <summary>
/// Factory for creating PipelineContext instances with default services.
/// Can be configured globally or per-instance for dependency injection.
/// Last instance created becomes the global Default (Last-Wins Singleton pattern).
/// </summary>
public sealed class PipelineContextFactory
{
    private readonly IEventChannel? _eventChannel;
    private static PipelineContextFactory? _default;

    public PipelineContextFactory(
        IEventChannel? eventChannel = null)
    {
        _eventChannel = eventChannel;
        _default = this; // Last-Wins Singleton
    }

    /// <summary>
    /// Global default factory instance.
    /// </summary>
    public static PipelineContextFactory Default => _default ??= new();

    /// <summary>
    /// Creates a new PipelineContext using default factory.
    /// </summary>
    public static PipelineContext Create(
        ConversationContext? conversation = null,
        string? correlationId = null)
        => Default.CreateContext(conversation, correlationId);

    /// <summary>
    /// Creates a new PipelineContext with optional overrides.
    /// </summary>
    public PipelineContext CreateContext(
        ConversationContext? conversation = null,
        string? correlationId = null)
    {
        return new PipelineContext(_eventChannel)
        {
            Conversation = conversation ?? new ConversationContext(),
            CorrelationId = correlationId ?? Guid.NewGuid().ToString(),
        };
    }
}
