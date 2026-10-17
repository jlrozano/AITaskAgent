# Custom Steps

## Overview

Create custom steps by extending `StepBase` or `TypedStep<TIn, TOut>`.

## Option 1: TypedStep (Recommended)

Type-safe with automatic result creation:

```csharp
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;

public class DataTransformStep : TypedStep<DataInput, DataOutput>
{
    public DataTransformStep() : base("DataTransform")
    {
        MaxRetries = 3;
    }
    
    protected override async Task<DataOutput> ExecuteAsync(
        DataInput input,
        PipelineContext context,
        int attempt,
        DataOutput? lastResult,
        CancellationToken cancellationToken)
    {
        Logger.LogInformation("Transforming data, attempt {Attempt}", attempt);
        
        // Your transformation logic
        var transformed = await TransformAsync(input.Data, cancellationToken);
        
        // Store in metadata for later steps
        context.Metadata["transformedAt"] = DateTime.UtcNow;
        
        return CreateResult(transformed);
    }
    
    protected override Task<(bool IsValid, string? Error)> Validate(
        IStepResult result, 
        PipelineContext context, 
        CancellationToken cancellationToken)
    {
        if (result is DataOutput output && output.Value?.Length > 0)
            return Task.FromResult((true, (string?)null));
        
        return Task.FromResult((false, "Transformation produced empty result"));
    }
}
```

## Option 2: StepBase (Full Control)

For non-generic scenarios:

```csharp
public class DynamicStep : StepBase
{
    public DynamicStep() : base("Dynamic", typeof(IStepResult), typeof(IStepResult))
    {
    }
    
    protected override async Task<IStepResult> ExecuteAsync(
        IStepResult input,
        PipelineContext context,
        int attempt,
        IStepResult? lastResult,
        CancellationToken cancellationToken)
    {
        // Handle any input type
        var value = input.Value;
        
        // Create result dynamically
        return CreateResult(ProcessValue(value));
    }
}
```

## Enriching observability

`IEnrichableStep` no longer exists. Enrichment is `IEnrichable`, and it is **composition, not
inheritance**: `StepBase` already implements `IEnrichable` and exposes the hooks as
`protected virtual` methods, so a step just overrides what it needs.

```csharp
public class ObservableStep : TypedStep<In, Out>
{
    public ObservableStep() : base("Observable") { }
    
    // protected internal in StepBase; outside the framework assembly the override is plain protected
    protected override void EnrichActivityBefore(
        Activity? activity, 
        IStepResult input, 
        PipelineContext context)
    {
        activity?.SetTag("custom.input_size", input.Value?.ToString().Length);
    }
    
    protected override void EnrichActivityAfter(
        Activity? activity, 
        IStepResult result, 
        PipelineContext context)
    {
        activity?.SetTag("custom.success", !result.HasError);
    }
    
    protected override IStepCompletedEvent EnrichCompletedEvent(
        IStepCompletedEvent baseEvent, 
        IStepResult result, 
        PipelineContext context)
    {
        return baseEvent is StepCompletedEvent completed
            ? completed with
              {
                  AdditionalData = new Dictionary<string, object?>
                  {
                      ["customMetric"] = CalculateMetric(result)
                  }
              }
            : baseEvent;
    }
}
```

Alternatively, attach an external enricher without touching the step's class:

```csharp
step.Enrichable = new MyEnricher();   // StepBase's default hooks delegate to it
```

## Custom StepResult

Step results are classes, not records, and their constructor takes the `IStep` that produced them —
that is where `StepName` and `StepType` come from.

```csharp
public class MyStepResult(IStep step, string? value = null) : StepResult<string>(step, value)
{
    public int ItemCount { get; init; }
    public TimeSpan ProcessingTime { get; init; }

    public override Task<(bool IsValid, string? Error)> ValidateAsync(PipelineContext context)
        => Task.FromResult(ItemCount > 0 ? (true, (string?)null) : (false, "No items produced"));
}
```

`ValidateAsync` on the result is the **structural** check; `IStep.ValidateAsync` (via
`StepBase.Validate`) is the **semantic** one. `RetryMiddleware` runs both, and a failure in either
consumes an attempt and feeds the error message back to the step's next attempt.

## Finalization Hook

Cleanup after all retries complete:

```csharp
protected override Task FinalizeAsync(
    IStepResult result, 
    PipelineContext context, 
    CancellationToken cancellationToken)
{
    Logger.LogInformation("Step finalized with success: {Success}", !result.HasError);
    
    // Cleanup resources
    _disposableResource?.Dispose();
    
    return Task.CompletedTask;
}
```

## Attachable Validators

Instead of overriding `Validate`, semantic checks can be attached as `IStepValidator` instances.
`StepBase.Validate` runs them in order and short-circuits on the first failure, so this is also the
mechanism YAML pipelines use.

```csharp
public sealed class NonEmptyValidator : IStepValidator
{
    public string Id => "non-empty";

    public Task<(bool IsValid, string? Error)> ValidateAsync(
        IStepResult result, PipelineContext context, CancellationToken cancellationToken)
        => Task.FromResult(string.IsNullOrWhiteSpace(result.Value?.ToString())
            ? (false, "Result is empty")
            : (true, (string?)null));
}

step.AddValidator(new NonEmptyValidator());
```

## Fallbacks

Implement `SwitchToNextFallback` to give a step a second chance with a different backing resource.
`RetryMiddleware` calls it when an attempt fails; returning `true` resets the attempt counter to 1
and runs again. `BaseLlmStep` uses this to walk `LlmRole.Fallbacks`.

```csharp
protected override bool SwitchToNextFallback(IStepResult error, PipelineContext context, int attempt)
{
    if (_endpointIndex + 1 >= _endpoints.Count) return false;
    _endpointIndex++;
    return true;
}
```

## Suspension

`IStep.Resume` is what makes a step suspendable. `StepBase` returns itself, so overriding
`SaveStateAsync` / `RestoreStateAsync` is enough:

```csharp
protected override Task<object?> SaveStateAsync() => Task.FromResult<object?>(_partialWork);

protected override Task RestoreStateAsync(object? state, object? hitlResponse)
{
    _partialWork = state as PartialWork;
    _approval = hitlResponse as ApprovalDecision;
    return Task.CompletedTask;
}
```

Returning a result with `SuspendInfo` set while `Resume` is `null` throws. The full flow is in
[Human in the loop](../guides/human-in-the-loop.md).
