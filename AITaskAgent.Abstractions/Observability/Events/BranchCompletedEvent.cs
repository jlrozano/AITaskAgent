namespace AITaskAgent.Observability.Events;

public sealed record BranchCompletedEvent : ProgressEventBase
{
    public const string EventTypeName = "branch.completed";

    public string? StepPath { get; init; }

    public string BranchName { get; init; } = string.Empty;

    public string BranchId { get; init; } = string.Empty;

    public bool Success { get; init; }

    public TimeSpan Duration { get; init; }

    public string? ErrorMessage { get; init; }

    public override string EventType => EventTypeName;
}
