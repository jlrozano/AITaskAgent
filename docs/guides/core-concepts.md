# Core Concepts

## Terminology

### Step
A unit of work in the pipeline. Implements `IStep` or `IStep<TIn, TOut>`.

### StepResult
The output of a step. Implements `IStepResult` or `IStepResult<T>`.

### Pipeline
`sealed class Pipeline : IPipeline`. A DI singleton that runs steps sequentially through a
middleware chain. Resolve `IPipeline` — it is not a static class.

### PipelineContext
Shared state across all steps in a pipeline execution:
- Conversation history (`Conversation`)
- Step results cache (`StepResults`, keyed by step path)
- Metadata dictionary (`Metadata`)
- Event channel (`EventChannel`, `SendEventAsync`)
- Current position in the execution tree (`CurrentStepPath`)
- Service access (`ServiceProvider`)

Built by `PipelineContextFactory`, which is registered in DI and also arms a static
`PipelineContextFactory.Create()` entry point during `UseAITaskAgent()`.

### LlmRole
The per-step LLM behaviour: provider, model, system prompt, inference settings, fallbacks. Roles
live in `LlmEnvironment.Roles` and replace the old "provider profile" concept.

### Middleware
Wraps step execution to add cross-cutting concerns (observability, retry, timeout). Registered
globally through `PipelineMiddlewareRegistry`.

## Step Lifecycle

See [Middlewares](middlewares.md) for the full chain, the two validation layers, and the four
ways out of the retry loop.

`FinalizeAsync` runs once, after the last attempt — success or final failure — never per attempt.
The retry loop happens inside Observability's single call, so only the first attempt's
`StepStartedEvent` comes from the middleware; the rest are emitted by `RetryMiddleware` itself.

## Interfaces vs Classes

| Interface | Base Class | Purpose |
|-----------|------------|---------|
| `IStep` | `StepBase` | Minimal contract |
| `IStep<TIn, TOut>` | `TypedStep<TIn, TOut>` | Type-safe steps |
| `IStepResult` | `StepResult` / `StepResult<T>` | Result contract |
| `ILlmService` | *(none — implement directly)* | LLM providers |
| `ITool` | `LlmTool` | LLM tools |
| `ITools` | `AvailableTools` | Tool registry |
| `IStepValidator` | *(none)* | Pluggable semantic validation |
| `IEnrichable` | `StepBase` | Observability enrichment (composition via `StepBase.Enrichable`) |
| `IFallbackable` | `StepBase` / `BaseLlmStep` | Provider fallback |
| `ITemplateRendered` | `TinyTemplateRendered` | Prompt rendering |
| `ITemplateProvider` | `FileTemplateProvider` | Named template lookup |

## StepResult Properties

```csharp
public interface IStepResult
{
    bool HasError { get; }           // Error occurred
    IStepError? Error { get; set; }  // Error details (Message + OriginalException)
    IStep Step { get; }              // Producer step
    object? Value { get; }           // Result value
    List<IStep> NextSteps { get; }   // Dynamic routing
    object? SuspendInfo { get; set; }// Set when execution pauses for external input

    Task<(bool IsValid, string? Error)> ValidateAsync(PipelineContext context);
}
```

The presence of `Error.OriginalException` is what separates a fatal failure from a recoverable one:
see [Resilience](resilience.md).

## PipelineContext Usage

```csharp
// Access conversation
context.Conversation.AddUserMessage("Hello");

// Store metadata (also reachable from prompts as {{ context.userid }})
context.Metadata["userId"] = "123";

// Get a previous step result. The key is the step's full path:
// just "AnalyzeText" at the top level, "Parent/Child" when nested,
// "MyLoop/Iter_2/Inner" inside a LoopStep.
var prev = context.StepResults["AnalyzeText"];

// Send event
await context.SendEventAsync(new MyEvent { ... }, ct);

// Resolve a service from inside a step
var storage = context.ServiceProvider.GetRequiredService<IConversationStorage>();
```
