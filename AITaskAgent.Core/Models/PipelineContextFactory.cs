using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using System.Collections.Concurrent;

namespace AITaskAgent.Core.Models;

/// <summary>
/// Creates the context a pipeline runs on when the caller does not supply one.
/// </summary>
/// <remarks>
/// Registered as a singleton; constructing it publishes the instance the static
/// <see cref="Create(IEnumerable{Func{IProgressEvent, CancellationToken, Task}}?)"/> uses. Extensions
/// that need a richer context derive from it and override <see cref="CreateContext"/> (the LLM
/// extension registers <c>LlmPipelineContextFactory</c>), so the pipeline's default context has the
/// type their steps require without the core knowing about it.
/// </remarks>
public class PipelineContextFactory
{
    private readonly IEventChannel? _eventChannel;
    private readonly IServiceProvider _serviceProvider;
    private readonly IPipelineContextStorage _storage;
    private static PipelineContextFactory? _instance;
    public PipelineContextFactory(IEventChannel? eventChannel, IServiceProvider serviceProvider, IPipelineContextStorage storage)
    {
        _eventChannel = eventChannel;
        _serviceProvider = serviceProvider;
        _storage = storage;
        _instance = this;
    }

    /// <summary>Service provider the factory was built with.</summary>
    protected IServiceProvider ServiceProvider => _serviceProvider;

    /// <summary>
    /// Creates a context through the registered factory, with a dedicated event channel when
    /// <paramref name="subscribers"/> are given.
    /// </summary>
    public static PipelineContext Create(
        IEnumerable<Func<IProgressEvent, CancellationToken, Task>>? subscribers = null)
    {
        var instance = RequireInstance();
        return instance.CreateContext(instance.ResolveEventChannel(subscribers));
    }

    /// <summary>
    /// Creates a context through this factory instance (the static <see cref="Create"/> uses the last
    /// factory built in the process). With <paramref name="subscribers"/>, the context gets a
    /// dedicated event channel.
    /// </summary>
    public PipelineContext NewContext(
        IEnumerable<Func<IProgressEvent, CancellationToken, Task>>? subscribers = null)
        => CreateContext(ResolveEventChannel(subscribers));

    /// <summary>
    /// Builds the context for a run. Derived factories return their own context type.
    /// </summary>
    protected virtual PipelineContext CreateContext(IEventChannel? eventChannel) =>
        new(eventChannel, _serviceProvider);

    /// <summary>The registered factory; throws when DI has not built it yet.</summary>
    private static PipelineContextFactory RequireInstance() =>
        _instance ?? throw new InvalidOperationException("PipelineContextFactory is not initialized. Ensure it is registered in the service container.");

    /// <summary>
    /// The channel a new context gets: the registered one, or a dedicated channel when
    /// <paramref name="subscribers"/> contains at least one handler.
    /// </summary>
    private IEventChannel? ResolveEventChannel(
        IEnumerable<Func<IProgressEvent, CancellationToken, Task>>? subscribers)
    {
        var eventChannel = _eventChannel;

        if (subscribers != null)
        {
            // Without registered options the fallback is ObservabilityOptions' own defaults, so DI and
            // non-DI paths agree on the event log level and the channel capacity.
            var observability = GetAgentOptions()?.Observability ?? new AITaskAgent.Configuration.ObservabilityOptions();
            var logger = _serviceProvider.GetService<ILogger<EventChannel>>();
            var localChannel = new EventChannel(
                new EventChannelOptions
                {
                    EventLogLevel = observability.EventLogLevel,
                    ChannelCapacity = observability.EventChannelCapacity
                },
                logger);
            var hasSubscribers = false;

            foreach (var handler in subscribers)
            {
                localChannel.Subscribe(handler);
                hasSubscribers = true;
            }

            if (hasSubscribers)
            {
                eventChannel = localChannel;
            }
        }

        return eventChannel;
    }

    /// <summary>The registered agent options, or null when none are registered.</summary>
    private AITaskAgent.Configuration.AITaskAgentOptions? GetAgentOptions() =>
        _serviceProvider
            .GetService<Microsoft.Extensions.Options.IOptions<AITaskAgent.Configuration.AITaskAgentOptions>>()?.Value;

    public static async Task<PipelineContext> CreateFromId(string id)
    {
        return _instance == null
            ? throw new InvalidOperationException("PipelineContextFactory is not initialized. Ensure it is registered in the service container.")
            : (await _instance._storage.LoadAsync(id, CancellationToken.None))
            ?? throw new KeyNotFoundException();
    }
}
