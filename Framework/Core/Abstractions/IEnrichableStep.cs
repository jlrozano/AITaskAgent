using AITaskAgent.Core.Models;
using AITaskAgent.Observability.Events;
using System.Diagnostics;

namespace AITaskAgent.Core.Abstractions;

/// <summary>
/// Optional interface for steps that want to enrich observability data.
/// Steps implementing ONLY IStep get basic observability automatically from Pipeline.
/// Implement this interface to add step-specific traces and events.
/// </summary>
public interface IEnrichableStep : IStep
{
    /// <summary>
    /// Enrich OpenTelemetry activity with step-specific tags before execution.
    /// Called by ObservabilityMiddleware before step execution.
    /// </summary>
    /// <param name="activity">The OpenTelemetry activity to enrich (may be null).</param>
    /// <param name="input">The input to the step.</param>
    /// <param name="context">The pipeline context.</param>
    void EnrichActivityBefore(Activity? activity, IStepResult input, PipelineContext context) { }

    /// <summary>
    /// Enrich OpenTelemetry activity with result-specific tags after execution.
    /// Called by ObservabilityMiddleware after step execution.
    /// </summary>
    /// <param name="activity">The OpenTelemetry activity to enrich (may be null).</param>
    /// <param name="result">The step result.</param>
    /// <param name="context">The pipeline context.</param>
    void EnrichActivityAfter(Activity? activity, IStepResult result, PipelineContext context) { }

    /// <summary>
    /// Enrich the started event with step-specific data before execution.
    /// Called by ObservabilityMiddleware before step execution.
    /// </summary>
    /// <param name="baseEvent">Base event with step name, etc.</param>
    /// <param name="input">The input to the step.</param>
    /// <param name="context">The pipeline context.</param>
    /// <returns>Enriched event (can be same instance or new instance with additional data).</returns>
    StepStartedEvent EnrichStartedEvent(StepStartedEvent baseEvent, IStepResult input, PipelineContext context) => baseEvent;

    /// <summary>
    /// Enrich the completed event with step-specific data after execution.
    /// Called by ObservabilityMiddleware after step execution.
    /// </summary>
    /// <param name="baseEvent">Base event with step name, duration, success, etc.</param>
    /// <param name="result">The step result.</param>
    /// <param name="context">The pipeline context.</param>
    /// <returns>Enriched event (can be same instance or new instance with additional data).</returns>
    IStepCompletedEvent EnrichCompletedEvent(IStepCompletedEvent baseEvent, IStepResult result, PipelineContext context) => baseEvent;
}
