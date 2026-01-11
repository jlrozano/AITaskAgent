using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using Microsoft.Extensions.Logging;

namespace AITaskAgent.Core.Execution.Middlewares;

/// <summary>
/// Middleware that enforces timeout for step execution.
/// Runs after RetryMiddleware (so timeout applies per-attempt).
/// </summary>
internal sealed class TimeoutMiddleware(
    ILogger<TimeoutMiddleware> logger,
    TimeSpan? defaultTimeout = null) : IPipelineMiddleware
{
    private readonly ILogger<TimeoutMiddleware> _logger = logger;
    private readonly TimeSpan _defaultTimeout = defaultTimeout ?? TimeSpan.FromMinutes(1);

    public async Task<IStepResult> InvokeAsync(
        IStep step,
        IStepResult input,
        PipelineContext context,
        Func<CancellationToken, Task<IStepResult>> next,
        CancellationToken cancellationToken)
    {
        var timeout = step.Timeout ?? _defaultTimeout;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            return await next(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Timeout occurred (not external cancellation)
            _logger.LogWarning("Step {StepName} timed out after {Timeout}", step.Name, timeout);
            return ErrorStepResult.FromMessage(
                step,
                $"Step {step.Name} timed out after {timeout}");
        }
    }
}

