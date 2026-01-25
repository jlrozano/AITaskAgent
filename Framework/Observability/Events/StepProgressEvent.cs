namespace AITaskAgent.Observability.Events;

/// <summary>
/// Generic progress event for custom step notifications.
/// </summary>
public sealed record StepProgressEvent : IProgressEvent
{
    /// <inheritdoc />
    public required string StepName { get; init; }

    /// <inheritdoc />
    public required string EventType { get; init; }

    /// <inheritdoc />
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public string? CorrelationId { get; init; }

    /// <inheritdoc />
    public bool SuppressFromUser { get; init; }

    /// <summary>Progress message.</summary>
    public required string Message { get; init; }

    /// <summary>Current progress value (optional).</summary>
    public int? CurrentProgress { get; init; }

    /// <summary>Total progress value (optional).</summary>
    public int? TotalProgress { get; init; }

    /// <summary>Additional metadata.</summary>
    public Dictionary<string, object?>? Metadata { get; init; }
}

