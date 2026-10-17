using Microsoft.Extensions.Logging;

namespace AITaskAgent.Observability.Events;

/// <summary>
/// Base record for all progress events.
/// Provides common properties for event identification, timing, correlation, and versioning.
/// Supports backward compatibility through schema versioning.
/// </summary>
public abstract record ProgressEventBase : IProgressEvent
{
    /// <inheritdoc />
    public string StepName { get; init; } = string.Empty;

    /// <inheritdoc />
    public abstract string EventType { get; }

    /// <inheritdoc />
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public string? CorrelationId { get; init; }

    /// <inheritdoc />
    public bool SuppressFromUser { get; init; }

    /// <inheritdoc />
    public string SchemaVersion { get; init; } = "1.0";

    /// <inheritdoc />
    /// <remarks>Override to log the fields specific to an event type.</remarks>
    public virtual void WriteLog(ILogger logger, LogLevel level) =>
        logger.Log(level, IProgressEvent.CommonLogFormat, EventType, StepName, CorrelationId);
}
