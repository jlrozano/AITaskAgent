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

    /// <summary>
    /// Text the step or tool wants shown for this event, rendered from its <c>Display</c> template
    /// (null when it has none or the template failed). See <c>DisplayTextRenderer</c>.
    /// </summary>
    public string? Display { get; init; }

    /// <summary>Additional data added by derived tool classes.</summary>
    public Dictionary<string, object>? AdditionalData { get; init; }
}
