namespace AITaskAgent.Observability.Events;

/// <summary>
/// Event emitted when a tool completes execution.
/// </summary>
public sealed record ToolCompletedEvent : ProgressEventBase
{
    /// <inheritdoc />
    public override string EventType => AITaskAgent.Core.AITaskAgentConstants.EventTypes.ToolCompleted;

    /// <summary>Name of the tool that was executed.</summary>
    public required string ToolName { get; init; }

    /// <summary>Whether the tool completed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Duration of the tool execution.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Error message if the tool failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Additional data added by derived tool classes.</summary>
    public Dictionary<string, object>? AdditionalData { get; init; }
}
