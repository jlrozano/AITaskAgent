using AITaskAgent.Core;
using AITaskAgent.Core.Base;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace AITaskAgent.LLM.Tools.Base;

/// <summary>
/// Abstract base class for LLM tools with built-in observability.
/// Uses Template Method pattern - ExecuteAsync controls flow, InternalExecuteAsync is your logic.
/// </summary>
public abstract class LlmTool : ITool
{
    /// <summary>Gets the unique name of the tool.</summary>
    public abstract string Name { get; }

    /// <summary>Gets the description of what the tool does.</summary>
    public abstract string Description { get; }

    /// <summary>Gets the JSON schema for the tool parameters.</summary>
    protected abstract BinaryData ParametersSchema { get; }

    /// <summary>
    /// Gets the provider-agnostic tool definition.
    /// </summary>
    public ToolDefinition GetDefinition()
    {
        return new ToolDefinition
        {
            Name = Name,
            Description = Description,
            ParametersJsonSchema = ParametersSchema.ToString()
        };
    }

    /// <summary>
    /// Executes the tool with observability (traces, metrics, events).
    /// This is the Template Method - controls the flow and calls hooks.
    /// </summary>
    public async Task<string> ExecuteAsync(
        string argumentsJson,
        PipelineContext context,
        string stepName,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        using var activity = Telemetry.Source.StartActivity(
            $"Tool.{Name}",
            ActivityKind.Internal);
        activity?.SetTag(AITaskAgentConstants.TelemetryTags.ToolName, Name);
        activity?.SetTag(AITaskAgentConstants.TelemetryTags.StepName, stepName);
        activity?.SetTag(AITaskAgentConstants.TelemetryTags.CorrelationId, context.CorrelationId);

        // Hook: Derived classes add specific tags
        EnrichActivityBefore(activity, argumentsJson, context);

        var startedEvent = new ToolStartedEvent()
        {
            StepName = stepName,
            ToolName = Name,
            CorrelationId = context.CorrelationId
        };
        // Hook: Derived classes enrich started event
        var enrichedStartedEvent = EnrichStartedEvent(startedEvent, argumentsJson, context);
        await context.SendEventAsync(enrichedStartedEvent, cancellationToken);

        logger.LogDebug("Tool {ToolName} executing with arguments length: {Length}",
            Name, argumentsJson.Length);

        var result = string.Empty;
        string? errorMessage = null;
        var success = true;

        try
        {
            result = await InternalExecuteAsync(argumentsJson, cancellationToken);

            stopwatch.Stop();
            activity?.SetStatus(ActivityStatusCode.Ok);

            logger.LogDebug("Tool {ToolName} completed, result length: {Length} chars",
                Name, result?.Length ?? 0);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            success = false;
            errorMessage = ex.Message;
            activity?.SetStatus(ActivityStatusCode.Error, errorMessage);

            logger.LogError(ex, "Tool {ToolName} failed: {Error}", Name, errorMessage);

            // Re-throw to let caller handle
            throw;
        }
        finally
        {
            var duration = stopwatch.Elapsed;

            // Hook: Derived classes add result-specific tags
            EnrichActivityAfter(activity, result, context);

            activity?.SetTag("tool.duration_ms", duration.TotalMilliseconds);
            activity?.SetTag("tool.success", success);

            // Record native metrics
            Metrics.ToolExecutions.Add(1,
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.ToolName, Name),
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.StepSuccess, success));

            Metrics.ToolDuration.Record(duration.TotalMilliseconds,
                new KeyValuePair<string, object?>(AITaskAgentConstants.TelemetryTags.ToolName, Name));

            var baseEvent = new ToolCompletedEvent()
            {
                StepName = stepName,
                ToolName = Name,
                Success = success,
                Duration = duration,
                ErrorMessage = errorMessage,
                CorrelationId = context.CorrelationId
            };
            var enrichedEvent = EnrichCompletedEvent(baseEvent, result, context);
            await context.SendEventAsync(enrichedEvent, cancellationToken);
        }

        return result!;
    }


    /// <summary>
    /// Internal tool logic to be implemented by derived classes.
    /// </summary>
    protected abstract Task<string> InternalExecuteAsync(
        string argumentsJson,
        CancellationToken cancellationToken);

    // ═══════════════════════════════════════════════════════════════════════════
    // VIRTUAL HOOKS - Derived classes override to enrich observability data
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Hook to add OpenTelemetry tags before execution.
    /// </summary>
    protected virtual void EnrichActivityBefore(Activity? activity, string argumentsJson, PipelineContext context)
    {
        // Default: no additional tags
    }

    /// <summary>
    /// Hook to add OpenTelemetry tags after execution.
    /// </summary>
    protected virtual void EnrichActivityAfter(Activity? activity, string result, PipelineContext context)
    {
        // Default: no additional tags
    }

    /// <summary>
    /// Hook to enrich the started event with tool-specific data.
    /// </summary>
    protected virtual ToolStartedEvent EnrichStartedEvent(ToolStartedEvent baseEvent, string argumentsJson, PipelineContext context)
    {
        return baseEvent;
    }

    /// <summary>
    /// Hook to enrich the completed event with tool-specific data.
    /// </summary>
    protected virtual ToolCompletedEvent EnrichCompletedEvent(ToolCompletedEvent baseEvent, string result, PipelineContext context)
    {
        return baseEvent;
    }
}
