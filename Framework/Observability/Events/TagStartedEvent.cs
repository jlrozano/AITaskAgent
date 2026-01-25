namespace AITaskAgent.Observability.Events;

/// <summary>
/// Event emitted when a streaming tag starts execution.
/// </summary>
public sealed record TagStartedEvent : ProgressEventBase
{
    /// <inheritdoc />
    public override string EventType => AITaskAgent.Core.AITaskAgentConstants.EventTypes.TagStarted;

    /// <summary>Name of the tag being executed (e.g., "write_file").</summary>
    public required string TagName { get; init; }

    /// <summary>Tag attributes parsed from the opening tag.</summary>
    public Dictionary<string, string>? Attributes { get; init; }

    /// <summary>Additional data added by derived handler classes.</summary>
    public Dictionary<string, object>? AdditionalData { get; init; }
}
