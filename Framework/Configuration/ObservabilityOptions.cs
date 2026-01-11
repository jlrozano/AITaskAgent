using Microsoft.Extensions.Logging;

namespace AITaskAgent.Configuration;

/// <summary>
/// Observability configuration options.
/// </summary>
public sealed class ObservabilityOptions
{
    /// <summary>Log level for EventChannel events.</summary>
    public LogLevel EventLogLevel { get; init; } = LogLevel.Debug;

    /// <summary>Event channel buffer capacity per subscriber.</summary>
    public int EventChannelCapacity { get; init; } = 100;

    /// <summary>Enable telemetry metrics.</summary>
    public bool EnableMetrics { get; init; } = true;

    /// <summary>Enable OpenTelemetry tracing.</summary>
    public bool EnableTracing { get; init; } = true;
}
