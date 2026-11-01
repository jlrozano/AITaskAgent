using System.Diagnostics;

namespace AITaskAgent.Core.Base;

/// <summary>
/// Centralized ActivitySource for OpenTelemetry distributed tracing.
/// Use Telemetry.Source.StartActivity() to create spans.
/// Tag constants are in AITaskAgentConstants.TelemetryTags.
/// </summary>
public static class Telemetry
{
    /// <summary>
    /// The ActivitySource for creating spans.
    /// </summary>
    public static readonly ActivitySource Source = new(
        AITaskAgentConstants.TelemetryTags.ActivitySourceName,
        AITaskAgentConstants.TelemetryTags.ActivitySourceVersion);
}
