namespace AITaskAgent.Observability;

/// <summary>
/// Channel for sending real-time progress events to observers.
/// Used for UI updates, SSE streams, WebSocket notifications, etc.
/// Separate from logs (ILogger), metrics (IMetricsCollector), and traces (OpenTelemetry).
/// </summary>
public interface IEventChannel
{
    /// <summary>
    /// Sends a progress event to all registered observers.
    /// </summary>
    /// <typeparam name="TEvent">Type of event implementing IProgressEvent.</typeparam>
    /// <param name="progressEvent">The event to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendAsync<TEvent>(TEvent progressEvent, CancellationToken cancellationToken = default)
        where TEvent : IProgressEvent;
}

