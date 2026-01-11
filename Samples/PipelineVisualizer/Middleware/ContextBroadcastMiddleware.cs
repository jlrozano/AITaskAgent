using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using Newtonsoft.Json;
using PipelineVisualizer.Events;

namespace PipelineVisualizer.Middleware;

/// <summary>
/// Application-level middleware that emits PipelineContext snapshots after each step.
/// This is NOT part of the core framework - it's specific to this visualizer application.
/// </summary>
public sealed class ContextBroadcastMiddleware(ILogger<ContextBroadcastMiddleware> logger) : IPipelineMiddleware
{
    /// <inheritdoc />
    public async Task<IStepResult> InvokeAsync(
        IStep step,
        IStepResult input,
        PipelineContext context,
        Func<CancellationToken, Task<IStepResult>> next,
        CancellationToken cancellationToken)
    {
        var result = await next(cancellationToken);

        try
        {
            // Build serializable snapshot of context
            var snapshot = new ContextSnapshotEvent
            {
                StepName = step.Name,
                CorrelationId = context.CorrelationId,
                Timestamp = DateTime.UtcNow,
                CurrentPath = context.CurrentPath,
                StepResults = SerializeStepResults(context),
                Metadata = SerializeMetadata(context)
            };

            await context.SendEventAsync(snapshot, cancellationToken);
            logger.LogDebug("Context snapshot emitted for step {StepName}", step.Name);
        }
        catch (Exception ex)
        {
            // Never fail the pipeline due to snapshot errors
            logger.LogWarning(ex, "Failed to emit context snapshot for step {StepName}", step.Name);
        }

        return result;
    }

    private static Dictionary<string, object?> SerializeStepResults(PipelineContext context)
    {
        var result = new Dictionary<string, object?>();
        foreach (var kvp in context.StepResults)
        {
            try
            {
                // Serialize to object to capture relevant properties
                var json = JsonConvert.SerializeObject(kvp.Value, new JsonSerializerSettings
                {
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                    MaxDepth = 3
                });
                result[kvp.Key] = JsonConvert.DeserializeObject(json);
            }
            catch
            {
                result[kvp.Key] = kvp.Value?.ToString();
            }
        }
        return result;
    }

    private static Dictionary<string, object?> SerializeMetadata(PipelineContext context)
    {
        var result = new Dictionary<string, object?>();
        foreach (var kvp in context.Metadata)
        {
            try
            {
                result[kvp.Key] = kvp.Value?.ToString();
            }
            catch
            {
                result[kvp.Key] = "<<serialization error>>";
            }
        }
        return result;
    }
}
