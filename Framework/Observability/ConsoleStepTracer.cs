namespace AITaskAgent.Observability;

/// <summary>
/// Console implementation of step tracer for development/debugging.
/// Outputs trace events to console with visual indicators.
/// </summary>
public sealed class ConsoleStepTracer : IStepTracer
{
    public Task OnTraceEventAsync(StepTraceEvent evt)
    {
        var icon = evt.Status switch
        {
            StepStatus.Started => "▶️",
            StepStatus.InProgress => "⏳",
            StepStatus.Retrying => "🔄",
            StepStatus.Completed => "✅",
            StepStatus.Failed => "❌",
            _ => "ℹ️"
        };

        var message = evt.Message != null ? $": {evt.Message}" : "";
        Console.WriteLine($"{icon} [{evt.StepName}] {evt.Status}{message}");

        if (evt.StreamingContent != null)
        {
            Console.Write(evt.StreamingContent);
        }

        return Task.CompletedTask;
    }
}

