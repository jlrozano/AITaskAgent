using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NJsonSchema;
using System.Diagnostics;

namespace AITaskAgent.Core.Steps;

/// <summary>
/// Base class for all steps providing observability enrichment hooks and retry state tracking.
/// Retry logic is handled by RetryMiddleware, not by this class.
/// Uses Template Method pattern for enrichment hooks.
/// </summary>
public abstract class StepBase(string name, Type inputType, Type outputType) : IStep, IEnrichable, IResume, IFallbackable
{
    private ILogger? _logger;
    private readonly List<IStepValidator> _validators = [];
    private Type _inputType = inputType;
    private Type _outputType = outputType;

    private Lazy<StepResultFactory.StepActivatorInfo> _activatorInfo = new(() => StepResultFactory.GetStepActivatorInfo(NormalizeResultType(outputType)));
    public string Name { get; protected set; } = name;
    public int MaxRetries { get; set; } = 3;
    public virtual IResume? Resume => this;
    public Type InputType
    {
        get => _inputType; protected internal set
        {
            ArgumentNullException.ThrowIfNull(value, nameof(InputType));
            _inputType = value;
        }
    }
    public Type OutputType
    {
        get => _outputType; protected internal set
        {
            ArgumentNullException.ThrowIfNull(value, nameof(OutputType));
            if (value != _outputType)
            {
                _activatorInfo = new(() => StepResultFactory.GetStepActivatorInfo(NormalizeResultType(value)));
                _outputType = value;
            }
        }
    }

    /// <summary>
    /// Logger for this step. Initialized on first access from context's service provider.
    /// </summary>
    protected ILogger Logger => _logger ??= Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>
    /// Initializes the logger from the context's service provider.
    /// Called automatically during ExecuteAsync.
    /// </summary>
    protected void InitializeLogger(PipelineContext context)
    {
        _logger ??= context.ServiceProvider
                .GetService<ILoggerFactory>()
                ?.CreateLogger(GetType()) ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>
    /// Optional timeout for this step. If null, uses Pipeline.DefaultStepTimeout.
    /// </summary>
    public virtual TimeSpan? Timeout { get; set; }

    /// <summary>Semantic validators registered for this step.</summary>
    public IReadOnlyList<IStepValidator> Validators => _validators;

    /// <summary>
    /// Adds a semantic validator to this step. Called by <see cref="AITaskAgent.YAML.Execution.YamlPipelineFactory"/>
    /// after deserialization, or programmatically. Validators run in order; first failure short-circuits.
    /// </summary>
    public void AddValidator(IStepValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _validators.Add(validator);
    }

    // ── YAML configuration properties ────────────────────────────────────────
    // These properties are set during YAML deserialization and resolved by
    // YamlPipelineFactory to their runtime equivalents (compiled types, IStepValidator).

    /// <summary>
    /// YAML-only. Schema name for the input type. Resolved to <see cref="InputType"/> by SchemaCompiler.
    /// </summary>
    public string? InputSchema { get; init; }

    /// <summary>
    /// YAML-only. Schema name for the output type. Resolved to <see cref="OutputType"/> by SchemaCompiler.
    /// </summary>
    public string? OutputSchema { get; init; }


    public IEnrichable? Enrichable { get; set; }

    protected Type ValueType => _activatorInfo.Value.ValueType;
    protected JsonSchema? Schema => _activatorInfo.Value.JsonSchema;
    protected IStepResult CreateResult(object? value, IStepError? error = null)
    {
        return (IStepResult)_activatorInfo.Value.CreateInstance(this, value, error);
    }

    protected IStepResult CreateErrorResult(string message, Exception? exception = null)
    {
        return CreateResult(null, new StepError()
        {
            Message = message,
            OriginalException = exception
        });
    }

    private static Type NormalizeResultType(Type resultType)
    {
        return resultType == typeof(IStepResult) || resultType.IsInterface || resultType.IsAbstract
            ? typeof(StepResult<object?>)
            : resultType;
    }


    /// <summary>
    /// Executes the step. Called by the middleware pipeline.
    /// Retry logic is handled by RetryMiddleware.
    /// </summary>
    protected abstract Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken);

    /// <summary>
    /// Hook to add OpenTelemetry tags before execution.
    /// Override in derived classes to add step-specific tags (e.g., LLM model, tools).
    /// </summary>
    protected internal virtual void EnrichActivityBefore(Activity? activity, IStepResult input, PipelineContext context)
    {
        Enrichable?.EnrichActivityBefore(activity, input, context);
    }

    /// <summary>
    /// Hook to add OpenTelemetry tags after execution.
    /// Override in derived classes to add result-specific tags (e.g., tokens used, cost).
    /// </summary>
    protected internal virtual void EnrichActivityAfter(Activity? activity, IStepResult result, PipelineContext context)
    {
        Enrichable?.EnrichActivityAfter(activity, result, context);
    }

    /// <summary>
    /// Hook to enrich the started event with step-specific data.
    /// Override in derived classes to add AdditionalData before execution begins.
    /// </summary>
    protected virtual StepStartedEvent EnrichStartedEvent(StepStartedEvent baseEvent, IStepResult input, PipelineContext context)
    {
        return Enrichable?.EnrichStartedEvent(baseEvent, input, context) ?? baseEvent;
    }

    /// <summary>
    /// Hook to enrich the completed event with step-specific data.
    /// Override in derived classes to add AdditionalData (e.g., tokens, cost).
    /// </summary>
    protected virtual IStepCompletedEvent EnrichCompletedEvent(IStepCompletedEvent baseEvent, IStepResult result, PipelineContext context)
    {
        return Enrichable?.EnrichCompletedEvent(baseEvent, result, context) ?? baseEvent;
    }

    /// <summary>
    /// Finalization hook called once after all retries complete (success or final failure).
    /// Override in derived classes for cleanup logic (e.g., conversation cleanup in BaseLlmStep).
    /// Note: Retry state (Attempt, LastErrorResult) is reset automatically after this method returns.
    /// </summary>
    protected virtual Task FinalizeAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Asynchronously validates the specified step result within the given pipeline context.
    /// Override in derived classes to add step-specific validation logic.
    /// The base implementation iterates all registered <see cref="IStepValidator"/> instances.
    /// </summary>
    protected virtual async Task<(bool IsValid, string? Error)> Validate(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
    {
        foreach (IStepValidator validator in _validators)
        {
            (bool isValid, string? error) = await validator.ValidateAsync(result, context, cancellationToken);
            if (!isValid)
            {
                return (false, error);
            }
        }
        return (true, null);
    }

    // Explicit interface implementations
    void IEnrichable.EnrichActivityBefore(Activity? activity, IStepResult input, PipelineContext context)
        => EnrichActivityBefore(activity, input, context);

    void IEnrichable.EnrichActivityAfter(Activity? activity, IStepResult result, PipelineContext context)
        => EnrichActivityAfter(activity, result, context);

    StepStartedEvent IEnrichable.EnrichStartedEvent(StepStartedEvent baseEvent, IStepResult input, PipelineContext context)
        => EnrichStartedEvent(baseEvent, input, context);

    IStepCompletedEvent IEnrichable.EnrichCompletedEvent(IStepCompletedEvent baseEvent, IStepResult result, PipelineContext context)
        => EnrichCompletedEvent(baseEvent, result, context);

    Task IStep.FinalizeAsync(IStepResult input, PipelineContext context, CancellationToken cancellationToken) =>
        FinalizeAsync(input, context, cancellationToken);

    Task<IStepResult> IStep.ExecuteAsync(IStepResult input, PipelineContext context, int Attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
    {
        InitializeLogger(context);
        return ExecuteAsync(input, context, Attempt, lastStepResult, cancellationToken);
    }

    Task<(bool IsValid, string? Error)> IStep.ValidateAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken) =>
        Validate(result, context, cancellationToken);

    Task<object?> IResume.SaveStateAsync(IStep callingStep) => SaveStateAsync();

    Task IResume.RestoreStateAsync(IStep step, object? state, object? hitlResponse) => RestoreStateAsync(state, hitlResponse);

    /// <summary>
    /// Hook to save step state when suspended.
    /// Override to return state artifacts needed when resuming later, or null if no local state needs preserving.
    /// </summary>
    protected virtual Task<object?> SaveStateAsync() => Task.FromResult<object?>(null);

    /// <summary>
    /// Hook to restore step state from suspension.
    /// Override to process internal state or inject hitl response natively.
    /// </summary>
    protected virtual Task RestoreStateAsync(object? state, object? hitlResponse) => Task.CompletedTask;

    protected virtual bool SwitchToNextFallback(IStepResult error, int attempt)
    {
        return false;
    }

    bool IFallbackable.SwitchToNextFallback(IStepResult error, int attempt)
    {
        return SwitchToNextFallback(error, attempt);
    }
}
