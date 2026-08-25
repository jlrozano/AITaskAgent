# Custom Events

## Overview

Create custom events to extend the observability system.

## Creating a Custom Event

### 1. Define the Event

```csharp
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;

public sealed record DataProcessedEvent : ProgressEventBase
{
    public override string EventType => "data.processed";
    
    // Custom properties
    public required int RecordsProcessed { get; init; }
    public required TimeSpan ProcessingTime { get; init; }
    public double ThroughputPerSecond { get; init; }
    public Dictionary<string, int>? CategoryCounts { get; init; }
}
```

### 2. Base Class Reference

```csharp
public abstract record ProgressEventBase : IProgressEvent
{
    public string StepName { get; init; } = string.Empty;
    public abstract string EventType { get; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? CorrelationId { get; init; }
    public bool SuppressFromUser { get; init; }
    public string SchemaVersion { get; init; } = "1.0";
}
```

`SuppressFromUser = true` means the event is logged but never routed to subscribers — use it for
diagnostics you do not want surfacing in a UI. `SchemaVersion` lets consumers cope with payload
changes; set it explicitly if your event ever gains or loses fields.

## Emitting Events from Steps

```csharp
public class DataProcessingStep : TypedStep<DataInput, DataOutput>
{
    protected override async Task<DataOutput> ExecuteAsync(
        DataInput input,
        PipelineContext context,
        int attempt,
        DataOutput? lastResult,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        
        // Process data
        var result = await ProcessAsync(input.Data, cancellationToken);
        
        sw.Stop();
        
        // Emit custom event
        await context.SendEventAsync(new DataProcessedEvent
        {
            StepName = Name,
            CorrelationId = context.CorrelationId,
            RecordsProcessed = result.Count,
            ProcessingTime = sw.Elapsed,
            ThroughputPerSecond = result.Count / sw.Elapsed.TotalSeconds,
            CategoryCounts = result.GroupBy(r => r.Category)
                .ToDictionary(g => g.Key, g => g.Count())
        }, cancellationToken);
        
        return CreateResult(result);
    }
}
```

## Emitting Events from Middlewares

```csharp
public class AuditMiddleware : IPipelineMiddleware
{
    public async Task<IStepResult> InvokeAsync(...)
    {
        // Emit before
        await context.SendEventAsync(new AuditEvent
        {
            StepName = step.Name,
            Action = "started",
            User = context.Metadata["userId"]?.ToString()
        }, cancellationToken);
        
        var result = await next(cancellationToken);
        
        // Emit after
        await context.SendEventAsync(new AuditEvent
        {
            StepName = step.Name,
            Action = result.HasError ? "failed" : "completed",
            User = context.Metadata["userId"]?.ToString()
        }, cancellationToken);
        
        return result;
    }
}
```

## Subscribing to Custom Events

There is no generic `Subscribe<TEvent>`. The handler receives every event as `IProgressEvent` and
filters by type; an optional `correlationId` narrows the subscription to one pipeline execution.

```csharp
var eventChannel = services.GetRequiredService<IEventChannel>();

using var subscription = eventChannel.Subscribe(async (evt, ct) =>
{
    if (evt is not DataProcessedEvent e) return;

    Console.WriteLine($"Processed {e.RecordsProcessed} records");
    Console.WriteLine($"Throughput: {e.ThroughputPerSecond:F2}/sec");
    
    // Log to external system
    await analytics.TrackAsync("data_processed", new
    {
        records = e.RecordsProcessed,
        duration_ms = e.ProcessingTime.TotalMilliseconds,
        correlation_id = e.CorrelationId
    });
});
```

`Subscribe` returns an `IDisposable`; disposing it unsubscribes and drains the handler's buffer.
Each subscription has its own bounded channel (200 items, drop-oldest), so a slow handler never
blocks the pipeline — it loses the oldest events instead.

### Reader form

For SSE, WebSockets or anything that wants to pull rather than be pushed:

```csharp
await using var subscription = eventChannel.CreateSubscription(
    capacity: 500,
    correlationId: context.CorrelationId);

await foreach (var evt in subscription.Reader.ReadAllAsync(cancellationToken))
{
    if (evt is DataProcessedEvent e)
    {
        await response.WriteAsync($"data: {JsonConvert.SerializeObject(e)}\n\n", cancellationToken);
    }
}
```

### Per-execution subscribers

`IPipeline.ExecuteAsync` also accepts a `subscribers` argument. Those handlers get an
`EventChannel` created for that run alone, which is the cleanest way to isolate concurrent
executions:

```csharp
await pipeline.ExecuteAsync(
    name: "MyPipeline",
    steps: steps,
    input: input,
    subscribers: [async (evt, ct) => Console.WriteLine(evt.EventType)]);
```

## Event for Streaming Updates

```csharp
public sealed record ProgressUpdateEvent : ProgressEventBase
{
    public override string EventType => "progress.update";
    
    public required int Current { get; init; }
    public required int Total { get; init; }
    public string? Message { get; init; }
    
    public double PercentComplete => Total > 0 ? (double)Current / Total * 100 : 0;
}

// In step
for (int i = 0; i < items.Count; i++)
{
    await ProcessItemAsync(items[i], cancellationToken);
    
    if (i % 10 == 0)  // Every 10 items
    {
        await context.SendEventAsync(new ProgressUpdateEvent
        {
            StepName = Name,
            Current = i + 1,
            Total = items.Count,
            Message = $"Processing item {i + 1}"
        }, cancellationToken);
    }
}
```

## Best Practices

1. **Unique EventType** - Use dot notation: `domain.action`
2. **Include CorrelationId** - For distributed tracing
3. **Keep events small** - Don't embed large payloads
4. **Handle nulls** - Subscribers shouldn't crash
5. **Don't await sensitive operations** - Event emission should be fast
