# Steps

## Overview

Steps are the building blocks of pipelines. Each step receives input, performs work, and produces output.

## Step Types

### ActionStep

Simple step with inline logic:

```csharp
var step = new ActionStep<StringResult, StringResult>(
    "ProcessText",
    async (input, context) =>
    {
        return new StringResult(input.Value.ToUpper());
    });
```

### TypedStep

Base class for custom steps:

```csharp
public class MyStep : TypedStep<InputResult, OutputResult>
{
    public MyStep() : base("MyStep") { }
    
    protected override async Task<OutputResult> ExecuteAsync(
        InputResult input,
        PipelineContext context,
        int attempt,
        OutputResult? lastResult,
        CancellationToken ct)
    {
        // Your logic here
        return new OutputResult(input.Value * 2);
    }
}
```

### GroupStep

Sequential execution of sub-steps:

```csharp
var group = new GroupStep<InputResult>("ProcessGroup", 
    new Step1(),
    new Step2(),
    new Step3()
);
```

### ParallelStep

Concurrent execution with result aggregation:

```csharp
var parallel = new ParallelStep<InputResult>("FetchAll",
    new FetchUserStep(),
    new FetchOrdersStep(),
    new FetchPreferencesStep()
);
```

**Key behaviors:**
- Each branch gets a **cloned** `ConversationContext` (isolated LLM history)
- `Metadata` and `StepResults` are **shared** across branches (thread-safe)
- All branches pass through middleware chain (observability, timeout, retry)

**Accessing results:**

```csharp
var result = await Pipeline.ExecuteAsync(..., steps: [parallel], ...);

if (result is ParallelResult parallelResult)
{
    var users = parallelResult.Results["FetchUserStep"];
    var orders = parallelResult.Results["FetchOrdersStep"];
}
```

### SwitchStep

Dynamic routing based on input:

```csharp
var router = new SwitchStep<AnalysisResult>(
    "RouteByIntent",
    (input, context) =>
    {
        return input.Intent switch
        {
            "search" => (new SearchStep(), input),
            "order" => (new OrderStep(), input),
            _ => (new DefaultStep(), input)
        };
    });
```

## Step Configuration

### Retry

```csharp
public class MyStep : TypedStep<In, Out>
{
    public MyStep() : base("MyStep")
    {
        MaxRetries = 3;  // Default: 3
    }
}
```

### Timeout

```csharp
// Step-level timeout (via IStep)
public TimeSpan? Timeout => TimeSpan.FromSeconds(30);

// Pipeline-level timeout
await Pipeline.ExecuteAsync(..., pipelineTimeout: TimeSpan.FromMinutes(5));
```

### Validation

```csharp
protected override Task<(bool IsValid, string? Error)> Validate(
    IStepResult result, 
    PipelineContext context, 
    CancellationToken ct)
{
    if (result.Value == null)
        return Task.FromResult((false, "Value cannot be null"));
    
    return Task.FromResult((true, (string?)null));
}
```

## NextSteps (Dynamic Routing)

Steps can return additional steps to execute:

```csharp
protected override async Task<MyResult> ExecuteAsync(...)
{
    var result = new MyResult(value);
    
    if (needsMoreProcessing)
    {
        result.NextSteps.Add(new AdditionalStep());
    }
    
    return result;
}
```
