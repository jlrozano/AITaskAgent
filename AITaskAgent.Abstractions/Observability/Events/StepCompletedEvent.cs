using AITaskAgent.Core.Abstractions;

namespace AITaskAgent.Observability.Events;

/// <summary>
/// Event emitted when a step completes execution.
/// </summary>
public sealed record StepCompletedEvent : ProgressEventBase, IStepCompletedEvent
{
    /// <inheritdoc />
    public override string EventType => AITaskAgent.Core.AITaskAgentConstants.EventTypes.StepCompleted;

    public string? StepPath { get; init; }

    /// <summary>Whether the step completed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Duration of the step execution.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Error message if the step failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Text the step or tool wants shown for this event, rendered from its <c>Display</c> template
    /// (null when it has none or the template failed). See <c>DisplayTextRenderer</c>.
    /// </summary>
    public string? Display { get; init; }

    /// <summary>
    /// Additional data added by derived step classes.
    /// Allows LLM steps to include tokens, cost, etc. without changing the event type.
    /// </summary>
    public Dictionary<string, object?> AdditionalData { get; init; } = [];
    /// <summary>
    /// Gets the result of the step, if available.
    /// </summary>
    public IStepResult? StepResult { get; init; }
}
