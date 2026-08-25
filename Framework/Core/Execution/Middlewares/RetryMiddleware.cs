using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Observability.Events;
using AITaskAgent.Support.JSON;
using Microsoft.Extensions.Logging;

namespace AITaskAgent.Core.Execution.Middlewares;

/// <summary>
/// Middleware that handles retry logic with structural and semantic validation.
/// Runs after TimeoutMiddleware. Note: Attempt tracking is now managed by StepBase, not this middleware.
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
        int attempt = 1;
        while (attempt <= step.MaxRetries)
        {
            // Store current attempt in context for observability middelware
            context.Metadata["__current_attempt__"] = attempt;

            try
            {
                result = await step.ExecuteAsync(input, context, attempt, result, cancellationToken);
            }
            catch (Exception ex)
            {
                // Exceptions are not retried - they are fatal errors
                _logger.LogError(ex, "Step {StepName} threw exception on attempt {Attempt}", step.Name, attempt);

                ErrorStepResult errResult = ErrorStepResult.FromException(step, ex);

                if (step is IFallbackable fallbackable && fallbackable.SwitchToNextFallback(errResult, attempt))
                {
                    attempt = 1;
                    result = null;
                    continue;
                }

                await step.FinalizeAsync(errResult, context, cancellationToken);
                return errResult;
            }

            // CASE 1: Step returned explicit error
            if (result.HasError)
            {
                // CRITICAL: If error contains an exception, it's likely a system failure / bug. DO NOT RETRY.
                if (result.Error?.OriginalException != null)
                {
                    if (step is IFallbackable fallbackable && fallbackable.SwitchToNextFallback(result, attempt))
                    {
                        attempt = 1;
                        result = null;
                        continue;
                    }
                    await step.FinalizeAsync(result, context, cancellationToken);
                    return result;
                }
                // If error has NO exception, it's likely a logic/parsing error (e.g. LLM invalid JSON).
                // Retry to allow LLM to self-correct with the error message.
                _logger.LogDebug("Step {StepName} returned recoverable error on attempt {Attempt}/{MaxRetries}: {@Error}",
                    step.Name, attempt, step.MaxRetries, result.Error);

                Base.Metrics.StepRetries.Add(1,
                    new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.StepName, step.Name));
                attempt++;
                continue; // Retry with feedback (StepBase tracks the error)
            }

            // CASE 2: Structural validation (IStepResult.ValidateAsync)
            (bool structValid, string? structError) = await result.ValidateAsync(context);
            if (!structValid)
            {
                _logger.LogDebug("Structural validation failed on attempt {Attempt}/{MaxRetries}: {Error}",
                        attempt, step.MaxRetries, structError);
                await context.SendEventAsync(new StepValidationEvent
                {
                    StepName = step.Name,
                    StepPath = context.CurrentStepPath,
                    CorrelationId = context.CorrelationId,
                    Timestamp = DateTimeOffset.UtcNow,
                    SchemaVersion = "1.0",
                    IsValid = false,
                    ValidationError = structError ?? "Structural validation failed",
                    ValidationType = AITaskAgentConstants.ValidationTypes.Structural,
                    AttemptNumber = attempt
                }, cancellationToken);
                result.Error = new StepError
                {
                    Message = structError ?? "Structural validation failed",
                };
                Base.Metrics.StepRetries.Add(1,
                    new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.StepName, step.Name));
                attempt++;
                continue; // Retry
            }

            // CASE 3: Semantic validation (IStep.ValidateAsync)
            (bool semValid, string? semError) = await step.ValidateAsync(result, context, cancellationToken);
            if (!semValid)
            {
                _logger.LogDebug("Semantic validation failed on attempt {Attempt}/{MaxRetries}: {Error}",
                    attempt, step.MaxRetries, semError);
                await context.SendEventAsync(new StepValidationEvent
                {
                    StepName = step.Name,
                    StepPath = context.CurrentStepPath,
                    CorrelationId = context.CorrelationId,
                    Timestamp = DateTimeOffset.UtcNow,
                    SchemaVersion = "1.0",
                    IsValid = false,
                    ValidationError = semError ?? "Semantic validation failed",
                    ValidationType = AITaskAgentConstants.ValidationTypes.Semantic,
                    AttemptNumber = attempt
                }, cancellationToken);
                result.Error = new StepError
                {
                    Message = semError ?? "Semantic validation failed",
                };
                Base.Metrics.StepRetries.Add(1,
                    new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.StepName, step.Name));
                attempt++;
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
