namespace AITaskAgent.Observability.Events;

/// <summary>
/// Event emitted when a step starts execution.
/// </summary>
public sealed record StepStartedEvent : ProgressEventBase
{
    /// <inheritdoc />
    public override string EventType => AITaskAgent.Core.AITaskAgentConstants.EventTypes.StepStarted;

    /// <summary>Current attempt number (1-based).</summary>
    public int AttemptNumber { get; init; } = 1;

    /// <summary>Additional data added by derived step classes before execution.</summary>
    public Dictionary<string, object>? AdditionalData { get; init; }
}
