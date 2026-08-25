namespace AITaskAgent.Observability.Events;

/// <summary>
/// Event emitted when a step times out.
/// </summary>
public sealed record StepTimeoutEvent : ProgressEventBase
{
    /// <inheritdoc />
    public override string EventType => "step.timeout";

    /// <summary>The path of the step that timed out.</summary>
    public string? StepPath { get; init; }

    /// <summary>The configured timeout value.</summary>
    public TimeSpan ConfiguredTimeout { get; init; }
}