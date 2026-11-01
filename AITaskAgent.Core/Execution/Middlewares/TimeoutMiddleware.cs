using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Logging;

namespace AITaskAgent.Core.Execution.Middlewares;

/// <summary>
/// Middleware that enforces timeout for step execution.
/// Execution order in the chain: Observability → Timeout → Retry → Step.
/// Because Timeout wraps Retry, the timeout is GLOBAL across ALL retry attempts —
/// not per-attempt. With MaxRetries=N and Timeout=T, the maximum possible execution
/// time is T (not N×T).
/// </summary>
internal sealed class TimeoutMiddleware(
    ILogger<TimeoutMiddleware> logger,
    TimeSpan? defaultTimeout = null) : IPipelineMiddleware
{
    private readonly ILogger<TimeoutMiddleware> _logger = logger;
    private readonly TimeSpan? _defaultTimeout = defaultTimeout;

    public async Task<IStepResult> InvokeAsync(
        IStep step,
        IStepResult input,
        PipelineContext context,
        Func<CancellationToken, Task<IStepResult>> next,
        CancellationToken cancellationToken)
    {
        TimeSpan? timeout = step.Timeout ?? _defaultTimeout;

        // If no timeout configured, execute without timeout
        if (!timeout.HasValue)
        {
            return await next(cancellationToken);
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout.Value);

        IStepResult result;
        try
        {
            result = await next(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return await TimedOutAsync(step, context, timeout.Value, cancellationToken);
        }

        // The step can answer the expiry with an error result instead of an exception: RetryMiddleware, inside this one, turns the
        // OperationCanceledException into one, and a step that catches the cancellation itself (ScriptStep) returns a plain
        // message. With the timer fired and the caller not cancelled, whatever error comes back is the timeout.
        if (result.HasError && cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return await TimedOutAsync(step, context, timeout.Value, cancellationToken);
        }

        return result;
    }

    private async Task<IStepResult> TimedOutAsync(IStep step, PipelineContext context, TimeSpan timeout, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Step {StepName} timed out after {Timeout}", step.Name, timeout);

        // Emit step timeout event
        await context.SendEventAsync(new StepTimeoutEvent
        {
            StepName = step.Name,
            StepPath = context.CurrentStepPath,
            ConfiguredTimeout = timeout,
            CorrelationId = context.CorrelationId,
            SchemaVersion = "1.0"
        }, cancellationToken);

        // A TimeoutException (not just a message) so that whoever reads the error can tell a timeout from other failures.
        return ErrorStepResult.FromException(
            step,
            new TimeoutException($"Step {step.Name} exceeded its limit of {Math.Round(timeout.TotalSeconds):0} s"));
    }
}
