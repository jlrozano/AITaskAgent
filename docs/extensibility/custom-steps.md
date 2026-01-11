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

## Implementing IEnrichableStep

Add observability hooks:

```csharp
public class ObservableStep : TypedStep<In, Out>, IEnrichableStep
{
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
        return baseEvent with
        {
            AdditionalData = new Dictionary<string, object?>
            {
                ["customMetric"] = CalculateMetric(result)
            }
        };
    }
}
```

## Custom StepResult

```csharp
public record MyStepResult : StepResult
{
    public required string ProcessedData { get; init; }
    public int ItemCount { get; init; }
    public TimeSpan ProcessingTime { get; init; }
}
```

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
