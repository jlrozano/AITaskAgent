using Microsoft.Extensions.Logging;

namespace AITaskAgent.Observability;

/// <summary>
/// Base interface for all progress events in the system.
/// Supports versioned schema evolution for backward compatibility.
/// </summary>
public interface IProgressEvent
{
    /// <summary>Message template of the common log line: the fields every event has.</summary>
    const string CommonLogFormat = "{EventType} from {StepName} | CorrelationId: {CorrelationId}";

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

    /// <summary>
    /// Writes this event to <paramref name="logger"/>. The channel decides whether and at which level events are
    /// logged; each event decides which of its own fields appear. The default writes the common fields only.
    /// </summary>
    void WriteLog(ILogger logger, LogLevel level) =>
        logger.Log(level, CommonLogFormat, EventType, StepName, CorrelationId);
}

