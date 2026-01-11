using System.Threading.Channels;

namespace AITaskAgent.Observability;

/// <summary>
/// Represents a subscription to the event channel that can be disposed.
/// </summary>
public interface IEventSubscription : IAsyncDisposable
{
    /// <summary>
    /// Reader for consuming events asynchronously.
    /// </summary>
    ChannelReader<IProgressEvent> Reader { get; }
}

