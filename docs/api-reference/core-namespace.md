# Core Namespace

`AITaskAgent.Core.*`

## Pipeline

Static executor with middleware composition.

```csharp
public static class Pipeline
{
    public static TimeSpan DefaultPipelineTimeout { get; set; }
    public static TimeSpan DefaultStepTimeout { get; set; }
    public static ILoggerFactory LoggerFactory { get; set; }
    
    public static Task<IStepResult> ExecuteAsync<T>(
        string name,
        IReadOnlyList<IStep> steps,
        T input,
        PipelineContext? context = null,
        IEnumerable<IPipelineMiddleware>? userMiddlewares = null,
        TimeSpan? pipelineTimeout = null);
}
```

**Source:** [Pipeline.cs](file:///c:/Users/juan.rozano/Desktop/AgenteAI/AITaskAgentFramework/Framework/Core/Execution/Pipeline.cs)

---

## PipelineContext

Shared state across steps.

```csharp
public sealed record PipelineContext(IEventChannel? EventChannel = null)
{
    public ConversationContext Conversation { get; init; }
    public string CorrelationId { get; init; }
    public ConcurrentDictionary<string, object?> Metadata { get; init; }
    public ConcurrentDictionary<string, IStepResult> StepResults { get; init; }
    public string CurrentPath { get; }
    
    public PipelineContext CloneForBranch();
    public Task<bool> SendEventAsync<TEvent>(TEvent eventData, CancellationToken ct = default);
}
```

**Source:** [PipelineContext.cs](file:///c:/Users/juan.rozano/Desktop/AgenteAI/AITaskAgentFramework/Framework/Core/Models/PipelineContext.cs)

---

## Base Classes

### StepBase

Abstract base for all steps.

```csharp
public abstract class StepBase : IStep, IEnrichableStep
{
    public string Name { get; }
    public int MaxRetries { get; set; }
    public Type InputType { get; }
    public Type OutputType { get; }
    protected ILogger Logger { get; }
    
    protected IStepResult CreateResult(object? value, IStepError? error = null);
    protected IStepResult CreateErrorResult(string message, Exception? exception = null);
    
    // Override these for custom behavior
    protected abstract Task<IStepResult> ExecuteAsync(...);
    protected virtual void EnrichActivityBefore(...);
    protected virtual void EnrichActivityAfter(...);
    protected virtual Task FinalizeAsync(...);
    protected virtual Task<(bool IsValid, string? Error)> Validate(...);
}
```

**Source:** [StepBase.cs](file:///c:/Users/juan.rozano/Desktop/AgenteAI/AITaskAgentFramework/Framework/Core/Steps/StepBase.cs)

### TypedStep&lt;TIn, TOut&gt;

Strongly-typed step base.

```csharp
public abstract class TypedStep<TIn, TOut> : StepBase
    where TIn : IStepResult
    where TOut : IStepResult
{
    protected abstract Task<TOut> ExecuteAsync(
        TIn input, 
        PipelineContext context, 
        int attempt, 
        TOut? lastResult, 
        CancellationToken cancellationToken);
}
```

---

## Step Types

| Class | Input | Output | Purpose |
|-------|-------|--------|---------|
| `ActionStep<TIn, TOut>` | `TIn` | `TOut` | Inline action |
| `DelegatedStep` | `IStepResult` | `IStepResult` | Dynamic delegation |
| `EmptyStep` | - | `EmptyResult` | No-op placeholder |
| `GroupStep<TIn>` | `TIn` | `IStepResult` | Sequential sub-steps |
| `ParallelStep<TIn>` | `TIn` | `ParallelResult` | Concurrent execution |
| `SwitchStep<TIn>` | `TIn` | `IStepResult` | Dynamic routing |

---

## Result Types

| Class | Value Type | Purpose |
|-------|------------|---------|
| `StepResult` | `object?` | Base result |
| `StepResult<T>` | `T` | Typed result |
| `StringResult` | `string` | Text result |
| `EmptyResult` | - | No value |
| `ErrorStepResult` | - | Error container |
| `ParallelResult` | `Dictionary<string, IStepResult>` | Parallel results |
