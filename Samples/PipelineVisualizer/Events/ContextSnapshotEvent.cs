using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using Newtonsoft.Json;

namespace PipelineVisualizer.Events;

/// <summary>
/// Event emitted after each step containing a snapshot of the PipelineContext.
/// Used to show real-time context changes in the frontend.
/// </summary>
public sealed record ContextSnapshotEvent : ProgressEventBase
{
    /// <inheritdoc />
    public override string EventType => "context.snapshot";

    /// <summary>
    /// Snapshot of StepResults as serializable dictionary.
    /// </summary>
    public required Dictionary<string, object?> StepResults { get; init; }

    /// <summary>
    /// Snapshot of Metadata as serializable dictionary.
    /// </summary>
    public required Dictionary<string, object?> Metadata { get; init; }

    /// <summary>
    /// Current execution path in the pipeline.
    /// </summary>
    public required string CurrentPath { get; init; }
}
