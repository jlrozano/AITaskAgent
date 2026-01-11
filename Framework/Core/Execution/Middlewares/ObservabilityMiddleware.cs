using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Base;
using AITaskAgent.Core.Models;
using AITaskAgent.Observability.Events;
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
        var stopwatch = Stopwatch.StartNew();

        // Check if step supports enrichment
        var enrichable = step as IEnrichableStep;

        using var activity = Telemetry.Source.StartActivity(
            $"Step.{step.Name}",
            ActivityKind.Internal);
        activity?.SetTag(AITaskAgentConstants.TelemetryTags.StepName, step.Name);
        activity?.SetTag(AITaskAgentConstants.TelemetryTags.StepType, step.GetType().Name);
        activity?.SetTag(AITaskAgentConstants.TelemetryTags.CorrelationId, context.CorrelationId);

        // ENRICHMENT: Allow step to add custom tags before execution
        enrichable?.EnrichActivityBefore(activity, input, context);

        // Create and enrich started event
        var startedEvent = new StepStartedEvent
        {
            StepName = step.Name,
            CorrelationId = context.CorrelationId
        };
        var enrichedStartedEvent = enrichable?.EnrichStartedEvent(startedEvent, input, context) ?? startedEvent;
        await context.SendEventAsync(enrichedStartedEvent, cancellationToken);

        IStepResult result;
        try
        {
            // Execute next middleware (or step itself)
            result = await next(cancellationToken);

            stopwatch.Stop();
            var duration = stopwatch.Elapsed;

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

            var completedEvent = new StepCompletedEvent()
            {
                StepName = step.Name,
                Success = !result.HasError,
                Duration = duration,
                ErrorMessage = result.HasError ? result.Error?.Message : null,
                CorrelationId = context.CorrelationId
            };

            // ENRICHMENT: Allow step to add custom data to event
            var enrichedCompletedEvent = enrichable?.EnrichCompletedEvent(completedEvent, result, context) ?? completedEvent;
            await context.SendEventAsync(enrichedCompletedEvent, cancellationToken);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var duration = stopwatch.Elapsed;

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
                Success = false,
                Duration = duration,
                ErrorMessage = ex.Message,
                CorrelationId = context.CorrelationId
            }, cancellationToken);

            throw;
        }
    }
}
