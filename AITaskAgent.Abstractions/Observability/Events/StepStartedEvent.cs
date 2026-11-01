namespace AITaskAgent.Observability.Events;

/// <summary>
/// Event emitted when a step starts execution.
/// </summary>
public sealed record StepStartedEvent : ProgressEventBase
{
    /// <inheritdoc />
    public override string EventType => AITaskAgent.Core.AITaskAgentConstants.EventTypes.StepStarted;

    public string? StepPath { get; init; }

    /// <summary>Current attempt number (1-based).</summary>
    public int AttemptNumber { get; init; } = 1;

    /// <summary>
    /// Text the step or tool wants shown for this event, rendered from its <c>Display</c> template
    /// (null when it has none or the template failed). See <c>DisplayTextRenderer</c>.
    /// </summary>
    public string? Display { get; init; }

    /// <summary>Additional data added by derived step classes before execution.</summary>
    public Dictionary<string, object>? AdditionalData { get; init; }
}
