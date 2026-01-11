# Custom Middleware Example

Based on `ContextBroadcastMiddleware` from PipelineVisualizer.

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
                CurrentPath = context.CurrentPath,
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

## Usage

```csharp
using MyApp.Middleware;

try
{
    var logger = host.Services.GetRequiredService<ILogger<ContextBroadcastMiddleware>>();
    var eventChannel = host.Services.GetRequiredService<IEventChannel>();
    
    // Subscribe to snapshots
    eventChannel.Subscribe<ContextSnapshotEvent>(async snapshot =>
    {
        Console.WriteLine($"[Snapshot] Step: {snapshot.StepName}");
        Console.WriteLine($"  Path: {snapshot.CurrentPath}");
        Console.WriteLine($"  Results: {string.Join(", ", snapshot.StepResults.Keys)}");
        Console.WriteLine($"  Metadata: {string.Join(", ", snapshot.Metadata.Keys)}");
    });
    
    // Create middleware
    var middlewares = new IPipelineMiddleware[]
    {
        new ContextBroadcastMiddleware(logger)
    };
    
    // Execute with middleware
    var result = await Pipeline.ExecuteAsync(
        name: "MonitoredPipeline",
        steps: [step1, step2, step3],
        input: new EmptyResult(),
        userMiddlewares: middlewares
    );
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
```

**Output:**
```
[Snapshot] Step: Step1
  Path: 
  Results: Step1
  Metadata: startTime

[Snapshot] Step: Step2
  Path: 
  Results: Step1, Step2
  Metadata: startTime

[Snapshot] Step: Step3
  Path: 
  Results: Step1, Step2, Step3
  Metadata: startTime, endTime
```

## Real-Time Dashboard

Use with SignalR or SSE for live updates:

```csharp
public class DashboardHub : Hub
{
    public async Task StreamSnapshots(IEventChannel eventChannel)
    {
        eventChannel.Subscribe<ContextSnapshotEvent>(async snapshot =>
        {
            await Clients.All.SendAsync("ContextSnapshot", snapshot);
        });
    }
}
```
