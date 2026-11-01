using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Base;
using AITaskAgent.Core.Models;
using AITaskAgent.Observability.Events;
using AITaskAgent.Support.Template;
using System.Diagnostics;

namespace AITaskAgent.Core.Execution.Middlewares;

/// <summary>
/// Middleware that provides observability for ALL steps.
/// Handles OpenTelemetry traces, events, and native metrics.
/// If step implements IEnrichableStep, calls enrichment methods.
/// Always runs first in the middleware chain.
/// </summary>
internal sealed class ObservabilityMiddleware : IPipelineMiddleware
{
    public async Task<IStepResult> InvokeAsync(
        IStep step,
        IStepResult input,
        PipelineContext context,
        Func<CancellationToken, Task<IStepResult>> next,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        // Check if step supports enrichment
        IEnrichable? enrichable = step as IEnrichable;

        using Activity? activity = Telemetry.Source.StartActivity(
            $"Step.{step.Name}",
            ActivityKind.Internal);
        activity?.SetTag(AITaskAgentConstants.TelemetryTags.StepName, step.Name);
        activity?.SetTag(AITaskAgentConstants.TelemetryTags.StepType, step.GetType().Name);
        activity?.SetTag(AITaskAgentConstants.TelemetryTags.CorrelationId, context.CorrelationId);

        // ENRICHMENT: Allow step to add custom tags before execution
        enrichable?.EnrichActivityBefore(activity, input, context);

        // Always the first attempt: RetryMiddleware runs the step directly instead of calling
        // next(), so its retry loop is nested inside this single call and cannot report back here.
        // Retries emit their own StepStartedEvent from RetryMiddleware.
        StepStartedEvent startedEvent = new()
        {
            StepName = step.Name,
            StepPath = context.CurrentStepPath,
            CorrelationId = context.CorrelationId,
            AttemptNumber = 1,
            SchemaVersion = "1.0"
        };
        StepStartedEvent enrichedStartedEvent = enrichable?.EnrichStartedEvent(startedEvent, input, context) ?? startedEvent;
        if (!enrichedStartedEvent.SuppressFromUser && enrichedStartedEvent.Display is null)
        {
            // After the enrichment, so the template sees the event's AdditionalData ("data"). Never throws.
            enrichedStartedEvent = enrichedStartedEvent with
            {
                Display = DisplayTextRenderer.StepStart(
                    step, input, context, enrichedStartedEvent.AttemptNumber, enrichedStartedEvent.StepPath,
                    enrichedStartedEvent.AdditionalData?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value))
            };
        }

        await context.SendEventAsync(enrichedStartedEvent, cancellationToken);

        IStepResult result;
        try
        {
            // Execute next middleware (or step itself)
            result = await next(cancellationToken);

            stopwatch.Stop();
            TimeSpan duration = stopwatch.Elapsed;

            // Update activity with result
            activity?.SetTag(AITaskAgentConstants.TelemetryTags.StepDurationMs, duration.TotalMilliseconds);
            activity?.SetTag(AITaskAgentConstants.TelemetryTags.StepSuccess, !result.HasError);
            activity?.SetStatus(result.HasError ? ActivityStatusCode.Error : ActivityStatusCode.Ok);

            // ENRICHMENT: Allow step to add result-specific tags
            enrichable?.EnrichActivityAfter(activity, result, context);

            // Record native metrics
            Metrics.StepExecutions.Add(1,
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.StepName, step.Name),
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.StepSuccess, !result.HasError));

            Metrics.StepDuration.Record(duration.TotalMilliseconds,
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.StepName, step.Name));

            if (result.HasError)
            {
                Metrics.StepErrors.Add(1,
                    new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.StepName, step.Name));
            }

            StepCompletedEvent completedEvent = new()
            {
                StepName = step.Name,
                StepPath = context.CurrentStepPath,
                Success = !result.HasError,
                Duration = duration,
                ErrorMessage = result.HasError ? result.Error?.Message : null,
                CorrelationId = context.CorrelationId,
                StepResult = result,
                SchemaVersion = "1.0"
            };

            // ENRICHMENT: Allow step to add custom data to event
            IStepCompletedEvent enrichedCompletedEvent = enrichable?.EnrichCompletedEvent(completedEvent, result, context) ?? completedEvent;
            if (enrichedCompletedEvent is StepCompletedEvent { SuppressFromUser: false, Display: null } displayable)
            {
                enrichedCompletedEvent = displayable with
                {
                    Display = DisplayTextRenderer.StepEnd(
                        step, input, result, context, displayable.StepPath,
                        displayable.Success, displayable.ErrorMessage, displayable.Duration, displayable.AdditionalData)
                };
            }

            await context.SendEventAsync(enrichedCompletedEvent, cancellationToken);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            TimeSpan duration = stopwatch.Elapsed;

            activity?.SetTag(AITaskAgentConstants.TelemetryTags.StepDurationMs, duration.TotalMilliseconds);
            activity?.SetTag(AITaskAgentConstants.TelemetryTags.StepSuccess, false);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            // Record native metrics for exception
            Metrics.StepExecutions.Add(1,
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.StepName, step.Name),
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.StepSuccess, false));

            Metrics.StepDuration.Record(duration.TotalMilliseconds,
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.StepName, step.Name));

            Metrics.StepErrors.Add(1,
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.StepName, step.Name));

            // Event for exception
            await context.SendEventAsync(new StepCompletedEvent
            {
                StepName = step.Name,
                StepPath = context.CurrentStepPath,
                Success = false,
                Duration = duration,
                ErrorMessage = ex.Message,
                CorrelationId = context.CorrelationId,
                StepResult = null,
                SchemaVersion = "1.0",
                Display = DisplayTextRenderer.StepEnd(
                    step, input, null, context, context.CurrentStepPath, false, ex.Message, duration, null)
            }, cancellationToken);

            throw;
        }
    }
}
