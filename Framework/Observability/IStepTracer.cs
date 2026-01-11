namespace AITaskAgent.Observability;

/// <summary>
/// Tracer interface for distributed tracing of pipeline step execution.
/// Receives trace events that can be used to build execution spans for observability systems.
/// </summary>
public interface IStepTracer
{
    /// <summary>
    /// Called when a step emits a trace event.
    /// </summary>
    /// <param name="evt">The trace event containing span information.</param>
    Task OnTraceEventAsync(StepTraceEvent evt);
}

