namespace AITaskAgent.Observability.Events;

public sealed record BranchStartedEvent : ProgressEventBase
{
    public const string EventTypeName = "branch.started";

    public string? StepPath { get; init; }

    public string BranchName { get; init; } = string.Empty;

    public string BranchId { get; init; } = string.Empty;

    public override string EventType => EventTypeName;
}
