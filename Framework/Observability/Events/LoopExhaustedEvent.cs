namespace AITaskAgent.Observability.Events;

public sealed record LoopExhaustedEvent : ProgressEventBase
{
    public override string EventType => AITaskAgent.Core.AITaskAgentConstants.EventTypes.LoopExhausted;

    public string LoopName { get; init; } = string.Empty;

    public int IterationsCompleted { get; init; }

    public int MaxIterations { get; init; }
}
