namespace AITaskAgent.Observability;

/// <summary>
/// Null implementation of step tracer that discards all traces.
/// Use when console output is not desired.
/// </summary>
public sealed class NullStepTracer : IStepTracer
{
    public Task OnTraceEventAsync(StepTraceEvent evt)
    {
        // Discard - do nothing
        return Task.CompletedTask;
    }
}
