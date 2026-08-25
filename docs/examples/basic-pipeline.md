# Basic Pipeline Example

## Simple Sequential Pipeline

```csharp
using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.Core.StepResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

try
{
    // Setup DI
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.AddLogging(b => b.AddConsole());
    services.AddAITaskAgent();

    var provider = services.BuildServiceProvider();
    provider.UseAITaskAgent();          // required: wires the static factories

    var pipeline = provider.GetRequiredService<IPipeline>();

    // Define steps.
    // The first delegate argument is the step itself - use it to build the result.
    var step1 = new DelegatedStep<StepResult<string>, StepResult<int>>(
        "Generate",
        (step, input, context, attempt, lastResult) =>
        {
            Console.WriteLine("Step 1: Generating number...");
            return Task.FromResult(new StepResult<int>(step, 42));
        });

    var step2 = new DelegatedStep<StepResult<int>, StepResult<int>>(
        "Double",
        (step, input, context, attempt, lastResult) =>
        {
            Console.WriteLine($"Step 2: Doubling {input.Value}...");
            return Task.FromResult(new StepResult<int>(step, input.Value * 2));
        });

    var step3 = new DelegatedStep<StepResult<int>, StepResult<string>>(
        "Format",
        (step, input, context, attempt, lastResult) =>
        {
            Console.WriteLine($"Step 3: Formatting {input.Value}...");
            return Task.FromResult(new StepResult<string>(step, $"Result: {input.Value}"));
        });

    // Execute pipeline
    var result = await pipeline.ExecuteAsync(
        name: "BasicPipeline",
        steps: [step1, step2, step3],
        input: "start");

    Console.WriteLine($"Output: {result.Value}");
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

> `input` does not have to be an `IStepResult`. When it is not, the pipeline wraps it into the
> input type declared by the first step.

---

## Parallel Pipeline

`ParallelStep` runs every entry of its `Branches` collection concurrently, each on a context cloned
via `CloneForBranch()`. The result is a `ParallelResult` keyed by branch step name.

```csharp
using AITaskAgent.Core.Steps;
using AITaskAgent.Core.StepResults;

try
{
    var fetchUsers = new DelegatedStep<StepResult<string>, StepResult<List<string>>>(
        "FetchUsers",
        async (step, input, context, attempt, lastResult) =>
        {
            await Task.Delay(100);                       // Simulate API call
            return new StepResult<List<string>>(step, ["Alice", "Bob"]);
        });

    var fetchOrders = new DelegatedStep<StepResult<string>, StepResult<int>>(
        "FetchOrders",
        async (step, input, context, attempt, lastResult) =>
        {
            await Task.Delay(150);
            return new StepResult<int>(step, 42);
        });

    var fetchSettings = new DelegatedStep<StepResult<string>, StepResult<string>>(
        "FetchSettings",
        async (step, input, context, attempt, lastResult) =>
        {
            await Task.Delay(80);
            return new StepResult<string>(step, "dark-mode");
        });

    // The generic variant pins the input type, so the pipeline knows how to wrap a raw input.
    // The non-generic ParallelStep accepts any IStepResult and is what YAML deserializes into.
    var parallel = new ParallelStep<StepResult<string>>(
        "FetchAll", fetchUsers, fetchOrders, fetchSettings);

    var result = await pipeline.ExecuteAsync(
        name: "ParallelPipeline",
        steps: [parallel],
        input: "start");

    if (result is ParallelResult parallelResult)
    {
        Console.WriteLine("Parallel results:");
        foreach (var (name, stepResult) in parallelResult.Value)
        {
            Console.WriteLine($"  {name}: {stepResult.Value}");
        }

        // Or read a single branch directly - the indexer returns its Value
        var orders = parallelResult["FetchOrders"];
        var users  = parallelResult.GetResult<StepResult<List<string>>>("FetchUsers");
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

If any branch fails, `ParallelResult.Error` collects every branch error message; the branches
themselves still all run to completion.

---

## Pipeline with Context

```csharp
try
{
    var context = PipelineContextFactory.Create();

    // Store data in context
    context.Metadata["userId"] = "user-123";

    var step1 = new DelegatedStep<StepResult<string>, StepResult<string>>(
        "ReadContext",
        (step, input, ctx, attempt, lastResult) =>
        {
            var userId = ctx.Metadata["userId"]?.ToString();
            return Task.FromResult(new StepResult<string>(step, $"User: {userId}"));
        });

    var step2 = new ActionStep<StepResult<string>>(
        "WriteContext",
        (input, ctx, attempt, lastResult) =>
        {
            ctx.Metadata["processedAt"] = DateTime.UtcNow;
            return Task.CompletedTask;
        });

    await pipeline.ExecuteAsync(
        name: "ContextPipeline",
        steps: [step1, step2],
        input: "start",
        context: context);

    Console.WriteLine($"Processed at: {context.Metadata["processedAt"]}");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
```

`ActionStep<TIn>` is for side effects: the delegate returns `Task`, and the step produces an
`ActionResult` carrying a success or failure message. Pass `fireAndForget: true` to let it return
immediately without awaiting the action.

Step results accumulate in `context.StepResults`, keyed by step path
(`context.CurrentStepPath` + `/` + step name). That dictionary is also what makes a resumed
pipeline skip the steps that already succeeded.

---

## Watching a Run

Handlers passed as `subscribers` get an event channel scoped to that single execution:

```csharp
var result = await pipeline.ExecuteAsync(
    name: "BasicPipeline",
    steps: [step1, step2, step3],
    input: "start",
    subscribers:
    [
        (evt, ct) =>
        {
            Console.WriteLine($"[{evt.EventType}] {evt.StepName}");
            return Task.CompletedTask;
        }
    ]);
```
