using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Conversation.Storage;
using AITaskAgent.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AITaskAgent.LLM.Conversation.Context;

/// <summary>
/// Pipeline context for pipelines with LLM steps: the base context plus the conversation.
/// </summary>
/// <remarks>
/// LLM steps declare it through <see cref="IStep.RequiredContext"/>, and the pipeline refuses to
/// start when the context it was given is not one. A parallel branch gets a clone of the conversation
/// and its own <see cref="StepState"/>, so concurrent branches never write into each other.
/// A context storage that uses <see cref="PipelineContextSerializer"/> keeps the conversation
/// (<see cref="IPersistableContextState"/>): id, maximum tokens, messages, metadata and timestamps,
/// the same fields <see cref="FileConversationStorage"/> stores. Bookmarks and
/// <see cref="StepState"/> are not kept: they only matter while a step is running.
/// </remarks>
public record LlmPipelineContext : PipelineContext, IPersistableContextState
{
    private static readonly JsonSerializer ConversationSerializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        TypeNameHandling = TypeNameHandling.None,
        DateParseHandling = DateParseHandling.None
    });

    /// <summary>Creates a context with an optional event channel and no service provider.</summary>
    public LlmPipelineContext(IEventChannel? eventChannel = null)
        : base(eventChannel)
    {
    }

    /// <summary>Creates a context with an event channel and a service provider.</summary>
    public LlmPipelineContext(IEventChannel? eventChannel, IServiceProvider serviceProvider)
        : base(eventChannel, serviceProvider)
    {
    }

    /// <summary>Active conversation.</summary>
    public ConversationContext Conversation { get; init; } = new();

    /// <summary>
    /// Framework-internal scratch space for a step's own per-invocation state (e.g.
    /// <c>BaseLlmStep</c>'s pending conversation bookmark and fallback index) - keyed by
    /// <see cref="PipelineContext.CurrentStepPath"/> so sequential steps sharing a context never
    /// collide, and NOT carried over by <see cref="CloneForBranch"/> (unlike
    /// <see cref="PipelineContext.Metadata"/>, which is shared on purpose for cross-branch
    /// aggregation) so concurrent branches never collide either. Not for step authors' own data -
    /// use <see cref="PipelineContext.Metadata"/> for that.
    /// </summary>
    internal Dictionary<string, object> StepState { get; private init; } = [];

    /// <summary>
    /// Clones the base state (see <see cref="PipelineContext.CloneForBranch"/>), the conversation,
    /// and starts the branch with empty <see cref="StepState"/>.
    /// </summary>
    public override LlmPipelineContext CloneForBranch() =>
        (LlmPipelineContext)base.CloneForBranch() with
        {
            Conversation = Conversation.Clone(),
            StepState = []
        };

    /// <inheritdoc/>
    JToken? IPersistableContextState.SaveExtensionState()
    {
        ConversationSnapshot snapshot = new(
            ConversationId: Conversation.ConversationId,
            MaxTokens: Conversation.History.MaxTokens,
            Messages: [.. Conversation.History.Messages],
            Metadata: Conversation.Metadata,
            StartedAt: Conversation.StartedAt,
            LastActivityAt: Conversation.LastActivityAt);

        return new JObject { ["conversation"] = JToken.FromObject(snapshot, ConversationSerializer) };
    }

    /// <inheritdoc/>
    PipelineContext IPersistableContextState.RestoreExtensionState(JToken state)
    {
        ConversationSnapshot? snapshot = state["conversation"]?.ToObject<ConversationSnapshot>(ConversationSerializer);
        if (snapshot is null)
        {
            return this;
        }

        // The token counter, logger and options come from the fresh conversation this context was
        // built with in the resuming process; only the data comes from storage.
        ConversationContext conversation = new(
            snapshot.MaxTokens,
            Conversation.History.TokenCounter,
            ServiceProvider.GetService<ILogger<ConversationContext>>(),
            ServiceProvider.GetService<Microsoft.Extensions.Options.IOptions<AITaskAgent.Configuration.AITaskAgentOptions>>()?.Value.Conversation)
        {
            ConversationId = snapshot.ConversationId,
            Metadata = snapshot.Metadata ?? [],
            StartedAt = snapshot.StartedAt
        };
        conversation.History.AddMessages(snapshot.Messages ?? []);
        conversation.LastActivityAt = snapshot.LastActivityAt;

        return this with { Conversation = conversation };
    }

    /// <summary>
    /// Returns <paramref name="context"/> as an LLM context, or fails with a message naming
    /// <paramref name="requester"/> when the pipeline was started with a context of another type.
    /// </summary>
    public static LlmPipelineContext From(PipelineContext context, string? requester = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context as LlmPipelineContext
            ?? throw new InvalidOperationException(
                $"{(requester is null ? "This operation" : $"Step '{requester}'")} requires a " +
                $"{nameof(LlmPipelineContext)}, but the pipeline context is a {context.GetType().Name}. " +
                $"Create the context with {nameof(LlmPipelineContextFactory)}.Create or " +
                $"new {nameof(LlmPipelineContext)}(...).");
    }
}

/// <summary>
/// Access to the LLM side of a <see cref="PipelineContext"/>.
/// </summary>
public static class LlmPipelineContextExtensions
{
    /// <summary>
    /// The context as an <see cref="LlmPipelineContext"/>; throws a descriptive
    /// <see cref="InvalidOperationException"/> when it is not one.
    /// </summary>
    public static LlmPipelineContext AsLlmContext(this PipelineContext context, string? requester = null) =>
        LlmPipelineContext.From(context, requester);
}

/// <summary>
/// Context factory registered by the LLM extension: the pipeline's default context is an
/// <see cref="LlmPipelineContext"/> with a conversation built from the configured options.
/// </summary>
public class LlmPipelineContextFactory(
    IEventChannel? eventChannel,
    IServiceProvider serviceProvider,
    Core.Execution.IPipelineContextStorage storage)
    : PipelineContextFactory(eventChannel, serviceProvider, storage)
{
    /// <summary>
    /// Creates an LLM context, optionally around an existing <paramref name="conversation"/>, with a
    /// dedicated event channel when <paramref name="subscribers"/> are given.
    /// </summary>
    public static LlmPipelineContext Create(
        ConversationContext? conversation = null,
        IEnumerable<Func<IProgressEvent, CancellationToken, Task>>? subscribers = null)
    {
        var context = PipelineContextFactory.Create(subscribers);

        if (context is LlmPipelineContext llmContext)
        {
            return conversation is null ? llmContext : llmContext with { Conversation = conversation };
        }

        // The registered factory is the core one (no LLM registration): build the LLM context here.
        return new LlmPipelineContext(context.EventChannel, context.ServiceProvider)
        {
            Conversation = conversation ?? NewConversation(context.ServiceProvider)
        };
    }

    /// <inheritdoc/>
    protected override PipelineContext CreateContext(IEventChannel? eventChannel) =>
        new LlmPipelineContext(eventChannel, ServiceProvider)
        {
            Conversation = NewConversation(ServiceProvider)
        };

    /// <summary>
    /// Builds an LLM context whose conversation is <paramref name="conversationId"/> as the registered
    /// <see cref="IConversationStorage"/> has it, or a new, empty conversation with that id when the
    /// storage does not know it. It is the starting point for a run that continues a previous
    /// conversation: pass the context to the pipeline, and save <c>context.Conversation</c> back to
    /// the storage when the run ends.
    /// </summary>
    /// <param name="services">Provider with the framework services (<c>AddAITaskAgent()</c>).</param>
    /// <param name="conversationId">Id of the conversation to continue.</param>
    /// <param name="subscribers">Optional handlers for a dedicated event channel.</param>
    /// <param name="cancellationToken">Cancels the storage read.</param>
    public static async Task<LlmPipelineContext> CreateForConversationAsync(
        IServiceProvider services,
        string conversationId,
        IEnumerable<Func<IProgressEvent, CancellationToken, Task>>? subscribers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        PipelineContext created = services.GetRequiredService<PipelineContextFactory>().NewContext(subscribers);
        LlmPipelineContext context = created as LlmPipelineContext
            ?? new LlmPipelineContext(created.EventChannel, created.ServiceProvider) { Conversation = NewConversation(services) };

        IConversationStorage storage = services.GetRequiredService<IConversationStorage>();
        ConversationContext? stored = await storage.GetConversationAsync(conversationId, cancellationToken);

        return context with { Conversation = stored ?? NewConversation(services, conversationId) };
    }

    private static ConversationContext NewConversation(IServiceProvider serviceProvider, string conversationId)
    {
        AITaskAgent.Configuration.ConversationOptions? options = serviceProvider
            .GetService<Microsoft.Extensions.Options.IOptions<AITaskAgent.Configuration.AITaskAgentOptions>>()?.Value.Conversation;

        return new ConversationContext(
            options?.MaxTokens ?? 4000,
            logger: serviceProvider.GetService<ILogger<ConversationContext>>(),
            conversationOptions: options)
        {
            ConversationId = conversationId
        };
    }

    private static ConversationContext NewConversation(IServiceProvider serviceProvider) =>
        new(
            logger: serviceProvider.GetService<ILogger<ConversationContext>>(),
            conversationOptions: serviceProvider
                .GetService<Microsoft.Extensions.Options.IOptions<AITaskAgent.Configuration.AITaskAgentOptions>>()?.Value
                .Conversation);
}
