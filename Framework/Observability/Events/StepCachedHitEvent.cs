namespace AITaskAgent.Observability.Events;

/// <summary>
/// Event emitted when a step result is retrieved from cache (cache hit).
/// </summary>
public sealed record StepCachedHitEvent : ProgressEventBase
{
    /// <inheritdoc />
    public override string EventType => "step.cached_hit";

    /// <summary>The path of the step that was retrieved from cache.</summary>
    public string? StepPath { get; init; }
}