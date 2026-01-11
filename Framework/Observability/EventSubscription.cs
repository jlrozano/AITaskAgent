using System.Threading.Channels;

namespace AITaskAgent.Observability;

/// <summary>
/// Default implementation of IEventSubscription.
/// </summary>
public sealed class EventSubscription(
    ChannelReader<IProgressEvent> reader,
    Action unsubscribe) : IEventSubscription
{
    /// <inheritdoc />
    public ChannelReader<IProgressEvent> Reader => reader;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        unsubscribe();
        return ValueTask.CompletedTask;
    }
}

