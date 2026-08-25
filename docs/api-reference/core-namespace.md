# Core Namespace

`AITaskAgent.Core.*`

## IPipeline / Pipeline

The executor is no longer static. `Pipeline` is a `sealed class` implementing `IPipeline`,
registered as a singleton by `AddAITaskAgent()` and resolved from DI.

```csharp
public interface IPipeline
{
    TimeSpan? DefaultPipelineTimeout { get; }
    TimeSpan? DefaultStepTimeout { get; }

    Task<IStepResult> ExecuteAsync<T>(
        string name,
        IReadOnlyList<IStep> steps,
        T input,
        PipelineContext? context = null,
        TimeSpan? pipelineTimeout = null,
        CancellationToken cancellationToken = default,
        IEnumerable<Func<IProgressEvent, CancellationToken, Task>>? subscribers = null);
}
```

```csharp
public sealed class Pipeline : IPipeline
{
    public Pipeline(
        TimeSpan? defaultPipelineTimeout = null,
        TimeSpan? defaultStepTimeout = null,
        ILoggerFactory? loggerFactory = null);
}
```

There is exactly **one** `ExecuteAsync` overload. A shorter one used to exist, but because every
parameter past the third had a default the two were ambiguous for any ordinary call; they were
merged.

```csharp
var pipeline = serviceProvider.GetRequiredService<IPipeline>();

var result = await pipeline.ExecuteAsync(
    name: "MyPipeline",
    steps: [step1, step2],
    input: "seed value");
```

`subscribers` creates an `EventChannel` local to that execution, so concurrent runs never see each
other's events. When omitted, the context uses the singleton `IEventChannel`.

**Execution order:** user middlewares → `ObservabilityMiddleware` → `TimeoutMiddleware` →
`RetryMiddleware` → step. Timeout wraps Retry, so it is global across all attempts.

**Source:** [Pipeline.cs](../../Framework/Core/Execution/Pipeline.cs), [IPipeline.cs](../../Framework/Core/Execution/IPipeline.cs)

---

## PipelineContext

Shared state across steps.

```csharp
public sealed record PipelineContext
{
    public PipelineContext(IEventChannel? eventChannel = null);
    public PipelineContext(IEventChannel? eventChannel, IServiceProvider serviceProvider);

    public IEventChannel? EventChannel { get; }
    public IServiceProvider ServiceProvider { get; }
    public ConversationContext Conversation { get; init; }
    public string CorrelationId { get; init; }
    public ConcurrentDictionary<string, object?> Metadata { get; init; }
    public ConcurrentDictionary<string, IStepResult> StepResults { get; init; }
    public string CurrentStepPath { get; }

    public object? GetMetadata(string key);
    public void SetMetadata(string key, object? value);
    public PipelineContext CloneForBranch();
    public Task<bool> SendEventAsync<TEvent>(TEvent eventData, CancellationToken cancellationToken = default)
        where TEvent : IProgressEvent;
}
```

- `CurrentPath` was renamed to **`CurrentStepPath`**.
- `ServiceProvider` is new — it is the pipeline equivalent of `HttpContext.RequestServices`, and is
  what steps use to resolve loggers and nested pipelines.
- The path is mutated only by the framework (`AddPathPart` / `RemovePathPart` are `internal`).

**Source:** [PipelineContext.cs](../../Framework/Core/Models/PipelineContext.cs)

### PipelineContextFactory

Lives in the same file and is registered as a singleton. Creating it wires the static `Create`
helpers, which is why `UseAITaskAgent()` must run on the built provider.

```csharp
public class PipelineContextFactory
{
    public PipelineContextFactory(
        IEventChannel? eventChannel,
        IServiceProvider serviceProvider,
        IPipelineContextStorage storage);

    public static PipelineContext Create(ConversationContext? conversation = null);

    public static PipelineContext Create(
        ConversationContext? conversation = null,
        IEnumerable<Func<IProgressEvent, CancellationToken, Task>>? subscribers = null);

    public static Task<PipelineContext> CreateFromId(string id);
}
```

---

## Base Classes

### StepBase

Abstract base for all steps. Note that enrichment is now **composition**: `StepBase` implements
`IEnrichable` itself and also exposes an `Enrichable` property that the virtual hooks delegate to.

```csharp
public abstract class StepBase(string name, Type inputType, Type outputType)
    : IStep, IEnrichable, IResume, IFallbackable
{
    public string Name { get; protected set; }
    public int MaxRetries { get; set; } = 3;
    public virtual IResume? Resume => this;
    public virtual TimeSpan? Timeout { get; set; }
    public Type InputType { get; protected internal set; }
    public Type OutputType { get; protected internal set; }

    public IReadOnlyList<IStepValidator> Validators { get; }
    public void AddValidator(IStepValidator validator);

    public IEnrichable? Enrichable { get; set; }

    // YAML-only, resolved by SchemaCompiler
    public string? InputSchema { get; init; }
    public string? OutputSchema { get; init; }

    protected ILogger Logger { get; }
    protected IStepResult CreateResult(object? value, IStepError? error = null);
    protected IStepResult CreateErrorResult(string message, Exception? exception = null);

    // Override these for custom behavior
    protected abstract Task<IStepResult> ExecuteAsync(
        IStepResult input, PipelineContext context, int attempt,
        IStepResult? lastStepResult, CancellationToken cancellationToken);

    protected internal virtual void EnrichActivityBefore(Activity? activity, IStepResult input, PipelineContext context);
    protected internal virtual void EnrichActivityAfter(Activity? activity, IStepResult result, PipelineContext context);
    protected virtual StepStartedEvent EnrichStartedEvent(StepStartedEvent baseEvent, IStepResult input, PipelineContext context);
    protected virtual IStepCompletedEvent EnrichCompletedEvent(IStepCompletedEvent baseEvent, IStepResult result, PipelineContext context);

    protected virtual Task FinalizeAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken);
    protected virtual Task<(bool IsValid, string? Error)> Validate(IStepResult result, PipelineContext context, CancellationToken cancellationToken);

    protected virtual Task<object?> SaveStateAsync();
    protected virtual Task RestoreStateAsync(object? state, object? hitlResponse);
    protected virtual bool SwitchToNextFallback(IStepResult error, int attempt);
}
```

**Source:** [StepBase.cs](../../Framework/Core/Steps/StepBase.cs)

### TypedStep&lt;TIn, TOut&gt;

Strongly-typed step base.

```csharp
public abstract class TypedStep<TIn, TOut>(string name)
    : StepBase(name, typeof(TIn), typeof(TOut)), IStep<TIn, TOut>
    where TIn : IStepResult
    where TOut : IStepResult
{
    protected abstract Task<TOut> ExecuteAsync(
        TIn input,
        PipelineContext context,
        int attempt,
        TOut? lastStepResult,
        CancellationToken cancellationToken);
}
```

---

## Step Types

| Class | Input | Output | Purpose |
|-------|-------|--------|---------|
| `ActionStep<TIn>` | `TIn` | `ActionResult` | Side effect; optional fire-and-forget |
| `DelegatedStep<TIn, TOut>` | `TIn` | `TOut` | Inline transformation via a delegate |
| `EmptyStep` | `void` | `void` | Pass-through placeholder used for error results |
| `GroupStep` / `GroupStep<TIn>` | `IStepResult` / `TIn` | last step's output | Sequential sub-steps in the same context |
| `ParallelStep` / `ParallelStep<TIn>` | `IStepResult` / `TIn` | `ParallelResult` | Concurrent branches, each with a cloned context |
| `LoopStep` | `IStepResult` | `IStepResult` | Repeats its `Steps` up to `MaxIterations` |
| `BreakStep` | `IStepResult` | `BreakStepResult` | Exits the enclosing `LoopStep`, tunnelling the input value |
| `SwitchStep<TIn>` | `TIn` | selected result type | Dynamic routing via `NextSteps` |

Signatures worth knowing:

```csharp
public sealed class ActionStep<TIn>(
    string name,
    Func<TIn, PipelineContext, int, ActionResult?, Task> action,
    bool fireAndForget = false) : TypedStep<TIn, ActionResult>(name)
    where TIn : IStepResult;

// The first delegate argument is the step itself — use it to build the result.
public class DelegatedStep<TIn, TOut>(
    string name,
    Func<DelegatedStep<TIn, TOut>, TIn, PipelineContext, int, TOut?, Task<TOut>> action)
    : TypedStep<TIn, TOut>(name)
    where TIn : IStepResult where TOut : IStepResult;

public class ParallelStep : StepBase
{
    public ParallelStep();                                    // YAML
    public ParallelStep(string name, params IStep[] branches);
    public List<IStep> Branches { get; init; }                // NOT "Steps"
}

public class GroupStep : StepBase
{
    public GroupStep();
    public GroupStep(string name, IReadOnlyList<IStep> steps);
    public List<IStep> Steps { get; init; }
}

public class LoopStep : StepBase
{
    public LoopStep();
    public LoopStep(string name, IReadOnlyList<IStep> steps, int maxIterations = 50);
    public List<IStep> Steps { get; init; }
    public int MaxIterations { get; init; } = 50;
    public string? OutputIterationToMetadata { get; init; }
}

public class SwitchStep<TIn>(
    string name,
    Func<TIn, PipelineContext, (IStep, IStepResult value)> selector) : IStep
    where TIn : IStepResult;
```

`GroupStep`, `ParallelStep`, `LoopStep` and `BreakStep` all carry `[StepTag]` so they can also be
declared declaratively — see [YAML pipelines](../guides/yaml-pipelines.md).

---

## Result Types

| Class | Value Type | Purpose |
|-------|------------|---------|
| `StepResult` | `object?` | Abstract base result |
| `StepResult<T>` | `T` | Typed result — `new StepResult<T>(step, value)` |
| `ActionResult` | `string` | Outcome message of an `ActionStep`, plus `Metadata` |
| `ErrorStepResult` | `string` | Error container (`FromException`, `FromMessage`) |
| `ParallelResult` | `IReadOnlyDictionary<string, IStepResult>` | Branch results keyed by step name |
| `BreakStepResult` | `object` | Signals an enclosing loop to stop |
| `SuspendStepResult` | `object` | Carries `DataToReview` while a step is suspended |

`ParallelResult` exposes `GetResult(name)`, `GetResult<T>(name)` and an indexer that returns the
branch's `Value` directly (`parallelResult["FetchUsers"]`).

---

## New Abstractions

Beyond `IStep`/`IStepResult`, `AITaskAgent.Core.Abstractions` now ships:

| Type | Purpose |
|------|---------|
| `IEnrichable` | Observability enrichment, by composition (replaces `IEnrichableStep`) |
| `IStepValidator` | Named semantic validator attachable to a step |
| `IStepActivator` | Step instantiation strategy (`DefaultStepActivator`) |
| `IFallbackable` | `bool SwitchToNextFallback(IStepResult error, int attempt)` |
| `IResume` | `SaveStateAsync` / `RestoreStateAsync` for suspended steps |
| `IFactory<T>` / `Factory<T>` | Resolve a service by a string key (defaults to its `Name` property) |
| `IPipeline` | The executor contract |
| `IPipelineContextStorage` | Persist/restore a suspended `PipelineContext` by correlation id |

See [Human in the loop](../guides/human-in-the-loop.md) for how `IResume`, `SuspendInfo` and
`IPipelineContextStorage` fit together.
