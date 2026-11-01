using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NJsonSchema;
using System.Diagnostics;

namespace AITaskAgent.Core.Steps;

/// <summary>
/// Base class for all steps providing observability enrichment hooks and retry state tracking.
/// Retry logic is handled by RetryMiddleware, not by this class.
/// Uses Template Method pattern for enrichment hooks.
/// </summary>
public abstract class StepBase(string name, Type inputType, Type outputType) : IStep, IEnrichable, IResume, IFallbackable, IDisplayable
{
    private ILogger? _logger;
    private readonly List<IStepValidator> _validators = [];
    private readonly List<IResultValidator> _resultValidators = [];
    private Type _inputType = inputType;
    private Type _outputType = outputType;

    private Lazy<StepResultFactory.StepActivatorInfo> _activatorInfo = new(() => StepResultFactory.GetStepActivatorInfo(NormalizeResultType(outputType)));
    /// <summary>
    /// Step name. Settable from inside the framework (<c>protected internal</c>) so the YAML factory
    /// can give a step created through <see cref="IStepRegistry"/> the <c>id</c> it declares.
    /// </summary>
    public string Name { get; protected internal set; } = name;
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

    /// <inheritdoc cref="IStep.RequiredContext"/>
    public virtual Type? RequiredContext => null;

    /// <summary>Semantic validators registered for this step.</summary>
    public IReadOnlyList<IStepValidator> Validators => _validators;

    /// <summary>
    /// Adds a semantic validator to this step programmatically. Nothing in the framework calls it:
    /// the YAML pipeline factory does not attach validators, so a YAML file cannot declare them.
    /// Validators run in order; first failure short-circuits.
    /// </summary>
    public void AddValidator(IStepValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _validators.Add(validator);
    }

    /// <summary>
    /// Result validators attached to every result this step returns (YAML <c>resultValidators</c>). They run
    /// as part of <c>IStepResult.ValidateAsync</c>, in the structural slot of the retry loop.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<IResultValidator> ResultValidators => _resultValidators;

    /// <summary>Adds a result validator; see <see cref="ResultValidators"/>.</summary>
    public void AddResultValidator(IResultValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _resultValidators.Add(validator);
    }

    /// <summary>
    /// Generic hooks of this step (YAML <c>hooks:</c>); see <see cref="StepHooks"/>. Set once when the step is
    /// built and replaced as a whole, never mutated.
    /// </summary>
    [JsonIgnore]
    public StepHooks? Hooks { get; set; }

    /// <summary>
    /// Template of the text this step wants shown in its start event (<c>StepStartedEvent.Display</c>), rendered
    /// with the step's input, the context and the event's data; null (the default) means no text. See
    /// <c>DisplayTextRenderer</c> for the model. YAML: <c>display: { start: ... }</c>.
    /// </summary>
    [JsonIgnore]
    public virtual string? DisplayStart { get; set; }

    /// <summary>
    /// Template of the text this step wants shown in its completion event (<c>StepCompletedEvent.Display</c>);
    /// besides the start model it can use <c>output</c>, <c>success</c>, <c>error</c> and <c>durationMs</c>.
    /// YAML: <c>display: { end: ... }</c>.
    /// </summary>
    [JsonIgnore]
    public virtual string? DisplayEnd { get; set; }

    /// <summary>Not configurable from YAML (a live object cannot come from JSON); hooks replace it.</summary>
    [JsonIgnore]
    public virtual IEnrichable? Enrichable { get; set; }

    protected Type ValueType => _activatorInfo.Value.ValueType;
    /// <summary>
    /// JSON Schema of the output value, used by LLM steps for the response format. By default it is
    /// derived from <see cref="OutputType"/>; a step whose output is plain JSON (a
    /// <c>JsonStepResult</c> with a schema loaded at run time) overrides it to return that schema.
    /// </summary>
    protected virtual JsonSchema? Schema => _activatorInfo.Value.JsonSchema;
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
        RunEnrichHook(Hooks?.EnrichActivityBefore is { } hook ? () => hook(new EnrichActivityArgs(this, activity, input, null, context)) : null, nameof(StepHooks.EnrichActivityBefore));
    }

    /// <summary>
    /// Hook to add OpenTelemetry tags after execution.
    /// Override in derived classes to add result-specific tags (e.g., tokens used, cost).
    /// </summary>
    protected internal virtual void EnrichActivityAfter(Activity? activity, IStepResult result, PipelineContext context)
    {
        Enrichable?.EnrichActivityAfter(activity, result, context);
        RunEnrichHook(Hooks?.EnrichActivityAfter is { } hook ? () => hook(new EnrichActivityArgs(this, activity, null, result, context)) : null, nameof(StepHooks.EnrichActivityAfter));
    }

    /// <summary>
    /// Hook to enrich the started event with step-specific data.
    /// Override in derived classes to add AdditionalData before execution begins.
    /// </summary>
    protected virtual StepStartedEvent EnrichStartedEvent(StepStartedEvent baseEvent, IStepResult input, PipelineContext context)
    {
        StepStartedEvent started = Enrichable?.EnrichStartedEvent(baseEvent, input, context) ?? baseEvent;
        return RunEnrichHook(Hooks?.EnrichStarted, new EnrichStartedArgs(this, started, input, context), started, nameof(StepHooks.EnrichStarted));
    }

    /// <summary>
    /// Hook to enrich the completed event with step-specific data.
    /// Override in derived classes to add AdditionalData (e.g., tokens, cost).
    /// </summary>
    protected virtual IStepCompletedEvent EnrichCompletedEvent(IStepCompletedEvent baseEvent, IStepResult result, PipelineContext context)
    {
        IStepCompletedEvent completed = Enrichable?.EnrichCompletedEvent(baseEvent, result, context) ?? baseEvent;
        return RunEnrichHook(Hooks?.EnrichCompleted, new EnrichCompletedArgs(this, completed, result, context), completed, nameof(StepHooks.EnrichCompleted));
    }

    /// <summary>
    /// Enrichment hooks are observability: an exception is logged and swallowed because the observability
    /// middleware calls them outside its own try, and a failing tag must not kill the step.
    /// </summary>
    private void RunEnrichHook(Action? hook, string name)
    {
        if (hook == null)
        {
            return;
        }

        try
        {
            hook();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Enrichment hook '{Hook}' of step {StepName} failed and was ignored", name, Name);
        }
    }

    private TEvent RunEnrichHook<TArgs, TEvent>(Func<TArgs, TEvent>? hook, TArgs args, TEvent fallback, string name)
    {
        if (hook == null)
        {
            return fallback;
        }

        try
        {
            return hook(args) ?? fallback;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Enrichment hook '{Hook}' of step {StepName} failed and was ignored", name, Name);
            return fallback;
        }
    }

    /// <summary>
    /// Post-processing of the result of one attempt, after <c>ExecuteAsync</c> and before validation. The
    /// default dispatches <see cref="StepHooks.PostProcess"/>: its own value is the result unchanged (<c>next</c> of an around hook); a hook
    /// of the after form runs at the end, with that result in <see cref="PostProcessArgs.Result"/>, and what it leaves there is returned.
    /// An override that does not call <c>base</c> replaces the hook. Only called for results without error.
    /// </summary>
    protected virtual Task<IStepResult> PostProcessAsync(IStepResult input, IStepResult result, PipelineContext context, int attempt, CancellationToken cancellationToken)
    {
        return Hooks?.PostProcess is { } hook
            ? HookDispatch.AroundAsync(nameof(StepHooks.PostProcess), Name, hook,
                new PostProcessArgs(this, input, result, context, attempt, cancellationToken), () => Task.FromResult(result))
            : Task.FromResult(result);
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

    async Task IStep.FinalizeAsync(IStepResult input, PipelineContext context, CancellationToken cancellationToken)
    {
        await FinalizeAsync(input, context, cancellationToken);

        // Notification hook: dispatched here, not from the virtual, so no override can bypass it.
        if (Hooks?.OnFinalize is { } hook)
        {
            await HookDispatch.NotifyAsync(nameof(StepHooks.OnFinalize), Name,
                () => hook(new FinalizeArgs(this, input, context, cancellationToken)));
        }
    }

    async Task<IStepResult> IStep.ExecuteAsync(IStepResult input, PipelineContext context, int Attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
    {
        InitializeLogger(context);
        IStepResult result = await ExecuteAsync(input, context, Attempt, lastStepResult, cancellationToken);
        if (result.HasError)
        {
            return result;
        }

        IStepResult produced = result;
        result = await PostProcessAsync(input, result, context, Attempt, cancellationToken);

        // Only a result the hook replaced is checked: steps may legitimately return other result types today.
        if (!ReferenceEquals(produced, result) && OutputType != typeof(IStepResult) && !OutputType.IsInstanceOfType(result))
        {
            throw new StepHookException(nameof(StepHooks.PostProcess), Name,
                new InvalidOperationException($"it returned a {result.GetType().Name}, which is not assignable to the output type {OutputType.Name}."));
        }

        // Stamp (replace) the validators so the result carries the latest owner's list.
        if (result is StepResult stamped)
        {
            stamped.AttachResultValidators(_resultValidators);
        }

        return result;
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

    protected virtual bool SwitchToNextFallback(IStepResult error, PipelineContext context, int attempt)
    {
        return false;
    }

    bool IFallbackable.SwitchToNextFallback(IStepResult error, PipelineContext context, int attempt)
    {
        return SwitchToNextFallback(error, context, attempt);
    }
}
