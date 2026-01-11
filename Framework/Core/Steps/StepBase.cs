using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Logging;
using NJsonSchema;
using System.Diagnostics;

namespace AITaskAgent.Core.Steps;

/// <summary>
/// Base class for all steps providing observability enrichment hooks and retry state tracking.
/// Retry logic is handled by RetryMiddleware, not by this class.
/// Uses Template Method pattern for enrichment hooks.
/// </summary>
public abstract class StepBase(string name, Type inputType, Type outputType) : IStep, IEnrichableStep
{
    private ILogger? _logger;
    private readonly Lazy<StepResultFactory.StepActivatorInfo> _activatorInfo =
       new(() => StepResultFactory.GetStepActivatorInfo(outputType));

    public string Name { get; } = name;
    public int MaxRetries { get; set; } = 3;
    public Type InputType { get; protected set; } = inputType;
    public Type OutputType { get; protected set; } = outputType;
    protected ILogger Logger => _logger ??= Pipeline.LoggerFactory.CreateLogger(GetType());

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
        // Default: no additional tags
    }

    /// <summary>
    /// Hook to add OpenTelemetry tags after execution.
    /// Override in derived classes to add result-specific tags (e.g., tokens used, cost).
    /// </summary>
    protected internal virtual void EnrichActivityAfter(Activity? activity, IStepResult result, PipelineContext context)
    {
        // Default: no additional tags
    }

    /// <summary>
    /// Hook to enrich the started event with step-specific data.
    /// Override in derived classes to add AdditionalData before execution begins.
    /// </summary>
    protected virtual StepStartedEvent EnrichStartedEvent(StepStartedEvent baseEvent, IStepResult input, PipelineContext context)
    {
        return baseEvent;
    }

    /// <summary>
    /// Hook to enrich the completed event with step-specific data.
    /// Override in derived classes to add AdditionalData (e.g., tokens, cost).
    /// </summary>
    protected virtual IStepCompletedEvent EnrichCompletedEvent(IStepCompletedEvent baseEvent, IStepResult result, PipelineContext context)
    {
        return baseEvent;
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
    /// </summary>
    protected virtual Task<(bool IsValid, string? Error)> Validate(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
    {
        return Task.FromResult((true, (string?)null));
    }

    // Explicit interface implementations
    void IEnrichableStep.EnrichActivityBefore(Activity? activity, IStepResult input, PipelineContext context)
        => EnrichActivityBefore(activity, input, context);

    void IEnrichableStep.EnrichActivityAfter(Activity? activity, IStepResult result, PipelineContext context)
        => EnrichActivityAfter(activity, result, context);

    StepStartedEvent IEnrichableStep.EnrichStartedEvent(StepStartedEvent baseEvent, IStepResult input, PipelineContext context)
        => EnrichStartedEvent(baseEvent, input, context);

    IStepCompletedEvent IEnrichableStep.EnrichCompletedEvent(IStepCompletedEvent baseEvent, IStepResult result, PipelineContext context)
        => EnrichCompletedEvent(baseEvent, result, context);

    Task IStep.FinalizeAsync(IStepResult input, PipelineContext context, CancellationToken cancellationToken) =>
        FinalizeAsync(input, context, cancellationToken);

    Task<IStepResult> IStep.ExecuteAsync(IStepResult input, PipelineContext context, int Attempt, IStepResult? lastStepResult, CancellationToken cancellationToken) =>
        ExecuteAsync(input, context, Attempt, lastStepResult, cancellationToken);

    Task<(bool IsValid, string? Error)> IStep.ValidateAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken) =>
        Validate(result, context, cancellationToken);
}
