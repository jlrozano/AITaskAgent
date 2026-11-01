namespace AITaskAgent.Observability.Events;

/// <summary>
/// Event emitted when a pipeline completes execution.
/// </summary>
public sealed record PipelineCompletedEvent : ProgressEventBase
{
    /// <inheritdoc />
    public override string EventType => AITaskAgent.Core.AITaskAgentConstants.EventTypes.PipelineCompleted;

    /// <summary>Name of the pipeline.</summary>
    public required string PipelineName { get; init; }

    /// <summary>Whether the pipeline completed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Total duration of the pipeline execution.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Error message if the pipeline failed.</summary>
    public string? ErrorMessage { get; init; }
}
