using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    // (correlationId, channel, capacity): null correlationId = global subscriber (receives all
    // events). Capacity is tracked alongside the channel because BoundedChannelFullMode.DropOldest
    // never reports a drop through TryWrite - it silently evicts the oldest buffered event and
    // still returns true - so detecting a drop requires checking Reader.Count against capacity
    // before writing.
    private readonly List<(string? CorrelationId, Channel<IProgressEvent> Channel, int Capacity)> _subscribers = [];

    // Background consumer tasks created by Subscribe(Func<>). Awaited in DisposeAsync to
    // ensure all buffered events have been delivered before the channel is torn down.
    private readonly List<Task> _backgroundLoops = [];
    private readonly Lock _subscribersLock = new();

    /// <summary>
    /// Creates a new EventChannel with simplified parameters, independent of DI.
    /// </summary>
    public EventChannel(
        LogLevel eventLogLevel = LogLevel.Information,
        ILogger<EventChannel>? logger = null)
    {
        _logger = logger ?? NullLogger<EventChannel>.Instance;
        _options = new EventChannelOptions
        {
            EventLogLevel = eventLogLevel
        };
    }

    public Task SendAsync<TEvent>(TEvent progressEvent, CancellationToken cancellationToken = default)
        where TEvent : IProgressEvent
    {
        ArgumentNullException.ThrowIfNull(progressEvent);

        // 1. Automatic structured logging (configurable level) - always log, even suppressed
        LogEvent(progressEvent);

        // 2. Skip sending to user-facing subscribers if suppressed
        if (progressEvent.SuppressFromUser)
        {
            _logger.LogDebug("Event {EventType} suppressed from user subscribers", progressEvent.EventType);
            return Task.CompletedTask;
        }

        // 3. Route event to matching subscribers:
        //    - Global subscribers (correlationId == null) receive all events
        //    - Filtered subscribers only receive events matching their correlationId
        List<(string? CorrelationId, Channel<IProgressEvent> Channel, int Capacity)> subscribersCopy;
        lock (_subscribersLock)
        {
            subscribersCopy = [.. _subscribers];
        }

        var eventCorrelationId = progressEvent.CorrelationId;

        foreach (var (correlationId, channel, capacity) in subscribersCopy)
        {
            // Skip if subscriber has a filter that doesn't match this event
            if (correlationId != null && correlationId != eventCorrelationId)
                continue;

            // BoundedChannelFullMode.DropOldest silently evicts the oldest buffered event to make
            // room and still reports success from TryWrite below - check fullness up front so the
            // drop is at least observable instead of vanishing without a trace.
            if (channel.Reader.CanCount && channel.Reader.Count >= capacity)
            {
                _logger.LogWarning(
                    "Subscriber channel full (capacity {Capacity}), dropping oldest buffered event to make room for {EventType}",
                    capacity, progressEvent.EventType);
            }

            try
            {
                if (!channel.Writer.TryWrite(progressEvent))
                {
                    _logger.LogWarning(
                        "Subscriber channel full, event {EventType} dropped",
                        progressEvent.EventType);
                }
            }
            catch (ChannelClosedException)
            {
                _logger.LogDebug("Subscriber channel closed, removing");

                // A closed writer never reopens: without removing it here, every future SendAsync
                // keeps retrying (and re-catching) against a dead subscriber for the rest of the
                // channel's lifetime.
                lock (_subscribersLock)
                {
                    _subscribers.RemoveAll(s => s.Channel == channel);
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Subscribes a Func handler. Uses an internal Bounded Channel for buffering and decoupling.
    /// Events are written instantly in SendAsync (non-blocking), and processed sequentially
    /// in a background loop. Dispose the returned IDisposable to unsubscribe and complete.
    /// </summary>
    public IDisposable Subscribe(
        Func<IProgressEvent, CancellationToken, Task> handler,
        string? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        // 1. Create a bounded channel for this subscription. Capacity comes from
        //    EventChannelOptions.ChannelCapacity instead of a hardcoded value, so the option is
        //    actually connected to the channel it documents itself as controlling.
        var capacity = _options.ChannelCapacity;
        var subscriberChannel = Channel.CreateBounded<IProgressEvent>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });

        // 2. Background consumer: drains the channel calling the handler.
        //    Tracked in _backgroundLoops so DisposeAsync can await it (otherwise
        //    late-stage events buffered just before disposal would be dropped).
        var consumerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (IProgressEvent evt in subscriberChannel.Reader.ReadAllAsync())
                {
                    try
                    {
                        await handler(evt, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error executing subscription handler for event {EventType}", evt.EventType);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in event subscription processing loop");
            }
        });

        // 3. Register BEFORE returning to avoid race condition with SendAsync.
        lock (_subscribersLock)
        {
            _subscribers.Add((correlationId, subscriberChannel, capacity));
            _backgroundLoops.Add(consumerTask);
        }

        // 4. Dispose: remove from list and TryComplete — loop drains buffer and exits cleanly.
        //    The consumer task deliberately stays in _backgroundLoops: completing the writer only
        //    *starts* the drain, and DisposeAsync is what awaits it. Removing the task here broke
        //    that guarantee - a subscriber that disposed its subscription and then the channel
        //    could observe the channel fully disposed while its last events were still buffered.
        return new HandlerSubscription(() =>
        {
            lock (_subscribersLock)
            {
                _subscribers.RemoveAll(s => s.Channel == subscriberChannel);
            }
            subscriberChannel.Writer.TryComplete();
        });
    }

    /// <summary>
    /// Creates a Channel-based subscription for use cases requiring buffering or async enumeration
    /// (e.g. SSE, WebSockets). Caller is responsible for consuming the Reader.
    /// Dispose to unsubscribe and complete the underlying channel.
    /// </summary>
    public IEventSubscription CreateSubscription(int capacity = 100, string? correlationId = null)
    {
        Channel<IProgressEvent> subscriberChannel = Channel.CreateBounded<IProgressEvent>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });

        lock (_subscribersLock)
        {
            _subscribers.Add((correlationId, subscriberChannel, capacity));
        }

        return new EventSubscription(subscriberChannel.Reader, () =>
        {
            lock (_subscribersLock)
            {
                _subscribers.RemoveAll(s => s.Channel == subscriberChannel);
            }
            subscriberChannel.Writer.TryComplete();
        });
    }

    private void LogEvent(IProgressEvent evt)
    {
        if (_options.EventLogLevel != LogLevel.None)
        {
            if (evt is Events.LlmResponseEvent llmEvt)
            {
                _logger.Log(_options.EventLogLevel,
                    "{EventType} from {StepName} | CorrelationId: {CorrelationId} | IsThinking: {IsThinking} | FinishReason: {FinishReason} | Content: {Content} | Tokens: {Tokens}",
                    evt.EventType, evt.StepName, evt.CorrelationId, llmEvt.IsThinking, llmEvt.FinishReason, llmEvt.Content, llmEvt.TokensUsed);
            }
            else
            {
                _logger.Log(_options.EventLogLevel,
                    "{EventType} from {StepName} | CorrelationId: {CorrelationId}",
                    evt.EventType, evt.StepName, evt.CorrelationId);
            }
        }
    }

    /// <summary>
    /// Disposes the event channel: completes all buffered active subscription channels
    /// and AWAITS the background consumer loops to drain so late-stage events
    /// (step_completed, pipeline_completed) are delivered before the channel is torn down.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        List<Task> loopsToAwait;
        lock (_subscribersLock)
        {
            foreach (var (_, channel, _) in _subscribers)
            {
                channel.Writer.TryComplete();
            }
            _subscribers.Clear();
            loopsToAwait = [.. _backgroundLoops];
            _backgroundLoops.Clear();
        }

        if (loopsToAwait.Count > 0)
        {
            try
            {
                await Task.WhenAll(loopsToAwait).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error awaiting subscription consumer loops during dispose");
            }
        }
    }
}

/// <summary>
/// Minimal IDisposable returned by EventChannel.Subscribe(Func&lt;&gt;).
/// Calling Dispose removes the handler from the EventChannel.
/// </summary>
file sealed class HandlerSubscription(Action unsubscribe) : IDisposable
{
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            unsubscribe();
        }
    }
}


