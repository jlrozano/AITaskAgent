namespace AITaskAgent.Observability.Events;

/// <summary>
/// Event emitted when a step completes execution.
/// </summary>
public sealed record StepCompletedEvent : ProgressEventBase, IStepCompletedEvent
{
    /// <inheritdoc />
    public override string EventType => AITaskAgent.Core.AITaskAgentConstants.EventTypes.StepCompleted;

    /// <summary>Whether the step completed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Duration of the step execution.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Error message if the step failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Additional data added by derived step classes.
    /// Allows LLM steps to include tokens, cost, etc. without changing the event type.
    /// </summary>
    public Dictionary<string, object>? AdditionalData { get; init; }
}
