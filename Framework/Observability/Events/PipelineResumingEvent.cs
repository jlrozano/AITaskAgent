namespace AITaskAgent.Observability.Events;

public sealed record PipelineResumingEvent : ProgressEventBase
{
    public const string EventTypeName = "pipeline.resuming";

    public string? StepPath { get; init; }

    public string? TargetStepPath { get; init; }

    public override string EventType => EventTypeName;
}
