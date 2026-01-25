namespace AITaskAgent.Observability.Events;

/// <summary>
/// Event emitted when a streaming tag completes execution.
/// </summary>
public sealed record TagCompletedEvent : ProgressEventBase
{
    /// <inheritdoc />
    public override string EventType => AITaskAgent.Core.AITaskAgentConstants.EventTypes.TagCompleted;

    /// <summary>Name of the tag that completed (e.g., "write_file").</summary>
    public required string TagName { get; init; }

    /// <summary>Whether the tag executed successfully.</summary>
    public bool Success { get; init; } = true;

    /// <summary>Duration of the tag execution.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Error message if the tag failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Additional data added by derived handler classes.</summary>
    public Dictionary<string, object>? AdditionalData { get; init; }
}
