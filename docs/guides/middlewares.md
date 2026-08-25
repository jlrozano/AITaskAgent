# Middlewares

## Overview

Middlewares wrap step execution to add cross-cutting concerns. They follow the ASP.NET Core middleware pattern.

## Middleware Chain

Execution order (outer to inner):

```
User Middlewares → ObservabilityMiddleware → TimeoutMiddleware → RetryMiddleware → Step.ExecuteAsync
```

![Middleware Execution Flow](../assets/images/middleware_onion_architecture.png)

## IPipelineMiddleware

```csharp
public interface IPipelineMiddleware
{
    Task<IStepResult> InvokeAsync(
        IStep step,
        IStepResult input,
        PipelineContext context,
        Func<CancellationToken, Task<IStepResult>> next,
        CancellationToken cancellationToken);
}
```

## Internal Middlewares

### ObservabilityMiddleware

Always first. Provides:
- OpenTelemetry traces (`Activity`)
- Native metrics (counters, histograms)
- Event emission (`StepStartedEvent`, `StepCompletedEvent`)
- Enrichment hooks through `IEnrichable` (`StepBase.Enrichable`)

### TimeoutMiddleware

Creates a linked `CancellationToken` from `step.Timeout ?? IPipeline.DefaultStepTimeout`.

A **null** timeout is legal and means "no timeout": the step runs with the incoming token
untouched. On expiry the middleware emits a `StepTimeoutEvent` carrying `StepName`, `StepPath` and
`ConfiguredTimeout`, then surfaces the cancellation.

Because Timeout wraps Retry, the timeout is **global across all retry attempts**, not per attempt.

### RetryMiddleware

Terminal middleware that handles retry logic:

1. Executes `step.ExecuteAsync(...)` — emitting a `StepStartedEvent` for every attempt after the first
2. Runs structural validation (`result.ValidateAsync`)
3. Runs semantic validation (`step.Validators`)
4. If invalid and attempts remain, retries with the error text as feedback
5. Calls `step.FinalizeAsync(...)` on completion

Exceptions are **fatal**, not retried. Only error results *without* an `OriginalException` are
retried, on the theory that those are recoverable logic/parsing errors an LLM can self-correct.
When the step implements `IFallbackable` and switches provider, the attempt counter resets to 1.

## Registering Custom Middlewares

Global middlewares are registered once, not passed per execution. `ExecuteAsync` has no
`userMiddlewares` parameter.

```csharp
using AITaskAgent.Core.Execution;

// From the service provider (resolved from DI, or built with ActivatorUtilities)
provider.RegisterPipelineMiddleware<LoggingMiddleware>();
provider.RegisterPipelineMiddlewares(typeof(LoggingMiddleware), typeof(MetricsMiddleware));

// Or directly with an instance
PipelineMiddlewareRegistry.Register(new LoggingMiddleware(logger));
PipelineMiddlewareRegistry.RegisterRange([m1, m2]);
```

The registry is a static, thread-safe list applied to **all** pipeline executions in registration
order, ahead of the internal middlewares. Duplicate instances are ignored.
`PipelineMiddlewareRegistry.Clear()` exists for tests.

## Creating a Custom Middleware

```csharp
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;

public class LoggingMiddleware(ILogger<LoggingMiddleware> logger) : IPipelineMiddleware
{
    public async Task<IStepResult> InvokeAsync(
        IStep step,
        IStepResult input,
        PipelineContext context,
        Func<CancellationToken, Task<IStepResult>> next,
        CancellationToken cancellationToken)
    {
        // BEFORE step
        logger.LogInformation("Step {StepName} starting", step.Name);
        var sw = Stopwatch.StartNew();
        
        try
        {
            // Execute next middleware (or step)
            var result = await next(cancellationToken);
            
            // AFTER step (success)
            sw.Stop();
            logger.LogInformation(
                "Step {StepName} completed in {Duration}ms, success: {Success}",
                step.Name, sw.ElapsedMilliseconds, !result.HasError);
            
            return result;
        }
        catch (Exception ex)
        {
            // AFTER step (exception)
            sw.Stop();
            logger.LogError(ex, 
                "Step {StepName} failed after {Duration}ms", 
                step.Name, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
```

## Example: ContextBroadcastMiddleware

From PipelineVisualizer - emits context snapshots after each step:

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
            var snapshot = new ContextSnapshotEvent
            {
                StepName = step.Name,
                CorrelationId = context.CorrelationId,
                CurrentPath = context.CurrentStepPath,
                StepResults = SerializeStepResults(context),
                Metadata = SerializeMetadata(context)
            };
            
            await context.SendEventAsync(snapshot, cancellationToken);
        }
        catch (Exception ex)
        {
            // Never fail the pipeline due to snapshot errors
            logger.LogWarning(ex, "Failed to emit snapshot");
        }
        
        return result;
    }
}
```

## Best Practices

1. **Call `next()`** - Always invoke the next middleware
2. **Use try/finally** - Ensure cleanup runs
3. **Don't swallow exceptions** - Unless intentional
4. **Keep stateless** - A single instance is shared by every pipeline execution, including
   concurrent ones and parallel branches
5. **Order matters** - Registered middlewares run before the internal ones, in registration order
