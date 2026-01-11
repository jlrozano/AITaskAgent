namespace AITaskAgent.Observability.Events;

/// <summary>
/// Event emitted when a pipeline starts execution.
/// </summary>
public sealed record PipelineStartedEvent : ProgressEventBase
{
    /// <inheritdoc />
    public override string EventType => AITaskAgent.Core.AITaskAgentConstants.EventTypes.PipelineStarted;

    /// <summary>Name of the pipeline.</summary>
    public required string PipelineName { get; init; }

    /// <summary>Total number of steps in the pipeline.</summary>
    public int TotalSteps { get; init; }
}
