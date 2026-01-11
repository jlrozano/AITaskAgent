# Custom Middlewares

## Overview

Create middlewares to add cross-cutting concerns to pipeline execution.

## Implementing IPipelineMiddleware

```csharp
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using System.Diagnostics;

public class PerformanceMiddleware(ILogger<PerformanceMiddleware> logger) : IPipelineMiddleware
{
    public async Task<IStepResult> InvokeAsync(
        IStep step,
        IStepResult input,
        PipelineContext context,
        Func<CancellationToken, Task<IStepResult>> next,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        
        try
        {
            // Execute the pipeline chain
            var result = await next(cancellationToken);
            
            sw.Stop();
            logger.LogInformation(
                "Step {Step} completed in {Ms}ms, success: {Success}",
                step.Name, sw.ElapsedMilliseconds, !result.HasError);
            
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, 
                "Step {Step} failed after {Ms}ms", 
                step.Name, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
```

## Example: Metrics Middleware

```csharp
public class PrometheusMiddleware(Counter stepCounter, Histogram stepDuration) 
    : IPipelineMiddleware
{
    public async Task<IStepResult> InvokeAsync(
        IStep step,
        IStepResult input,
        PipelineContext context,
        Func<CancellationToken, Task<IStepResult>> next,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        
        try
        {
            var result = await next(cancellationToken);
            
            sw.Stop();
            stepCounter.Inc(new[] { step.Name, result.HasError ? "error" : "success" });
            stepDuration.Observe(sw.Elapsed.TotalSeconds, new[] { step.Name });
            
            return result;
        }
        catch
        {
            sw.Stop();
            stepCounter.Inc(new[] { step.Name, "exception" });
            stepDuration.Observe(sw.Elapsed.TotalSeconds, new[] { step.Name });
            throw;
        }
    }
}
```

## Example: Context Snapshot Middleware

From PipelineVisualizer - emits context state after each step:

```csharp
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
        var result = await next(cancellationToken);
        
        try
        {
            // Build snapshot
            var snapshot = new ContextSnapshotEvent
            {
                StepName = step.Name,
                CorrelationId = context.CorrelationId,
                CurrentPath = context.CurrentPath,
                StepResults = context.StepResults
                    .ToDictionary(k => k.Key, v => (object?)v.Value.Value),
                Metadata = context.Metadata
                    .ToDictionary(k => k.Key, v => v.Value)
            };
            
            await context.SendEventAsync(snapshot, cancellationToken);
        }
        catch (Exception ex)
        {
            // Never fail pipeline due to snapshot
            logger.LogWarning(ex, "Snapshot failed for {Step}", step.Name);
        }
        
        return result;
    }
}
```

## Using Custom Middlewares

```csharp
var middlewares = new IPipelineMiddleware[]
{
    new PerformanceMiddleware(logger),
    new PrometheusMiddleware(counter, histogram)
};

await Pipeline.ExecuteAsync(
    name: "MyPipeline",
    steps: [step1, step2],
    input: input,
    userMiddlewares: middlewares  // Runs BEFORE internal middlewares
);
```

## Middleware Order

```
[Your Middlewares] → Observability → Timeout → Retry → Step
```

Your middlewares see:
- Raw input before any processing
- Result after all internal middleware processing

## Best Practices

1. **Always call `next()`** - Unless intentionally short-circuiting
2. **Use try/finally** - Ensure cleanup
3. **Keep stateless** - Middlewares are reused across steps
4. **Don't swallow exceptions** - Unless intentional
5. **Handle cancellation** - Pass token through
