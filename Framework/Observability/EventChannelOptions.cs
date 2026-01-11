using Microsoft.Extensions.Logging;

namespace AITaskAgent.Observability;

/// <summary>
/// Configuration options for EventChannel.
/// </summary>
public sealed class EventChannelOptions
{
    /// <summary>
    /// Log level for automatic event logging.
    /// Set to None to disable automatic logging (events are still sent to subscribers).
    /// Default: None
    /// </summary>
    public LogLevel EventLogLevel { get; set; } = LogLevel.None;

    /// <summary>
    /// Capacity of the bounded channel for event buffering.
    /// Default: 1000
    /// </summary>
    public int ChannelCapacity { get; set; } = 1000;
}


