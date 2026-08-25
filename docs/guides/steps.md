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
- Each branch runs on `context.CloneForBranch()`: a **cloned** `ConversationContext` and its own
  step path, so nested results are keyed independently
- `Metadata`, `StepResults` and the `EventChannel` are **shared** across branches (thread-safe)
- Each branch emits `BranchStartedEvent` / `BranchCompletedEvent` with a unique `BranchId`
- All branches pass through the middleware chain (observability, timeout, retry)

**Accessing results:**

```csharp
var result = await pipeline.ExecuteAsync(name: "Fan-out", steps: [parallel], input: input);

if (result is ParallelResult parallelResult)
{
    IStepResult? users = parallelResult.GetResult("FetchUserStep");
    var orders = parallelResult.GetResult<OrdersResult>("FetchOrdersStep");

    // Or the whole dictionary
    IReadOnlyDictionary<string, IStepResult> all = parallelResult.Value;
}
```

### LoopStep

Runs its `Steps` repeatedly until a `BreakStep` result appears or `MaxIterations` is reached.

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
example from a `SwitchStep` that inspects the critic's verdict.

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

For LLM-driven routing over string keys, use `RouterStep` — see
[Intention Routing](intention-routing.md).

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

A null step timeout falls back to `IPipeline.DefaultStepTimeout`; if that is null too, the step
runs untimed. See [Resilience](resilience.md).

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

`IStepValidator` is deliberately decoupled from Core so validators can ship from external modules,
and its `Id` is what a [YAML pipeline](yaml-pipelines.md) references. Each failure emits a
`StepValidationEvent` (`ValidationType` = `"structural"` or `"semantic"`) and retries with the
error message as feedback.

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
