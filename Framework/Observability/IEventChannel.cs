using System.Threading.Channels;

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

    /// <summary>
    /// Subscribes to receive all events. Returns a channel reader for async enumeration.
    /// </summary>
    ChannelReader<IProgressEvent> Subscribe(int capacity = 100);

    /// <summary>
    /// Creates a subscription that can be disposed to unsubscribe.
    /// </summary>
    IEventSubscription CreateSubscription(int capacity = 100);
}
