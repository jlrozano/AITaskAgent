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
    public required string StepName { get; init; }
    public abstract string EventType { get; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? CorrelationId { get; init; }
}
```

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

```csharp
var eventChannel = services.GetRequiredService<IEventChannel>();

eventChannel.Subscribe<DataProcessedEvent>(async e =>
{
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
