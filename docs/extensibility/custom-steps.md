# Custom Steps

## Overview

Create custom steps by extending `StepBase` or `TypedStep<TIn, TOut>`.

## Option 1: TypedStep (Recommended)

Type-safe with automatic result creation:

```csharp
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;

public class DataTransformStep : TypedStep<DataInput, DataOutput>
{
    public DataTransformStep() : base("DataTransform")
    {
        MaxRetries = 3;
    }
    
    protected override async Task<DataOutput> ExecuteAsync(
        DataInput input,
        PipelineContext context,
        int attempt,
        DataOutput? lastResult,
        CancellationToken cancellationToken)
    {
        Logger.LogInformation("Transforming data, attempt {Attempt}", attempt);
        
        // Your transformation logic
        var transformed = await TransformAsync(input.Data, cancellationToken);
        
        // Store in metadata for later steps
        context.Metadata["transformedAt"] = DateTime.UtcNow;
        
        return CreateResult(transformed);
    }
    
    protected override Task<(bool IsValid, string? Error)> Validate(
        IStepResult result, 
        PipelineContext context, 
        CancellationToken cancellationToken)
    {
        if (result is DataOutput output && output.Value?.Length > 0)
            return Task.FromResult((true, (string?)null));
        
        return Task.FromResult((false, "Transformation produced empty result"));
    }
}
```

## Option 2: StepBase (Full Control)

For non-generic scenarios:

```csharp
public class DynamicStep : StepBase
{
    public DynamicStep() : base("Dynamic", typeof(IStepResult), typeof(IStepResult))
    {
    }
    
    protected override async Task<IStepResult> ExecuteAsync(
        IStepResult input,
        PipelineContext context,
        int attempt,
        IStepResult? lastResult,
        CancellationToken cancellationToken)
    {
        // Handle any input type
        var value = input.Value;
        
        // Create result dynamically
        return CreateResult(ProcessValue(value));
    }
}
```

## Declaring a step in YAML

A step becomes available to YAML pipelines (`type: MyStep`) when it is registered in the `IStepRegistry` under a tag. The tag comes from the
`[StepTag]` attribute (assembly `AITaskAgent.Abstractions`, namespace `AITaskAgent.Core.Attributes`); registration is done by an
[extension](custom-extensions.md) with `builder.AddStep<MyStep>()`, or with `registry.Add<MyStep>("OtherTag")` to choose the tag explicitly.

```csharp
using System.ComponentModel;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.StepResults;

[StepTag("Uppercase")]
[Description("Uppercases the text it receives.")]
public sealed class UppercaseStep : StepBase
{
    public UppercaseStep() : base("Uppercase", typeof(IStepResult), typeof(StepResult<string>)) { }

    // Public writable properties are the step's YAML configuration (camelCase keys: "suffix").
    [Description("Text appended to the result.")]
    public string? Suffix { get; set; }

    protected override Task<IStepResult> ExecuteAsync(
        IStepResult input, PipelineContext context, int attempt,
        IStepResult? lastResult, CancellationToken cancellationToken)
        => Task.FromResult(CreateResult(((input.Value?.ToString() ?? "").ToUpperInvariant()) + Suffix));
}
```

The registry creates the step with the dependency injection container (constructor arguments are resolved from `IServiceProvider`; parameters with
defaults may be left out) and then populates its public writable properties from the YAML mapping. Only types that are concrete, non-generic and carry a
tag can be registered; an open generic such as `TemplateLlmStep<,>` cannot, which is why the typed LLM steps are built in code and `LlmStep` is the only
LLM step YAML knows. The factory sets the step's `Name` to its YAML `id`. A property whose type is `List<IStep>` or `Dictionary<string, List<IStep>>` is a
container property: its YAML value is built recursively into steps (this is how `GroupStep.Steps` or `ParallelStep.Branches` work). The full
configuration contract is in [YAML pipelines](../guides/yaml-pipelines.md).

## Steps that need a specific context

A step whose logic needs more than the base `PipelineContext` (the LLM steps need the conversation) declares it by overriding `RequiredContext`:

```csharp
public override Type? RequiredContext => typeof(MyDomainContext);   // MyDomainContext : PipelineContext
```

`Pipeline.ExecuteAsync` walks the step tree (Group, Parallel and Loop included, and every case and the default of a declarative Switch) before running anything and throws `InvalidOperationException` if the
context it was given is not assignable to that type; a step reached later through `NextSteps` or a programmatic `SwitchStep<TIn>` selector is checked just before it executes and returns an
error result with the same message. Inside the step, cast with a helper that fails with a clear message, as `AsLlmContext(Name)` does for
`LlmPipelineContext`. A wrapper step that calls an LLM step internally does not get the early check unless it declares `RequiredContext` itself.

## Enriching observability

`IEnrichableStep` no longer exists. Enrichment is `IEnrichable`, and it is **composition, not
inheritance**: `StepBase` already implements `IEnrichable` and exposes the hooks as
`protected virtual` methods, so a step just overrides what it needs.

```csharp
public class ObservableStep : TypedStep<In, Out>
{
    public ObservableStep() : base("Observable") { }
    
    // protected internal in StepBase; outside the framework assembly the override is plain protected
    protected override void EnrichActivityBefore(
        Activity? activity, 
        IStepResult input, 
        PipelineContext context)
    {
        activity?.SetTag("custom.input_size", input.Value?.ToString().Length);
    }
    
    protected override void EnrichActivityAfter(
        Activity? activity, 
        IStepResult result, 
        PipelineContext context)
    {
        activity?.SetTag("custom.success", !result.HasError);
    }
    
    protected override IStepCompletedEvent EnrichCompletedEvent(
        IStepCompletedEvent baseEvent, 
        IStepResult result, 
        PipelineContext context)
    {
        return baseEvent is StepCompletedEvent completed
            ? completed with
              {
                  AdditionalData = new Dictionary<string, object?>
                  {
                      ["customMetric"] = CalculateMetric(result)
                  }
              }
            : baseEvent;
    }
}
```

Alternatively, attach an external enricher without touching the step's class:

```csharp
step.Enrichable = new MyEnricher();   // StepBase's default hooks delegate to it
```

## Hooks

Besides overriding the `protected virtual` methods, a step carries `StepBase.Hooks` (a `StepHooks`, replaced as a whole, never mutated) whose
slots a host or the YAML/JavaScript layer can set without subclassing: `PostProcess`, `OnFinalize`, `EnrichActivityBefore`,
`EnrichActivityAfter`, `EnrichStarted` and `EnrichCompleted` (`AITaskAgent.Core/Hooks/StepHooks.cs`). The default of each virtual method
dispatches its slot, so an override that does not call `base` replaces the hook.

In C#, `PostProcess` is a `HookAround<PostProcessArgs, IStepResult>`: it receives its arguments and `next`, the built-in behaviour
(`Func<Task<T>>`); not calling `next` replaces it, calling it before or after wraps it. The "no `next`" form (a function that receives
`args` and changes its writable members, such as `args.result`) only exists in JavaScript and YAML hooks, see the
[ScriptStep README](../../AITaskAgent.Extensions.Script/README.md).

```csharp
var step = new MyStep { Hooks = new StepHooks
{
    PostProcess = async (args, next) =>
    {
        IStepResult result = await next();      // the step's own value, unchanged
        return result;                          // or a replacement
    }
} };
```

`PostProcessAsync` runs after each successful attempt and before validation, and only for results without error. A step can also declare the
text its start and completion events carry with `DisplayStart` and `DisplayEnd` (YAML: `display: { start, end }`), see
[Custom events](custom-events.md#display-text).

## Custom StepResult

Step results are classes, not records, and their constructor takes the `IStep` that produced them —
that is where `StepName` and `StepType` come from.

```csharp
public class MyStepResult(IStep step, string? value = null) : StepResult<string>(step, value)
{
    public int ItemCount { get; init; }
    public TimeSpan ProcessingTime { get; init; }

    public override Task<(bool IsValid, string? Error)> ValidateAsync(PipelineContext context)
        => Task.FromResult(ItemCount > 0 ? (true, (string?)null) : (false, "No items produced"));
}
```

`ValidateAsync` on the result is the **structural** check; `IStep.ValidateAsync` (via
`StepBase.Validate`) is the **semantic** one. `RetryMiddleware` runs both, and a failure in either
consumes an attempt and feeds the error message back to the step's next attempt.

## Finalization Hook

Cleanup after all retries complete:

```csharp
protected override Task FinalizeAsync(
    IStepResult result, 
    PipelineContext context, 
    CancellationToken cancellationToken)
{
    Logger.LogInformation("Step finalized with success: {Success}", !result.HasError);
    
    // Cleanup resources
    _disposableResource?.Dispose();
    
    return Task.CompletedTask;
}
```

## Attachable Validators

Instead of overriding `Validate`, semantic checks can be attached as `IStepValidator` instances.
`StepBase.Validate` runs them in order and short-circuits on the first failure.

```csharp
public sealed class NonEmptyValidator : IStepValidator
{
    public string Id => "non-empty";

    public Task<(bool IsValid, string? Error)> ValidateAsync(
        IStepResult result, PipelineContext context, CancellationToken cancellationToken)
        => Task.FromResult(string.IsNullOrWhiteSpace(result.Value?.ToString())
            ? (false, "Result is empty")
            : (true, (string?)null));
}

step.AddValidator(new NonEmptyValidator());
```

## Fallbacks

Implement `SwitchToNextFallback` to give a step a second chance with a different backing resource.
`RetryMiddleware` calls it when an attempt fails; returning `true` resets the attempt counter to 1
and runs again. `BaseLlmStep` uses this to walk `LlmRole.Fallbacks`.

```csharp
protected override bool SwitchToNextFallback(IStepResult error, PipelineContext context, int attempt)
{
    if (_endpointIndex + 1 >= _endpoints.Count) return false;
    _endpointIndex++;
    return true;
}
```

## Suspension

`IStep.Resume` is what makes a step suspendable. `StepBase` returns itself, so overriding
`SaveStateAsync` / `RestoreStateAsync` is enough:

```csharp
protected override Task<object?> SaveStateAsync() => Task.FromResult<object?>(_partialWork);

protected override Task RestoreStateAsync(object? state, object? hitlResponse)
{
    _partialWork = state as PartialWork;
    _approval = hitlResponse as ApprovalDecision;
    return Task.CompletedTask;
}
```

Returning a result with `SuspendInfo` set while `Resume` is `null` throws.

Persisting the suspended run is the pipeline's job, not the host's. When `Pipeline.ExecuteAsync` ends suspended it saves the context in the
registered `IPipelineContextStorage` under its `CorrelationId` (`PersistSuspendedAsync`), before emitting `PipelineCompletedEvent`; with no
storage registered it only logs a warning, and if `SaveAsync` fails the exception propagates. When a resumed run completes without error it
deletes that saved context (`DeleteCompletedAsync`); a run that fails keeps it. The full flow is in
[Human in the loop](../guides/human-in-the-loop.md).
