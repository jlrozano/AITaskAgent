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

From [PipelineVisualizer](../../Samples/PipelineVisualizer/Middleware/ContextBroadcastMiddleware.cs)
— emits context state after each step:

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
                CurrentPath = context.CurrentStepPath,
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

## Registering Custom Middlewares

`ExecuteAsync` no longer takes a `userMiddlewares` argument. Middlewares are registered once on the
built service provider and apply to **every** pipeline execution in the process, in registration
order.

```csharp
var provider = services.BuildServiceProvider();
provider.UseAITaskAgent();

provider.RegisterPipelineMiddleware<PerformanceMiddleware>();
provider.RegisterPipelineMiddleware<PrometheusMiddleware>();

// or, by type:
provider.RegisterPipelineMiddlewares(typeof(PerformanceMiddleware), typeof(PrometheusMiddleware));
```

The middleware is resolved from DI when registered there, and otherwise constructed with
`ActivatorUtilities`, so constructor injection works either way. Registrations land in the static
`PipelineMiddlewareRegistry`; duplicate instances are ignored, and `PipelineMiddlewareRegistry.Clear()`
exists for tests.

## Middleware Order

```
[Your Middlewares] → Observability → Timeout → Retry → Step
```

Your middlewares see:
- Raw input before any processing
- Result after all internal middleware processing

Because `TimeoutMiddleware` wraps `RetryMiddleware`, the step timeout is global across all retry
attempts, not per attempt.

### RetryMiddleware is terminal

`RetryMiddleware` does **not** call `next()`. It calls `step.ExecuteAsync` directly, in a loop, and
runs structural and semantic validation between attempts. Two consequences:

1. Nothing can be chained after it — it is always the innermost middleware.
2. Its whole retry loop happens **inside** the single `next()` call made by the middlewares above
   it. A middleware wrapping `next` sees one invocation covering all attempts, not one per attempt.

If your middleware needs per-attempt visibility, subscribe to the `step.started` events instead:
`RetryMiddleware` emits one for every attempt after the first, with `AttemptNumber` set, precisely
because `ObservabilityMiddleware` cannot see them.

```csharp
eventChannel.Subscribe(async (evt, ct) =>
{
    if (evt is StepStartedEvent started && started.AttemptNumber > 1)
    {
        logger.LogWarning("Retry {N} of {Step}", started.AttemptNumber, started.StepName);
    }
});
```

## Best Practices

1. **Always call `next()`** - Unless intentionally short-circuiting
2. **Use try/finally** - Ensure cleanup
3. **Keep stateless** - Middlewares are singletons shared across every step and every pipeline
4. **Don't swallow exceptions** - Unless intentional
5. **Handle cancellation** - `next` takes a `CancellationToken`; pass yours through so downstream
   middlewares can replace it (this is how `TimeoutMiddleware` imposes the step timeout)
