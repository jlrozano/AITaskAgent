using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Support.JSON;
using Microsoft.Extensions.Logging;

namespace AITaskAgent.Core.Execution.Middlewares;

/// <summary>
/// Middleware that handles retry logic with structural and semantic validation.
/// Runs after ObservabilityMiddleware and before TimeoutMiddleware.
/// Note: Attempt tracking is now managed by StepBase, not this middleware.
/// </summary>
internal sealed class RetryMiddleware(ILogger<RetryMiddleware> logger) : IPipelineMiddleware
{
    private readonly ILogger<RetryMiddleware> _logger = logger;

    public async Task<IStepResult> InvokeAsync(
        IStep step,
        IStepResult input,
        PipelineContext context,
        Func<CancellationToken, Task<IStepResult>> next,
        CancellationToken cancellationToken)
    {
        IStepResult? result = null;
        for (var attempt = 1; attempt <= step.MaxRetries; attempt++)
        {
            try
            {
                result = await step.ExecuteAsync(input, context, attempt, result, cancellationToken);
            }
            catch (Exception ex)
            {
                // Exceptions are not retried - they are fatal errors
                _logger.LogError(ex, "Step {StepName} threw exception on attempt {Attempt}", step.Name, attempt);
                await step.FinalizeAsync(ErrorStepResult.FromException(step, ex), context, cancellationToken);
                return ErrorStepResult.FromException(step, ex);
            }

            // CASE 1: Step returned explicit error
            if (result.HasError)
            {
                // CRITICAL: If error contains an exception, it's likely a system failure / bug. DO NOT RETRY.
                if (result.Error?.OriginalException != null)
                {
                    await step.FinalizeAsync(result, context, cancellationToken);
                    return result;
                }
                // If error has NO exception, it's likely a logic/parsing error (e.g. LLM invalid JSON).
                // Retry to allow LLM to self-correct with the error message.
                _logger.LogDebug("Step {StepName} returned recoverable error on attempt {Attempt}/{MaxRetries}: {@Error}",
                    step.Name, attempt, step.MaxRetries, result.Error);

                Base.Metrics.StepRetries.Add(1,
                    new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.StepName, step.Name));

                continue; // Retry with feedback (StepBase tracks the error)
            }

            // CASE 2: Structural validation (IStepResult.ValidateAsync)            
            (var structValid, var structError) = await result.ValidateAsync();
            if (!structValid)
            {
                _logger.LogDebug("Structural validation failed on attempt {Attempt}/{MaxRetries}: {Error}",
                        attempt, step.MaxRetries, structError);
                result.Error = new StepError
                {
                    Message = structError ?? "Structural validation failed",
                };
                Base.Metrics.StepRetries.Add(1,
                    new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.StepName, step.Name));
                continue; // Retry
            }


            // CASE 3: Semantic validation (IStep.ValidateAsync)
            (var semValid, var semError) = await step.ValidateAsync(result, context, cancellationToken);
            if (!semValid)
            {
                _logger.LogDebug("Semantic validation failed on attempt {Attempt}/{MaxRetries}: {Error}",
                    attempt, step.MaxRetries, semError);
                result.Error = new StepError
                {
                    Message = semError ?? "Semantic validation failed",
                };
                Base.Metrics.StepRetries.Add(1,
                    new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.StepName, step.Name));
                continue; // Retry  
            }

            // SUCCESS            
            await step.FinalizeAsync(result, context, cancellationToken);
            return result;
        }


        // CASE 4: All retries failed
        _logger.LogError(result?.Error?.OriginalException, "Validation failed after {MaxRetries} attempts. {@Error}",
                        step.MaxRetries, (result?.Error is StepError) ? new { result.Error.Message } : result?.Error?.WithoutExceptionProperties());

        var finalError = result ?? ErrorStepResult.FromMessage(
            step,
            $"Validation failed after {step.MaxRetries} attempts");

        await step.FinalizeAsync(finalError, context, cancellationToken);
        return finalError;
    }
}

