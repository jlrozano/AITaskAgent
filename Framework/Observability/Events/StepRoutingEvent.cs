namespace AITaskAgent.Observability.Events;

/// <summary>
/// Event published when a router step makes a routing decision.
/// </summary>
public sealed record StepRoutingEvent : ProgressEventBase
{
    /// <inheritdoc />
    public override string EventType => AITaskAgent.Core.AITaskAgentConstants.EventTypes.StepRouting;

    /// <summary>Name of the selected route.</summary>
    public required string SelectedRoute { get; init; }

    /// <summary>Reason for the routing decision.</summary>
    public required string RoutingReason { get; init; }
}
