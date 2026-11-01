# Core Namespace

`AITaskAgent.Core.*`

> From v2 the contracts live in the `AITaskAgent.Abstractions` assembly (`IStep`, `IStepResult`, `IStepRegistry`,
> `IExtension`, `PipelineContext`, the step results and the base events), while the engine (`Pipeline`, `StepBase`,
> the basic steps, middlewares) lives in `AITaskAgent.Core`. Namespaces did not change. See
> [ARCHITECTURE-v2](../ARCHITECTURE-v2.md).

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
public sealed class Pipeline : PipelineRuntimeBase, IPipeline
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

`Pipeline` derives from `PipelineRuntimeBase`, the public base of `IPipelineRuntime`
(`ILoggerFactory LoggerFactory { get; }` plus `ExecuteStepsWithNextStepsAsync(...)`), which is the
engine contract the composite steps use to run their children through the same middleware chain.

**Execution order:** user middlewares → `ObservabilityMiddleware` → `TimeoutMiddleware` →
`RetryMiddleware` → step. Timeout wraps Retry, so it is global across all attempts.

**Suspension and persistence:** when a run ends suspended (`SuspendInfo != null`), `Pipeline.ExecuteAsync` saves the
context in the registered `IPipelineContextStorage` under its `CorrelationId`, before emitting `PipelineCompletedEvent`. Without a
registered storage (a pipeline built outside DI) it only logs a warning; if `SaveAsync` throws, the exception propagates. When a
resumed run completes without error the saved context is deleted; a failed run keeps it. See
[Human in the loop](../guides/human-in-the-loop.md).

**Source:** [Pipeline.cs](../../AITaskAgent.Core/Execution/Pipeline.cs), [IPipeline.cs](../../AITaskAgent.Abstractions/Execution/IPipeline.cs)

---

## PipelineContext

Shared state across steps. The base context carries no LLM state: it is a non-sealed `record` so that
extensions derive from it (the LLM extension adds the conversation in `LlmPipelineContext`).

```csharp
public record PipelineContext
{
    public PipelineContext(IEventChannel? eventChannel = null);
    public PipelineContext(IEventChannel? eventChannel, IServiceProvider serviceProvider);

    public IEventChannel? EventChannel { get; }
    public IServiceProvider ServiceProvider { get; }
    public string CorrelationId { get; init; }
    public ConcurrentDictionary<string, object?> Metadata { get; init; }
    public ConcurrentDictionary<string, IStepResult> StepResults { get; init; }
    public string CurrentStepPath { get; }
    public IPipelineRuntime? Runtime { get; internal set; }   // [JsonIgnore]

    public object? GetMetadata(string key);
    public void SetMetadata(string key, object? value);
    public virtual PipelineContext CloneForBranch();
    public Task<bool> SendEventAsync<TEvent>(TEvent eventData, CancellationToken cancellationToken = default)
        where TEvent : IProgressEvent;
}
```

There is no context interface: the engine signatures (`IStep.ExecuteAsync`, `IPipelineMiddleware`, `IPipeline`)
take `PipelineContext`, the base class, and the record's polymorphism is enough for LLM steps to receive their derived context.

- `Conversation` is no longer a member of `PipelineContext`. It lives in
  `LlmPipelineContext` (`AITaskAgent.LLM.Conversation.Context`, assembly `AITaskAgent.Extensions.Llm`);
  see [LLM Namespace](llm-namespace.md#llmpipelinecontext).
- `CloneForBranch()` is virtual and uses `this with { }`, so a branch of an `LlmPipelineContext` stays an
  `LlmPipelineContext` (with a cloned conversation). `Metadata` and `StepResults` are shared between branches on purpose.
- `Runtime` is the engine executing the context, assigned by the pipeline when a run starts and
  propagated to branch clones; it is `null` before a run and on a context just loaded from storage.
  Composite steps run their children through it (falling back to an `IPipelineRuntime` from
  `ServiceProvider`), and the context's loggers come from `Runtime.LoggerFactory` first.
  `EventChannel`, `ServiceProvider` and `Runtime` are not serialized.
- The path is mutated only by the framework (`AddPathPart` / `RemovePathPart` are `internal`; the assemblies that
  need them get `InternalsVisibleTo`).

**Source:** [PipelineContext.cs](../../AITaskAgent.Abstractions/Models/PipelineContext.cs)

### PipelineContextFactory

Lives in `AITaskAgent.Core` and is registered as a singleton. Creating it wires the static `Create`
helper, which is why `UseAITaskAgent()` must run on the built provider. Extensions that need a richer context derive
from it and override `CreateContext`; `AddAITaskAgent()` registers `LlmPipelineContextFactory`, so the context it
creates is an `LlmPipelineContext` at run time (the static return type is still `PipelineContext`).

```csharp
public class PipelineContextFactory
{
    public PipelineContextFactory(
        IEventChannel? eventChannel,
        IServiceProvider serviceProvider,
        IPipelineContextStorage storage);

    public static PipelineContext Create(
        IEnumerable<Func<IProgressEvent, CancellationToken, Task>>? subscribers = null);

    // Same as Create, through this factory instance (Create uses the last factory built in the process)
    public PipelineContext NewContext(
        IEnumerable<Func<IProgressEvent, CancellationToken, Task>>? subscribers = null);

    protected virtual PipelineContext CreateContext(IEventChannel? eventChannel);

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
    : IStep, IEnrichable, IResume, IFallbackable, IDisplayable
{
    public string Name { get; protected internal set; }
    public int MaxRetries { get; set; } = 3;
    public virtual IResume? Resume => this;
    public virtual TimeSpan? Timeout { get; set; }
    public Type InputType { get; protected internal set; }
    public Type OutputType { get; protected internal set; }
    public virtual Type? RequiredContext => null;   // LLM steps: typeof(LlmPipelineContext)

    public IReadOnlyList<IStepValidator> Validators { get; }
    public void AddValidator(IStepValidator validator);

    public virtual IEnrichable? Enrichable { get; set; }          // [JsonIgnore]; not configurable from YAML

    // Result validators (YAML resultValidators); run as part of IStepResult.ValidateAsync
    public IReadOnlyList<IResultValidator> ResultValidators { get; }
    public void AddResultValidator(IResultValidator validator);

    public StepHooks? Hooks { get; set; }                         // generic hooks (YAML hooks:); see "Hooks" below

    // IDisplayable: templates of the text shown in StepStartedEvent.Display / StepCompletedEvent.Display
    public virtual string? DisplayStart { get; set; }             // YAML display: { start: ... }
    public virtual string? DisplayEnd { get; set; }               // YAML display: { end: ... }

    protected ILogger Logger { get; }
    protected void InitializeLogger(PipelineContext context);   // called automatically during ExecuteAsync
    protected Type ValueType { get; }
    protected virtual JsonSchema? Schema { get; }
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

    // After each attempt that returned no error, before validation; default dispatches Hooks.PostProcess
    protected virtual Task<IStepResult> PostProcessAsync(
        IStepResult input, IStepResult result, PipelineContext context, int attempt, CancellationToken cancellationToken);

    protected virtual Task FinalizeAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken);
    protected virtual Task<(bool IsValid, string? Error)> Validate(IStepResult result, PipelineContext context, CancellationToken cancellationToken);

    protected virtual Task<object?> SaveStateAsync();
    protected virtual Task RestoreStateAsync(object? state, object? hitlResponse);
    protected virtual bool SwitchToNextFallback(IStepResult error, PipelineContext context, int attempt);
}
```

`ValueType` and `Schema` describe the `Value` of the step's output type. `Schema` is virtual: by default it is generated by
the internal `StepResultFactory` (and is what `BaseLlmStep` injects into the system prompt for
structured output): a property is required when it carries `[Required]` or when it is non-nullable,
init-only and has no `[DefaultValue]`; `[Range]` sets `minimum`/`maximum` on both integer and
number properties; `[StringLength]`, `[MinLength]` and `[MaxLength]` are also applied.

**Source:** [StepBase.cs](../../AITaskAgent.Core/Steps/StepBase.cs)

### Hooks

`StepBase.Hooks` holds a `StepHooks` record (`AITaskAgent.Core.Hooks`): the YAML `hooks:` slots. A hook is the default
implementation of the matching virtual method of the step, one layer above the built-in behaviour. The slots of `StepHooks` are
`PostProcess`, `OnFinalize`, `EnrichActivityBefore`, `EnrichActivityAfter`, `EnrichStarted` and `EnrichCompleted`; `BaseLlmStep`
declares `[HooksType(typeof(LlmStepHooks))]`, so its `Hooks` is an `LlmStepHooks` (extra slots such as `BuildUserMessage`,
`BuildSystemMessage`, `ConfigureRequest`, `ParseResponse`; `BaseLlmStep.LlmHooks` exposes it).

The C# slots that replace a built-in behaviour (`PostProcess`, and the `BuildUserMessage`, `BuildSystemMessage` and `ParseResponse` slots
of `LlmStepHooks`) are typed `HookAround<TArgs, T>`: they receive `next`, the built-in behaviour. The other slots are plain
`Func`/`Action` delegates with no `next` (`OnFinalize` and the enrichment slots among them). The "after" form (no `next`; the hook edits a writable value of its arguments)
is what the JavaScript and YAML hooks use, bound through `HookForms.After`.

| Type | Purpose |
|------|---------|
| `HookAttribute` | On a `public static` method of a public class: registers it as a hook a YAML step can name (`Name` or `Class.Method`, optional `Description`) |
| `ValidatorAttribute` | On `Task<(bool IsValid, string? Error)> M(ValidateArgs)`: a step validator (YAML `validators:`) |
| `ResultValidatorAttribute` | On `Task<(bool IsValid, string? Error)> M(ResultValidateArgs)`: a result validator (YAML `resultValidators:`) |
| `ResponseTypeAttribute` | On a public class: registers its JSON schema under `Name` (or the class name) so `outputSchema` / `schema:Name` can use it |
| `IResultValidator` | `string Id` + `ValidateAsync(IStepResult, PipelineContext, CancellationToken)`: validates the response itself; attached with `AddResultValidator` |
| `IHookRegistry` / `HookRegistry` | Named delegates by `HookKind` (`Hook`, `StepValidator`, `ResultValidator`): `TryGet(name, kind, delegateType, out hook)`, `List()`; `HookRegistry.Add(name, kind, hook, description?, source)` rejects a duplicate name |

**Source:** [StepHooks.cs](../../AITaskAgent.Core/Hooks/StepHooks.cs), [HookAttributes.cs](../../AITaskAgent.Abstractions/Hooks/HookAttributes.cs),
[HookRegistry.cs](../../AITaskAgent.Abstractions/Hooks/HookRegistry.cs), [LlmStepHooks.cs](../../AITaskAgent.Extensions.Llm/Steps/LlmStepHooks.cs)

### TypedStep&lt;TIn, TOut&gt;

Strongly-typed step base.

```csharp
public abstract class TypedStep<TIn, TOut>(string name)
    : StepBase(name, typeof(TIn), typeof(TOut)), IStep<TIn, TOut>
    where TIn : IStepResult
    where TOut : IStepResult
{
    // Typed versions hiding the StepBase ones
    protected new TOut CreateResult(object? value = null, IStepError? error = null);
    protected new TOut CreateErrorResult(string message, Exception? exception = null);

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
| `BreakStep` | `IStepResult` | `BreakStepResult` | Exits the enclosing `LoopStep`, tunnelling the input value; with `When` (template expression) it exits only if the expression is truthy; an empty or blank `When` is rejected (`ArgumentException`), `null` means unconditional |
| `SwitchStep<TIn>` | `TIn` | selected result type | Dynamic routing via `NextSteps` |
| `ExecStep` | `IStepResult` | stdout text | Runs an external executable or batch file (no shell; tag `ExecStep`); failures raise `ExecStepException` (`Kind`); a host can register an `IExecStepPolicy` to deny commands or confine the working directory |
| `SuspendStep` | `IStepResult` | the response (`JsonStepResult`) | Suspends the pipeline asking for input (`AwaitingInputResult`); on resume returns the response (tag `SuspendStep`); see [Human in the loop](../guides/human-in-the-loop.md) |
| `CSharpStep` | `IStepResult` | what the body returns | C# written in the pipeline, compiled by the host; lives in `AITaskAgent.Extensions.CSharp` (tag `CSharpStep`); see [Extensions](extensions.md) |
| `DeclarativeSwitchStep` (tag `SwitchStep`) | `IStepResult` | chosen case's output | Declarative switch for YAML: `On` expression, `Cases` and `Default` with child steps; case keys that match ignoring case and surrounding spaces are a construction error in YAML |

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
    public int MaxDegreeOfParallelism { get; init; }          // <= 0 (default): Environment.ProcessorCount
    public bool FailFast { get; set; } = true;                // false: the step does not fail; each failed branch keeps its own error
}

public sealed class ParallelStep<TIn> : ParallelStep where TIn : StepResult
{
    public ParallelStep(string name, params IStep[] branches);
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

`GroupStep`, `ParallelStep`, `LoopStep`, `BreakStep` and `DeclarativeSwitchStep` carry `[StepTag]` (`GroupStep`, `ParallelStep`, `LoopStep`, `BreakStep`, `SwitchStep`) so they can also be
declared declaratively — see [YAML pipelines](../guides/yaml-pipelines.md).

---

## Result Types

| Class | Value Type | Purpose |
|-------|------------|---------|
| `StepResult` | `object?` | Abstract base result |
| `StepResult<T>` | `T` | Typed result — `new StepResult<T>(step, value)` |
| `ActionResult` | `string` | Outcome message of an `ActionStep`, plus `Metadata` |
| `SuccessStepResult` | `string` | Success message — `new SuccessStepResult(step, message)` |
| `ErrorStepResult` | `string` | Error container (`FromException`, `FromMessage`) |
| `ParallelResult` | `IReadOnlyDictionary<string, IStepResult>` | Branch results keyed by step name |
| `BreakStepResult` | `object` | Signals an enclosing loop to stop |
| `SuspendStepResult` | `object` | Carries `DataToReview` while a step is suspended |
| `JsonStepResult` | `JToken` | Result of declarative steps: carries an optional `JsonSchema` (NJsonSchema) and validates its value at run time; `static Validate(JToken?, JsonSchema?)` returns the list of violations |

`ParallelResult` exposes `GetResult(name)`, `GetResult<T>(name)` and an indexer that returns the
branch's `Value` directly (`parallelResult["FetchUsers"]`).

`StepResult.GetParameters()` (`public virtual`) builds the template-rendering parameters by
reflection: every public instance property except `Value` and `HasError`, under both its PascalCase
and camelCase name. Indexers are skipped, so `ParallelResult`'s `this[stepName]` does not appear
in the dictionary.

---

## Abstractions

Beyond `IStep`/`IStepResult`, `AITaskAgent.Core.Abstractions` (assembly `AITaskAgent.Abstractions`) ships:

| Type | Purpose |
|------|---------|
| `IEnrichable` | Observability enrichment, by composition (replaces `IEnrichableStep`) |
| `IStepValidator` | Named semantic validator attachable to a step |
| `IStepActivator` | Step instantiation strategy (`DefaultStepActivator`) |
| `IFallbackable` | `bool SwitchToNextFallback(IStepResult error, PipelineContext context, int attempt)` |
| `IResume` | `SaveStateAsync` / `RestoreStateAsync` for suspended steps |
| `IFactory<T>` / `Factory<T>` | Resolve a service by a string key (defaults to its `Name` property) |
| `IPipeline` | The executor contract |
| `IStepRegistry` | Single, injectable registry of step types by `[StepTag]`: `Add`, `AddFromAssembly`, `TryGetType`, `Create(tag, JObject?, IServiceProvider?)` (without a provider only constructors callable with default values are tried; if none, `InvalidOperationException`), `Describe(tag)`, `Describe(Type)`, `GetRegisteredTypes()` |
| `IExtension` / `IExtensionBuilder` | A unit that registers services and steps; see [Extensions](extensions.md) and [Custom extensions](../extensibility/custom-extensions.md) |
| `IJsonSchemaStep` | A step whose input and output are described with a `JsonSchema` loaded at run time (the YAML `inputSchema` / `outputSchema` keys) |
| `IPipelineContextStorage` | Persist/restore a suspended `PipelineContext` by correlation id; `Pipeline` saves a suspended run into it and deletes the entry when a resumed run completes without error (see [IPipeline](#ipipeline--pipeline)) |

See [Human in the loop](../guides/human-in-the-loop.md) for how `IResume`, `SuspendInfo` and
`IPipelineContextStorage` fit together.
