# Core Concepts

## Terminology

### Step
A unit of work in the pipeline. Implements `IStep` or `IStep<TIn, TOut>`.

### StepResult
The output of a step. Implements `IStepResult` or `IStepResult<T>`.

### Pipeline
Static executor that runs steps sequentially through a middleware chain.

### PipelineContext
Shared state across all steps in a pipeline execution:
- Conversation history
- Step results cache
- Metadata dictionary
- Event channel

### Middleware
Wraps step execution to add cross-cutting concerns (observability, retry, timeout).

## Step Lifecycle

![Step Lifecycle Detailed](../assets/images/step_lifecycle_detailed.png)

## Interfaces vs Classes

| Interface | Base Class | Purpose |
|-----------|------------|---------|
| `IStep` | - | Minimal contract |
| `IStep<TIn, TOut>` | `TypedStep<TIn, TOut>` | Type-safe steps |
| `IStepResult` | `StepResult` | Result contract |
| `ILlmService` | `BaseLlmService` | LLM providers |
| `ITool` | `LlmTool` | LLM tools |

## StepResult Properties

```csharp
public interface IStepResult
{
    bool HasError { get; }           // Error occurred
    IStepError? Error { get; set; }  // Error details
    IStep Step { get; }              // Producer step
    object? Value { get; }           // Result value
    List<IStep> NextSteps { get; }   // Dynamic routing
}
```

## PipelineContext Usage

```csharp
// Access conversation
context.Conversation.AddUserMessage("Hello");

// Store metadata
context.Metadata["userId"] = "123";

// Get previous step result
var prev = context.StepResults["StepName"];

// Send event
await context.SendEventAsync(new MyEvent { ... }, ct);
```
