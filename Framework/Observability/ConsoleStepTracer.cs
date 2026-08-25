namespace AITaskAgent.Observability;

/// <summary>
/// Console implementation of step tracer for development/debugging.
/// Outputs trace events to console with visual indicators.
/// </summary>
public sealed class ConsoleStepTracer : IStepTracer
{
    public Task OnTraceEventAsync(StepTraceEvent evt)
    {
        string icon = evt.Status switch
        {
            StepStatus.Started => "▶️",
            StepStatus.InProgress => "⏳",
            StepStatus.Retrying => "🔄",
            StepStatus.Completed => "✅",
            StepStatus.Failed => "❌",
            _ => "ℹ️"
        };

        // A substring check for "Loop exhausted" used to live here. Nothing ever emitted such a
        // trace, and an exhausted loop now reports through LoopExhaustedEvent on the event channel,
        // which carries the fact as a type instead of as prose.
        string message = evt.Message != null ? $": {evt.Message}" : "";
        Console.WriteLine($"{icon} [{evt.StepName}] {evt.Status}{message}");

        if (evt.StreamingContent != null)
        {
            Console.Write(evt.StreamingContent);
        }

        return Task.CompletedTask;
    }
}

