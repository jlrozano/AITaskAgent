# Steps

## Overview

Steps are the building blocks of pipelines. Each step receives input, performs work, and produces
output.

## Step Types

### ActionStep

A side-effecting step. It does **not** transform the value: it runs an action and returns an
`ActionResult` describing success or failure.

```csharp
var step = new ActionStep<StepResult<string>>(
    "AuditInput",
    async (input, context, attempt, lastResult) =>
    {
        await auditLog.WriteAsync(input.Value);
    });

// Fire-and-forget variant: returns immediately, exceptions are logged, not propagated
var background = new ActionStep<StepResult<string>>(
    "NotifyAsync",
    async (input, context, attempt, lastResult) => await notifier.SendAsync(input.Value),
    fireAndForget: true);
```

### DelegatedStep

Inline transformation. The delegate receives the step itself as its first argument so it can build
a result bound to the right instance.

```csharp
var step = new DelegatedStep<StepResult<string>, StepResult<string>>(
    "Uppercase",
    (self, input, context, attempt, lastResult) =>
        Task.FromResult(new StepResult<string>(self, input.Value!.ToUpperInvariant())));
```

Exceptions thrown by the delegate are converted into an error result rather than escaping.

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
        return CreateResult(input.Value * 2);
    }
}
```

`CreateResult(value)` and `CreateErrorResult(message, exception)` build a `TOut` for you through
`StepResultFactory`, so you never need the concrete constructor.

### GroupStep

Sequential execution of sub-steps, in the **same** pipeline context. The group appends its steps to
`NextSteps` and returns; the parent pipeline runs them in order and then resumes.

```csharp
var group = new GroupStep("ProcessGroup", [new Step1(), new Step2(), new Step3()]);

// Typed variant, constrains the input type
var typed = new GroupStep<StepResult<string>>("ProcessGroup", [new Step1(), new Step2()]);
```

Both the non-generic `GroupStep` and `GroupStep<TIn>` take `(string name, IReadOnlyList<IStep> steps)`.
The non-generic form also has a parameterless constructor plus an init-only `Steps` collection, used
by [YAML pipelines](yaml-pipelines.md).

### ParallelStep

Concurrent execution with result aggregation. The collection is called **`Branches`**.

```csharp
var parallel = new ParallelStep("FetchAll",
    new FetchUserStep(),
    new FetchOrdersStep(),
    new FetchPreferencesStep());

// Typed variant
var typed = new ParallelStep<StepResult<string>>("FetchAll", new FetchUserStep(), new FetchOrdersStep());
```

**Key behaviors:**
- Each branch runs on `context.CloneForBranch()`: (in an `LlmPipelineContext`) a **cloned** `ConversationContext` and its own
  step path, so nested results are keyed independently. That path keeps the parent's path and adds
  the parallel step's name and the branch name: a branch `FetchUserStep` of `FetchAll` caches its
  step under `{parent}/FetchAll/FetchUserStep/FetchUserStep` (for example
  `Loop/Iter_2/FetchAll/FetchUserStep/FetchUserStep` inside a loop), so loop iterations and
  same-named branches of different parallel steps never share a cache entry
- `Metadata`, `StepResults` and the `EventChannel` are **shared** across branches (thread-safe)
- Each branch emits `BranchStartedEvent` / `BranchCompletedEvent` with a unique `BranchId`
- All branches pass through the middleware chain (observability, timeout, retry)
- Concurrency is capped by `MaxDegreeOfParallelism` (init-only; `0`, the default, means
  `Environment.ProcessorCount`)
- A failing branch does not cancel the others. With `FailFast = true` (the default) every error
  message is joined into `ParallelResult.Error`, so the parallel step fails if any branch failed.
  With `FailFast = false` the step returns no aggregate error, even if every branch failed: each
  failed branch keeps its own error in its entry of `ParallelResult.Value` (`HasError` / `Error`) and
  in its `BranchCompletedEvent` (`Success = false`, `ErrorMessage`), and whoever reads the result
  decides what to do with it
- Results are keyed by each branch step's `Name`

**Accessing results:**

```csharp
var result = await pipeline.ExecuteAsync(name: "Fan-out", steps: [parallel], input: input);

if (result is ParallelResult parallelResult)
{
    IStepResult? users = parallelResult.GetResult("FetchUserStep");
    var orders = parallelResult.GetResult<OrdersResult>("FetchOrdersStep");

    // Or the whole dictionary
    IReadOnlyDictionary<string, IStepResult> all = parallelResult.Value;

    // The indexer returns the branch result's Value directly
    object? prefs = parallelResult["FetchPreferencesStep"];
}
```

### LoopStep

Runs its `Steps` repeatedly until an iteration ends with a `BreakStep` result or `MaxIterations`
is reached. The check happens after the iteration's last step: a `BreakStep` in the middle of
`Steps` does not stop the steps after it. Each iteration receives the previous iteration's final
result as input.

```csharp
var loop = new LoopStep(
    name: "RefineUntilGoodEnough",
    steps: [draftStep, criticStep, maybeBreakStep],
    maxIterations: 10)
{
    OutputIterationToMetadata = "iteration"   // exposes {{ context.iteration }} to prompts
};
```

| Member | Notes |
|--------|-------|
| `Steps` | Executed in order on every iteration |
| `MaxIterations` | Default `50` |
| `OutputIterationToMetadata` | Optional metadata key holding the 1-based iteration number |

Behaviour worth knowing:

- Each iteration pushes the path part `"{Name}/Iter_{n}"`, so cached results and events are unique
  per iteration.
- An error result or a suspension inside the loop bubbles up **immediately** without caching the
  loop itself.
- When the loop finishes because it ran out of iterations, it emits a `LoopExhaustedEvent`
  carrying `LoopName`, `IterationsCompleted` and `MaxIterations`. That event means the loop never
  converged.
- `MaxRetries` is forced to `1` — the loop is the retry mechanism.

### BreakStep

Control-flow step that exits the enclosing `LoopStep` early, tunnelling the input's value out with
it:

```csharp
var breakStep = new BreakStep("StopWhenApproved");
```

It returns a `BreakStepResult` carrying `input.Value`. `LoopStep` unwraps it into an ordinary
result, so a `BreakStep` never accidentally breaks an outer loop. Route to it conditionally — for
example from a `SwitchStep` that inspects the critic's verdict — or give it a condition: `When` (`when:` in YAML) is a
template expression over `input` and `context` (`input.done`), and the loop exits only if it is truthy; otherwise the
step hands its input on unchanged and the loop continues. Without `When` it always exits, and an empty or blank `When`
is rejected when it is set (in YAML, when the pipeline is built). See [YAML pipelines](yaml-pipelines.md).

### SwitchStep

Dynamic routing based on input:

```csharp
var router = new SwitchStep<AnalysisResult>(
    "RouteByIntent",
    (input, context) => input.Value!.Type switch
    {
        "search" => ((IStep)new SearchStep(), (IStepResult)input),
        "order"  => (new OrderStep(), input),
        _        => (new DefaultStep(), input)
    });
```

The selector returns `(IStep target, IStepResult value)`. The target is appended to
`value.NextSteps` and a `StepRoutingEvent` is emitted. Returning `null` for either element throws.

In a YAML pipeline the same routing is declared with `type: SwitchStep`, an `on` expression and `cases`/`default` that
contain child steps (the class is `DeclarativeSwitchStep`; the generic `SwitchStep<TIn>` above is the code-first form).

For LLM-driven routing over string keys, use `RouterStep` — see
[Intention Routing](intention-routing.md).

### SuspendStep

Suspends the pipeline to ask for more information. The first time it runs it returns an
`AwaitingInputResult` (`SuspendInfo`, the rendered `Message` and `DataToReview`) and the pipeline stops;
executing the pipeline again with the same context and the response as input resumes it, and the step
returns the response as its result. See [Human in the loop](human-in-the-loop.md) and
[`SuspendStep` in YAML](yaml-pipelines.md#suspendstep-requesting-more-information).

### ExecStep

Runs an external executable or batch file (`.exe`, `.bat`, `.cmd`) and returns the text of its standard
output. It is not a shell: `Executable` is a literal and each item of `Args` is rendered on its own and
passed as exactly one argument. See [`ExecStep` in YAML](yaml-pipelines.md#execstep-running-an-external-executable).

### ScriptStep

Lives in the `AITaskAgent.Extensions.Script` assembly, not in Core. Runs JavaScript (the body of
`function (input, context)`) without an LLM and returns its value as JSON. The scripts run in a worker
process, one per pipeline run. See [`ScriptStep` in YAML](yaml-pipelines.md#scriptstep-javascript-declarable)
and `AITaskAgent.Extensions.Script/README.md`.

### CSharpStep

Lives in the `AITaskAgent.Extensions.CSharp` assembly. Runs C# declared in the pipeline (YAML
`type: CSharpStep`): its `Code` is the body of `static async Task<IStepResult> Step(StepCall call)`, which
the host compiles with the other `CSharpStep`s of a package into one unit. The code runs in the host
process, not isolated, with the host's permissions.

## Step Configuration

### Retry

`MaxRetries` is a settable property on `StepBase` (default `3`), not a virtual member:

```csharp
public class MyStep : TypedStep<In, Out>
{
    public MyStep() : base("MyStep")
    {
        MaxRetries = 5;
    }
}

// or from the outside
var step = new MyStep { MaxRetries = 1 };
```

### Timeout

```csharp
// Step-level: StepBase.Timeout is a virtual get/set property
var step = new MyStep { Timeout = TimeSpan.FromSeconds(30) };

// Pipeline-level
await pipeline.ExecuteAsync(..., pipelineTimeout: TimeSpan.FromMinutes(5));
```

A null step timeout falls back to `IPipeline.DefaultStepTimeout`, which `Pipeline` always resolves
to a value: 1 minute through `AddAITaskAgent()` (`AITaskAgent:Timeouts:DefaultStepTimeout`), 30
minutes when `Pipeline` is constructed directly. The timeout of a `LoopStep` or `ParallelStep`
covers all of its children, since they run inside its single execution. See
[Resilience](resilience.md).

### Validation

Two layers run after each attempt, both handled by `RetryMiddleware`:

**1. Structural** — override `ValidateAsync` on the *result*:

```csharp
public sealed class MyResult(IStep step) : StepResult<string>(step)
{
    public override Task<(bool IsValid, string? Error)> ValidateAsync(PipelineContext context)
        => Task.FromResult(string.IsNullOrEmpty(Value)
            ? (false, (string?)"Value cannot be empty")
            : (true, (string?)null));
}
```

**2. Semantic** — pluggable validators attached to the *step*:

```csharp
public interface IStepValidator
{
    string Id { get; }
    Task<(bool IsValid, string? Error)> ValidateAsync(
        IStepResult result, PipelineContext context, CancellationToken cancellationToken);
}

step.AddValidator(new BusinessRuleValidator());
IReadOnlyList<IStepValidator> current = step.Validators;
```

`AddValidator` / `Validators` live on `StepBase`, whose `ValidateAsync` runs the validators in order
and stops at the first failure. Steps that implement `IStep` directly (`SwitchStep`, `RouterStep`)
have no validator list.

`IStepValidator` is deliberately decoupled from Core so validators can ship from external modules.
Besides it, `StepBase` has `AddResultValidator` / `ResultValidators` (`IResultValidator`): validators of
the result itself, which run as part of `IStepResult.ValidateAsync`, in the structural slot of the
retry loop. In YAML both are attached with the step keys `validators:` (`IStepValidator`) and
`resultValidators:` (`IResultValidator`, or `schema:<name>` for a registered schema), next to
`outputSchema`; see [Hooks and Validators on Any Step](yaml-pipelines.md#hooks-and-validators-on-any-step).
Each failure emits a
`StepValidationEvent` (`ValidationType` = `"structural"` or `"semantic"`) and retries with the
error message as feedback.

### Display text

Every `StepBase` step has `DisplayStart` and `DisplayEnd` (string templates, null by default) that fill
`StepStartedEvent.Display` and `StepCompletedEvent.Display` with the text the step wants shown. They use the
template engine over `step`, `input`, `context` and `data` (and `output`, `success`, `error`, `durationMs` at the
end); see [Display text](event-system.md#display-text) for the model, examples and limits.

```csharp
step.DisplayStart = "Reading {{ input.fileName }}";
step.DisplayEnd   = "{{ output.rows }} rows in {{ durationMs }} ms";
```

### Hooks

`StepBase.Hooks` (a `StepHooks`, set once when the step is built) holds the code the host attaches to a
step's extension points: `PostProcess`, `OnFinalize`, `EnrichActivityBefore` / `EnrichActivityAfter`,
`EnrichStarted` and `EnrichCompleted`. A step type declares its own hooks class with
`[HooksType(typeof(...))]`; LLM steps use `LlmStepHooks` (see [LLM Steps](llm-steps.md#hooks)). In C# the
slots that wrap built-in behaviour (`PostProcess`, and `BuildUserMessage` or `ParseResponse` in LLM
steps) are `HookAround` delegates that receive `next`, the built-in behaviour. The forms without `next`
exist in the declarative layers (JavaScript and YAML); see
[Hooks and Validators on Any Step](yaml-pipelines.md#hooks-and-validators-on-any-step).

### Observability enrichment

`StepBase` implements `IEnrichable` and exposes an `Enrichable` property for composition:

```csharp
var step = new MyStep { Enrichable = new MyEnricher() };
```

The old `IEnrichableStep` interface no longer exists. See [Event System](event-system.md).

## NextSteps (Dynamic Routing)

Steps can return additional steps to execute:

```csharp
protected override async Task<MyResult> ExecuteAsync(...)
{
    var result = CreateResult(value);

    if (needsMoreProcessing)
    {
        result.NextSteps.Add(new AdditionalStep());
    }

    return result;
}
```

The pipeline runs `NextSteps` before continuing with the next configured step. Nothing prevents
cycles — a step that adds itself loops forever.
