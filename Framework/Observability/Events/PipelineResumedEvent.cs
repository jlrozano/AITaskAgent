namespace AITaskAgent.Observability.Events;

public sealed record PipelineResumedEvent : ProgressEventBase
{
    public const string EventTypeName = "pipeline.resumed";

    public string? StepPath { get; init; }

    public string? TargetStepPath { get; init; }

    public override string EventType => EventTypeName;
}
