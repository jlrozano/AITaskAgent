# Custom Middleware Example

Based on [`ContextBroadcastMiddleware`](../../Samples/PipelineVisualizer/Middleware/ContextBroadcastMiddleware.cs)
from PipelineVisualizer.

## Context Snapshot Middleware

Emits a snapshot of the pipeline context after each step:

```csharp
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace MyApp.Middleware;

/// <summary>
/// Custom event for context snapshots
/// </summary>
public sealed record ContextSnapshotEvent : ProgressEventBase
{
    public override string EventType => "context.snapshot";
    
    public required string CurrentPath { get; init; }
    public required Dictionary<string, object?> StepResults { get; init; }
    public required Dictionary<string, object?> Metadata { get; init; }
}

/// <summary>
/// Middleware that emits PipelineContext snapshots after each step.
/// Useful for debugging, visualization, and audit trails.
/// </summary>
public sealed class ContextBroadcastMiddleware(
    ILogger<ContextBroadcastMiddleware> logger) : IPipelineMiddleware
{
    public async Task<IStepResult> InvokeAsync(
        IStep step,
        IStepResult input,
        PipelineContext context,
        Func<CancellationToken, Task<IStepResult>> next,
        CancellationToken cancellationToken)
    {
        // Execute the step chain
        var result = await next(cancellationToken);
        
        try
        {
            // Build serializable snapshot
            var snapshot = new ContextSnapshotEvent
            {
                StepName = step.Name,
                CorrelationId = context.CorrelationId,
                CurrentPath = context.CurrentStepPath,
                StepResults = SerializeStepResults(context),
                Metadata = SerializeMetadata(context)
            };
            
            await context.SendEventAsync(snapshot, cancellationToken);
            
            logger.LogDebug(
                "Context snapshot emitted for step {StepName}, results: {Count}", 
                step.Name, 
                snapshot.StepResults.Count);
        }
        catch (Exception ex)
        {
            // IMPORTANT: Never fail the pipeline due to snapshot errors
            logger.LogWarning(ex, 
                "Failed to emit context snapshot for step {StepName}", 
                step.Name);
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
```

Serializing `IStepResult` is now viable precisely because a result carries `StepName` and
`StepType` strings instead of a reference to the `IStep` instance.

## Usage

Middlewares are registered on the built service provider, not passed per execution.

```csharp
using AITaskAgent.Core.Execution;
using MyApp.Middleware;

try
{
    var provider = services.BuildServiceProvider();
    provider.UseAITaskAgent();

    // Resolved from DI, or constructed with ActivatorUtilities when not registered
    provider.RegisterPipelineMiddleware<ContextBroadcastMiddleware>();

    var eventChannel = provider.GetRequiredService<IEventChannel>();
    var pipeline = provider.GetRequiredService<IPipeline>();

    // Subscribe to snapshots. Handlers receive every event; filter by type.
    using var subscription = eventChannel.Subscribe((evt, ct) =>
    {
        if (evt is ContextSnapshotEvent snapshot)
        {
            Console.WriteLine($"[Snapshot] Step: {snapshot.StepName}");
            Console.WriteLine($"  Path: {snapshot.CurrentPath}");
            Console.WriteLine($"  Results: {string.Join(", ", snapshot.StepResults.Keys)}");
            Console.WriteLine($"  Metadata: {string.Join(", ", snapshot.Metadata.Keys)}");
        }
        return Task.CompletedTask;
    });

    var result = await pipeline.ExecuteAsync(
        name: "MonitoredPipeline",
        steps: [step1, step2, step3],
        input: "start");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
```

**Output** (the pipeline stores a step's result in `context.StepResults` only after the whole
middleware chain returns, so a snapshot taken inside the chain does not yet include the current step):
```
[Snapshot] Step: Step1
  Path: 
  Results: 
  Metadata: startTime

[Snapshot] Step: Step2
  Path: 
  Results: Step1
  Metadata: startTime

[Snapshot] Step: Step3
  Path: 
  Results: Step1, Step2
  Metadata: startTime, endTime
```

## Seeing every attempt, not just every step

This middleware wraps `next()`, so it fires **once per step** — not once per retry.
`RetryMiddleware` is terminal: it calls `step.ExecuteAsync` directly instead of `next()`, so its
whole retry loop happens inside the single `next()` call above.

To observe individual attempts, listen for `StepStartedEvent`. `RetryMiddleware` emits one for
every attempt after the first, carrying `AttemptNumber`, for exactly this reason.

```csharp
using var retryWatch = eventChannel.Subscribe((evt, ct) =>
{
    if (evt is StepStartedEvent started && started.AttemptNumber > 1)
    {
        Console.WriteLine($"[Retry {started.AttemptNumber}] {started.StepName} @ {started.StepPath}");
    }
    return Task.CompletedTask;
});
```

## Real-Time Dashboard

Use with SignalR or SSE for live updates. Scope the subscription to a single execution by passing
its correlation id, so concurrent runs do not bleed into each other:

```csharp
public class DashboardHub(IEventChannel eventChannel) : Hub
{
    public async Task StreamSnapshots(string correlationId, CancellationToken cancellationToken)
    {
        await using var subscription = eventChannel.CreateSubscription(
            capacity: 500,
            correlationId: correlationId);

        await foreach (var evt in subscription.Reader.ReadAllAsync(cancellationToken))
        {
            if (evt is ContextSnapshotEvent snapshot)
            {
                await Clients.All.SendAsync("ContextSnapshot", snapshot, cancellationToken);
            }
        }
    }
}
```
