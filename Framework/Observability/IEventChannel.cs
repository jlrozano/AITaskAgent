using System.Threading.Channels;

namespace AITaskAgent.Observability;

/// <summary>
/// Singleton channel for sending real-time progress events to observers.
/// Supports concurrent pipeline executions via CorrelationId-based routing:
/// each subscription can filter to a specific pipeline execution.
/// </summary>
public interface IEventChannel
{
    /// <summary>
    /// Sends a progress event. Routes to subscribers matching the event's CorrelationId
    /// and to global subscribers (correlationId = null).
    /// </summary>
    Task SendAsync<TEvent>(TEvent progressEvent, CancellationToken cancellationToken = default)
        where TEvent : IProgressEvent;

    /// <summary>
    /// Subscribes a handler backed by an internal buffered Channel.
    /// Events are written non-blocking in SendAsync; the handler runs in a background loop.
    /// </summary>
    /// <param name="handler">Async handler invoked per event.</param>
    /// <param name="correlationId">
    /// If set, only events whose CorrelationId matches are delivered.
    /// If null, all events are delivered (global subscription).
    /// </param>
    /// <returns>Disposable that unsubscribes and drains the handler when disposed.</returns>
    IDisposable Subscribe(
        Func<IProgressEvent, CancellationToken, Task> handler,
        string? correlationId = null);

    /// <summary>
    /// Creates a Channel-based subscription exposing a ChannelReader directly.
    /// For SSE/WebSocket use cases needing async enumeration.
    /// Dispose to unsubscribe and complete the underlying channel.
    /// </summary>
    /// <param name="correlationId">Optional filter. See Subscribe for semantics.</param>
    IEventSubscription CreateSubscription(int capacity = 100, string? correlationId = null);
}
