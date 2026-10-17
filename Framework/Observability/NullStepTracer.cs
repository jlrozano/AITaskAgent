namespace AITaskAgent.Observability;

/// <summary>
/// Null implementation of step tracer that discards all traces.
/// Use when console output is not desired.
/// </summary>
[Obsolete("Not invoked by the framework; will be removed in a release with breaking changes. Use IEventChannel events or ActivitySource traces instead.")]
public sealed class NullStepTracer : IStepTracer
{
    public Task OnTraceEventAsync(StepTraceEvent evt)
    {
        // Discard - do nothing
        return Task.CompletedTask;
    }
}
