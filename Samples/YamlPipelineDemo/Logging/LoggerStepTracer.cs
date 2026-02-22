using AITaskAgent.Observability;
using Microsoft.Extensions.Logging;

namespace YamlPipelineDemo.Logging;

/// <summary>
/// IStepTracer implementation that writes trace events to the structured logger
/// (YAML file) instead of the console.
/// Replaces the default ConsoleStepTracer registered by AddAITaskAgent().
/// </summary>
internal sealed class LoggerStepTracer(ILogger<LoggerStepTracer> logger) : IStepTracer
{
    public Task OnTraceEventAsync(StepTraceEvent evt)
    {
        var level = evt.Status switch
        {
            StepStatus.Failed => LogLevel.Error,
            StepStatus.Retrying => LogLevel.Warning,
            StepStatus.Completed => LogLevel.Information,
            _ => LogLevel.Debug,
        };

        logger.Log(level,
            "[Trace] [{Step}] {Status}{Message}",
            evt.StepName,
            evt.Status,
            evt.Message != null ? $": {evt.Message}" : string.Empty);

        if (evt.StreamingContent != null)
            logger.LogDebug("[Trace] [{Step}] streaming: {Content}", evt.StepName, evt.StreamingContent);

        return Task.CompletedTask;
    }
}
