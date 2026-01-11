namespace AITaskAgent.Observability.Events;

/// <summary>
/// Event emitted when a tool starts execution.
/// </summary>
public sealed record ToolStartedEvent : ProgressEventBase
{
    /// <inheritdoc />
    public override string EventType => AITaskAgent.Core.AITaskAgentConstants.EventTypes.ToolStarted;

    /// <summary>Name of the tool being executed.</summary>
    public required string ToolName { get; init; }

    /// <summary>Additional data added by derived tool classes.</summary>
    public Dictionary<string, object>? AdditionalData { get; init; }
}
