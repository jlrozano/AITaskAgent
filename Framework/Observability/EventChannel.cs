using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace AITaskAgent.Observability;

/// <summary>
/// Default implementation of IEventChannel using System.Threading.Channels.
/// Observers subscribe and receive events asynchronously through a Channel.
/// </summary>
public sealed class EventChannel : IEventChannel, IAsyncDisposable
{
    private readonly ILogger<EventChannel> _logger;
    private readonly EventChannelOptions _options;
    private readonly List<ChannelWriter<IProgressEvent>> _subscribers = [];
    private readonly Lock _subscribersLock = new();

    /// <summary>
    /// Creates a new EventChannel with the specified options.
    /// </summary>
    public EventChannel(
        ILogger<EventChannel> logger,
        IOptions<EventChannelOptions> options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _options = options?.Value ?? new EventChannelOptions();
    }

    /// <summary>
    /// Sends an event to all subscribers and logs it automatically.
    /// </summary>
    public async Task SendAsync<TEvent>(TEvent progressEvent, CancellationToken cancellationToken = default)
        where TEvent : IProgressEvent
    {
        ArgumentNullException.ThrowIfNull(progressEvent);

        // 1. Automatic structured logging (configurable level)
        LogEvent(progressEvent);

        // 2. Send to all subscriber channels asynchronously
        List<ChannelWriter<IProgressEvent>> subscribersCopy;
        lock (_subscribersLock)
        {
            subscribersCopy = [.. _subscribers];
        }

        foreach (var subscriber in subscribersCopy)
        {
            try
            {
                // TryWrite is non-blocking; if channel is full, event is dropped
                if (!subscriber.TryWrite(progressEvent))
                {
                    _logger.LogDebug(
                        "Subscriber channel full, event {EventType} dropped",
                        progressEvent.EventType);
                }
            }
            catch (ChannelClosedException)
            {
                // Subscriber closed, will be cleaned up
                _logger.LogDebug("Subscriber channel closed, removing");
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Subscribes to receive events. Returns a ChannelReader for async enumeration.
    /// </summary>
    /// <param name="capacity">Buffer capacity for this subscriber.</param>
    /// <returns>ChannelReader to consume events asynchronously.</returns>
    public ChannelReader<IProgressEvent> Subscribe(int capacity = 100)
    {
        var subscriberChannel = Channel.CreateBounded<IProgressEvent>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });

        lock (_subscribersLock)
        {
            _subscribers.Add(subscriberChannel.Writer);
        }

        return subscriberChannel.Reader;
    }

    /// <summary>
    /// Creates a subscription that can be disposed to unsubscribe.
    /// </summary>
    public IEventSubscription CreateSubscription(int capacity = 100)
    {
        var subscriberChannel = Channel.CreateBounded<IProgressEvent>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });

        lock (_subscribersLock)
        {
            _subscribers.Add(subscriberChannel.Writer);
        }

        return new EventSubscription(subscriberChannel.Reader, () =>
        {
            lock (_subscribersLock)
            {
                _subscribers.Remove(subscriberChannel.Writer);
            }
            subscriberChannel.Writer.TryComplete();
        });
    }

    private void LogEvent(IProgressEvent evt)
    {
        if (_options.EventLogLevel != LogLevel.None)
        {
            _logger.Log(_options.EventLogLevel,
                "{EventType} from {StepName} | CorrelationId: {CorrelationId}",
                evt.EventType, evt.StepName, evt.CorrelationId);
        }
    }

    /// <summary>
    /// Disposes the channel and completes all subscribers.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (_subscribersLock)
        {
            foreach (var subscriber in _subscribers)
            {
                subscriber.TryComplete();
            }
            _subscribers.Clear();
        }

        await Task.CompletedTask;
    }
}

