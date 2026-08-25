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

        try
        {
            return await next(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Timeout occurred (not external cancellation)
            _logger.LogWarning("Step {StepName} timed out after {Timeout}", step.Name, timeout.Value);

            // Emit step timeout event
            await context.SendEventAsync(new StepTimeoutEvent
            {
                StepName = step.Name,
                StepPath = context.CurrentStepPath,
                ConfiguredTimeout = timeout.Value,
                CorrelationId = context.CorrelationId,
                SchemaVersion = "1.0"
            }, cancellationToken);

            return ErrorStepResult.FromMessage(
                step,
                $"Step {step.Name} timed out after {timeout.Value}");
        }
    }
}
