namespace AITaskAgent.Observability.Events;

/// <summary>
/// Base record for all progress events.
/// Provides common properties for event identification, timing, and correlation.
/// </summary>
public abstract record ProgressEventBase : IProgressEvent
{
    /// <inheritdoc />
    public required string StepName { get; init; }

    /// <inheritdoc />
    public abstract string EventType { get; }

    /// <inheritdoc />
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public string? CorrelationId { get; init; }

    /// <inheritdoc />
    public bool SuppressFromUser { get; init; }
}
