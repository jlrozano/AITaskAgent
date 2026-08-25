namespace AITaskAgent.Observability;

/// <summary>
/// Base interface for all progress events in the system.
/// Supports versioned schema evolution for backward compatibility.
/// </summary>
public interface IProgressEvent
{
    /// <summary>Name of the step emitting this event.</summary>
    string StepName { get; }

    /// <summary>Event type identifier (e.g., "step.started", "tool.completed").</summary>
    string EventType { get; }

    /// <summary>Timestamp when the event was created.</summary>
    DateTimeOffset Timestamp { get; }

    /// <summary>Correlation ID for distributed tracing.</summary>
    string? CorrelationId { get; }

    /// <summary>If true, event should not be shown to end users (only logging/debugging).</summary>
    bool SuppressFromUser { get; }

    /// <summary>Schema version for backward compatibility and evolution tracking.</summary>
    string SchemaVersion { get; }
}

