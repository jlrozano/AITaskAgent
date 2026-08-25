using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using System.Collections.Concurrent;

namespace AITaskAgent.Core.Models;

/// <summary>
/// Empty service provider implementation for default values.
/// </summary>
internal sealed class EmptyServiceProvider : IServiceProvider
{
    public static readonly EmptyServiceProvider Instance = new();
    public object? GetService(Type serviceType) => null;
}

/// <summary>
/// Context passed through the pipeline execution containing shared state and services.
/// Uses ConcurrentDictionary for thread-safety in parallel execution scenarios.
/// </summary>
public sealed record PipelineContext
{
    /// <summary>
    /// Creates a new PipelineContext with an event channel.
    /// </summary>
    public PipelineContext(IEventChannel? eventChannel = null)
        : this(eventChannel, EmptyServiceProvider.Instance)
    {
    }

    /// <summary>
    /// Creates a new PipelineContext with an event channel and service provider.
    /// </summary>
    public PipelineContext(IEventChannel? eventChannel, IServiceProvider serviceProvider)
    {
        EventChannel = eventChannel;
        ServiceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    /// <summary>Active conversation context.</summary>
    private string _currentPath = "";
    private readonly Stack<string> _lastPath = [];

    /// <summary>
    /// Event channel for sending observability events.
    /// </summary>
    /// <remarks>
    /// Not serialized. See <see cref="Runtime"/> for how a suspended context regains it.
    /// </remarks>
    [JsonIgnore]
    public IEventChannel? EventChannel { get; }

    /// <summary>
    /// Service provider for resolving dependencies during pipeline execution.
    /// Similar to HttpContext.RequestServices in ASP.NET Core.
    /// </summary>
    /// <remarks>Not serialized. See <see cref="Runtime"/>.</remarks>
    [JsonIgnore]
    public IServiceProvider ServiceProvider { get; }

    /// <summary>
    /// The engine executing this context. Assigned by the pipeline when a run starts, and
    /// propagated to branch clones.
    /// </summary>
    /// <remarks>
    /// Holding the engine directly is what lets a step log or run children without a DI container:
    /// asking <see cref="ServiceProvider"/> for the pipeline that is already executing you is
    /// service location, and it makes composite steps unusable outside one.
    ///
    /// Null until a run begins, and null again on a context just loaded from storage — this and the
    /// two references above are live objects, not data. Suspend/resume works by deserializing
    /// *onto an instance that already holds them*, so the persisted state supplies the data and the
    /// freshly built context supplies the wiring.
    /// </remarks>
    [JsonIgnore]
    public IPipelineRuntime? Runtime { get; internal set; }

    public ConversationContext Conversation { get; init; } = new();

    /// <summary>
    /// Unique identifier for this pipeline execution.
    /// Used for correlating logs and events across the entire flow.
    /// </summary>
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Metadata dictionary for custom data. Thread-safe for parallel access.
    /// </summary>
    public ConcurrentDictionary<string, object?> Metadata { get; init; } = [];

    /// <summary>
    /// Helper for JS access to metadata.
    /// </summary>
    public object? GetMetadata(string key) => Metadata.TryGetValue(key, out var val) ? val : null;

    /// <summary>
    /// Helper for JS access to set metadata.
    /// </summary>
    public void SetMetadata(string key, object? value) => Metadata[key] = value;

    /// <summary>
    /// Storage for step results. Thread-safe for parallel access.
    /// Key: step path (e.g., "pipeline/step1"). Value: step result.
    /// </summary>
    public ConcurrentDictionary<string, IStepResult> StepResults { get; init; } = [];

    /// <summary>
    /// Current path in the pipeline execution tree.
    /// Used for building step result keys. Internal setter for Pipeline use.
    /// </summary>
    public string CurrentStepPath { get => _currentPath; }

    internal void AddPathPart(string part)
    {
        _lastPath.Push(_currentPath);
        _currentPath = $"{(_currentPath == "" ? "" : $"{_currentPath}/")}{part}";
    }
    internal void RemovePathPart()
    {
        _currentPath = _lastPath.Count > 0 ? _lastPath.Pop() : "";
    }

    /// <summary>
    /// Creates a clone for parallel branch execution.
    /// Clones Conversation and path state to prevent interference between branches.
    /// Shares StepResults, Metadata, EventChannel (thread-safe).
    /// </summary>
    public PipelineContext CloneForBranch()
    {
        PipelineContext clonedContext = new(EventChannel, ServiceProvider)
        {
            Runtime = Runtime,
            Conversation = Conversation.Clone(),
            CorrelationId = CorrelationId,
            Metadata = Metadata,
            StepResults = StepResults,
            _currentPath = ""
        };

        // Clone the path stack by reversing the original order
        Stack<string> reversedStack = new(_lastPath.Reverse());

        // Create a new context with the cloned stack using reflection to bypass readonly constraint
        var finalContext = clonedContext with { };
        typeof(PipelineContext).GetField("_lastPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(finalContext, reversedStack);

        return finalContext;
    }

    /// <summary>
    /// Sends an event to the EventChannel if available.
    /// Returns true if event was sent, false if channel is null.
    /// Swallows exceptions to prevent pipeline failures from event delivery issues.
    /// </summary>
    public async Task<bool> SendEventAsync<TEvent>(TEvent eventData, CancellationToken cancellationToken = default)
        where TEvent : IProgressEvent
    {
        if (EventChannel == null)
        {
            return false;
        }

        try
        {
            await EventChannel.SendAsync(eventData, cancellationToken);
            return true;
        }
        catch
        {
            // Swallow event errors to prevent pipeline failure
            return false;
        }
    }

    /// <summary>
    /// Logger factory for anything running under this context.
    /// </summary>
    /// <remarks>
    /// Prefers the running engine's factory, which is never null. Falls back to the container only
    /// for a context that has not been handed to a pipeline yet, and to <c>NullLoggerFactory</c>
    /// when there is no container either — logging must never be the reason a step cannot run.
    /// </remarks>
    private ILoggerFactory LoggerFactory =>
        Runtime?.LoggerFactory
        ?? (ServiceProvider.GetService(typeof(ILoggerFactory)) as ILoggerFactory)
        ?? NullLoggerFactory.Instance;

    /// <summary>
    /// INTERNAL: Gets a logger for the specified type.
    /// </summary>
    internal ILogger<T> GetLogger<T>() => LoggerFactory.CreateLogger<T>();

    /// <summary>
    /// INTERNAL: Gets a logger for the specified type.
    /// </summary>
    internal ILogger GetLogger(Type type) => LoggerFactory.CreateLogger(type);

    /// <summary>
    /// INTERNAL: Executes a sub-pipeline with NextSteps support.
    /// Used by ParallelStep, LoopStep, and router steps.
    /// </summary>
    internal Task<IStepResult> ExecuteStepsWithNextStepsAsync(
        IReadOnlyList<IStep> steps,
        IStepResult currentResult,
        string pipelineName,
        CancellationToken cancellationToken,
        bool isBranchScope = false)
    {
        // The engine that started this run, not whatever the container happens to hold. Falling
        // back to the container covers a context built by hand and never handed to a pipeline.
        var runtime = Runtime ?? ServiceProvider.GetService(typeof(IPipelineRuntime)) as IPipelineRuntime;

        return runtime is not null
            ? runtime.ExecuteStepsWithNextStepsAsync(
                steps, currentResult, this, pipelineName, cancellationToken, isBranchScope)
            : throw new InvalidOperationException(
                "This context is not attached to a pipeline, so composite steps cannot run their " +
                "children. Execute it through IPipeline.ExecuteAsync rather than invoking the step " +
                "directly.");
    }
}

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

    public static PipelineContext Create(ConversationContext? conversation = null)
    {
        return Create(conversation, null);
    }

    public static PipelineContext Create(
        ConversationContext? conversation = null,
        IEnumerable<Func<IProgressEvent, CancellationToken, Task>>? subscribers = null)
    {
        if (_instance == null)
        {
            throw new InvalidOperationException("PipelineContextFactory is not initialized. Ensure it is registered in the service container.");
        }

        var eventChannel = _instance._eventChannel;

        if (subscribers != null)
        {
            var options = _instance._serviceProvider.GetService<Microsoft.Extensions.Options.IOptions<AITaskAgent.Configuration.AITaskAgentOptions>>()?.Value;
            var eventLogLevel = options?.Observability?.EventLogLevel ?? LogLevel.Information;
            var logger = _instance._serviceProvider.GetService<ILogger<EventChannel>>();
            var localChannel = new EventChannel(eventLogLevel, logger);
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

        return new(eventChannel, _instance._serviceProvider)
        {
            Conversation = conversation ?? new ConversationContext()
        };
    }
    public static async Task<PipelineContext> CreateFromId(string id)
    {
        return _instance == null
            ? throw new InvalidOperationException("PipelineContextFactory is not initialized. Ensure it is registered in the service container.")
            : (await _instance._storage.LoadAsync(id, CancellationToken.None))
            ?? throw new KeyNotFoundException();
    }
}
