# Basic Pipeline Example

## Simple Sequential Pipeline

```csharp
using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.Core.StepResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

try
{
    // Setup DI
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddAITaskAgent();
    var host = builder.Build();
    
    Pipeline.LoggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
    
    // Define steps
    var step1 = new ActionStep<EmptyResult, StepResult<int>>(
        "Generate",
        async (input, context) =>
        {
            Console.WriteLine("Step 1: Generating number...");
            return new StepResult<int>(42);
        });
    
    var step2 = new ActionStep<StepResult<int>, StepResult<int>>(
        "Double",
        async (input, context) =>
        {
            Console.WriteLine($"Step 2: Doubling {input.Value}...");
            return new StepResult<int>(input.Value * 2);
        });
    
    var step3 = new ActionStep<StepResult<int>, StringResult>(
        "Format",
        async (input, context) =>
        {
            Console.WriteLine($"Step 3: Formatting {input.Value}...");
            return new StringResult($"Result: {input.Value}");
        });
    
    // Execute pipeline
    var result = await Pipeline.ExecuteAsync(
        name: "BasicPipeline",
        steps: [step1, step2, step3],
        input: new EmptyResult()
    );
    
    Console.WriteLine($"Output: {((StringResult)result).Value}");
    Console.WriteLine($"Success: {!result.HasError}");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
```

**Output:**
```
Step 1: Generating number...
Step 2: Doubling 42...
Step 3: Formatting 84...
Output: Result: 84
Success: True
```

---

## Parallel Pipeline

```csharp
using AITaskAgent.Core.Steps;
using AITaskAgent.Core.StepResults;

try
{
    // Define parallel branches
    var fetchUsers = new ActionStep<EmptyResult, StepResult<List<string>>>(
        "FetchUsers",
        async (input, context) =>
        {
            await Task.Delay(100); // Simulate API call
            return new StepResult<List<string>>(["Alice", "Bob"]);
        });
    
    var fetchOrders = new ActionStep<EmptyResult, StepResult<int>>(
        "FetchOrders",
        async (input, context) =>
        {
            await Task.Delay(150); // Simulate API call
            return new StepResult<int>(42);
        });
    
    var fetchSettings = new ActionStep<EmptyResult, StringResult>(
        "FetchSettings",
        async (input, context) =>
        {
            await Task.Delay(80); // Simulate API call
            return new StringResult("dark-mode");
        });
    
    // Parallel execution
    var parallel = new ParallelStep<EmptyResult>(
        "FetchAll",
        fetchUsers,
        fetchOrders,
        fetchSettings
    );
    
    var result = await Pipeline.ExecuteAsync(
        name: "ParallelPipeline",
        steps: [parallel],
        input: new EmptyResult()
    );
    
    if (result is ParallelResult parallelResult)
    {
        Console.WriteLine("Parallel results:");
        foreach (var (name, stepResult) in parallelResult.Results)
        {
            Console.WriteLine($"  {name}: {stepResult.Value}");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
```

**Output:**
```
Parallel results:
  FetchUsers: [Alice, Bob]
  FetchOrders: 42
  FetchSettings: dark-mode
```

---

## Pipeline with Context

```csharp
try
{
    var contextFactory = host.Services.GetRequiredService<PipelineContextFactory>();
    var context = contextFactory.Create();
    
    // Store data in context
    context.Metadata["userId"] = "user-123";
    
    var step1 = new ActionStep<EmptyResult, StringResult>(
        "ReadContext",
        async (input, ctx) =>
        {
            var userId = ctx.Metadata["userId"]?.ToString();
            return new StringResult($"User: {userId}");
        });
    
    var step2 = new ActionStep<StringResult, StringResult>(
        "WriteContext",
        async (input, ctx) =>
        {
            ctx.Metadata["processedAt"] = DateTime.UtcNow;
            return input;
        });
    
    await Pipeline.ExecuteAsync(
        name: "ContextPipeline",
        steps: [step1, step2],
        input: new EmptyResult(),
        context: context
    );
    
    Console.WriteLine($"Processed at: {context.Metadata["processedAt"]}");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
```
