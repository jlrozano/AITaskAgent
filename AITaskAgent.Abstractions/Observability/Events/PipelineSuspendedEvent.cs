namespace AITaskAgent.Observability.Events;

/// <summary>
/// Event emitted when a pipeline enters HITL suspension state waiting for human input.
/// </summary>
public sealed record PipelineSuspendedEvent : ProgressEventBase
{
    /// <inheritdoc />
    public override string EventType => AITaskAgent.Core.AITaskAgentConstants.EventTypes.PipelineSuspended;

    /// <summary>Full path to the target step awaiting input.</summary>
    public required string TargetStepPath { get; init; }

    public bool IsBranchSuspension { get; init; }

    public string? BranchName { get; init; }

    /// <summary>Error message if suspension occurred due to an error.</summary>
    public string? ErrorMessage { get; init; }
}
