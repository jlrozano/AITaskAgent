namespace AITaskAgent.Observability;

/// <summary>
/// Tracer interface for distributed tracing of pipeline step execution.
/// Receives trace events that can be used to build execution spans for observability systems.
/// </summary>
/// <remarks>
/// The framework never calls <see cref="OnTraceEventAsync"/> and never registers an implementation,
/// so registering one has no effect. Use the <c>IEventChannel</c> events or the
/// <c>ActivitySource</c> traces instead.
/// </remarks>
[Obsolete("Not invoked by the framework; will be removed in a release with breaking changes. Use IEventChannel events or ActivitySource traces instead.")]
public interface IStepTracer
{
    /// <summary>
    /// Called when a step emits a trace event.
    /// </summary>
    /// <param name="evt">The trace event containing span information.</param>
    Task OnTraceEventAsync(StepTraceEvent evt);
}

