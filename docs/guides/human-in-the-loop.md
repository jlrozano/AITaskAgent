# Human-in-the-Loop: suspending and resuming a pipeline

Some steps cannot finish on their own. A step may need a person to approve a draft, choose between
options, or supply a value the model could not infer. Rather than blocking a thread until that
happens — which does not survive a restart, a deploy, or a user who answers tomorrow — a step can
**suspend** the pipeline. The run stops, its state is captured, and a later call resumes it,
skipping everything that already succeeded.

## The shape of it

```
run 1:  StepA ──► StepB ──► StepC (suspends)          ✗ StepD
                                    │
                            state captured, PipelineSuspendedEvent
                                    │
                              (hours later)
                                    │
run 2:  StepA ─╌╌► StepB ─╌╌► StepC (resumes with the answer) ──► StepD
        └── skipped, results served from the context ──┘
```

The fast-forward is not an optimisation detail — it is the point. Steps that already called an LLM
or wrote to a database must not run twice because someone took an hour to click a button.

## Suspending

A step suspends by returning a result whose `SuspendInfo` is set. `SuspendStepResult` is the
ready-made one:

```csharp
protected override Task<IStepResult> ExecuteAsync(
    IStepResult input, PipelineContext context, int attempt,
    IStepResult? lastStepResult, CancellationToken cancellationToken)
{
    if (_answer is null)
    {
        return Task.FromResult<IStepResult>(new SuspendStepResult(this, state: _draft)
        {
            SuspendInfo = "awaiting-approval",   // any non-null marker
            DataToReview = _draft                // what the person should look at
        });
    }

    return Task.FromResult<IStepResult>(new StepResult<string>(this, _answer));
}
```

`DataToReview` is what you show the person. `state` is what the step needs to pick up where it left
off, and it reaches the step again through `IResume`.

## Saving and restoring step state

`StepBase` implements `IResume` against itself, so a step overrides two hooks:

```csharp
protected override Task<object?> SaveStateAsync()
    => Task.FromResult<object?>(_draft);

protected override Task RestoreStateAsync(object? state, object? hitlResponse)
{
    _draft  = state as string;
    _answer = hitlResponse as string;   // what the human supplied
    return Task.CompletedTask;
}
```

`StatelessTemplateLlmStep` already saves its whole conversation this way, so an LLM step that
suspends mid-tool-call resumes with its history intact.

## What the pipeline does

On suspension it stores the step's state in the context under a key scoped by `CorrelationId`,
emits a `PipelineSuspendedEvent` carrying `TargetStepPath`, `IsBranchSuspension` and `BranchName`,
and returns the suspended result up the chain.

Crucially, results are cached **only after a step and all its `NextSteps` have succeeded**. That is
what makes the fast-forward correct: anything in the cache is genuinely finished, and anything
half-done runs again.

## Resuming

Resuming is three calls, and the first one is yours:

```csharp
// 1. When the pipeline suspends, persist the context. The framework does NOT do this for you.
var storage = provider.GetRequiredService<IPipelineContextStorage>();
await storage.SaveAsync(context.CorrelationId, context, ct);

// 2. Later, load it back by the same id.
var resumed = await PipelineContextFactory.CreateFromId(correlationId);

// 3. Run the same steps again, passing the human's answer as the input.
var result = await pipeline.ExecuteAsync(
    name: "Approval",
    steps: steps,
    input: new StepResult<string>(new EmptyStep("Human"), "approved"),
    context: resumed);
```

The pipeline recognises a resume by the saved state already present in the context. It replays the
completed steps from cache, reaches the suspended one, calls its `RestoreStateAsync` with both the
saved state and your input, and carries on.

`InMemoryPipelineContextStorage` ships with the framework and is fine for a single process. Anything
that must survive a restart needs a real `IPipelineContextStorage` — Redis, a database, a file.

## Observing it

| Event | When |
|---|---|
| `PipelineSuspendedEvent` | a step asked for input; carries the step path and, for parallel work, the branch |
| `PipelineResumingEvent` | a resume has started |
| `PipelineResumedEvent` | the suspended step has been rehydrated and execution continues |
| `StepCachedHitEvent` | a completed step was served from cache instead of re-running |

Subscribing with the run's `CorrelationId` gives you just that run's events, which is usually what a
UI wants.

## Suspending inside branches and loops

`ParallelStep` marks a suspension raised inside a branch (`IsBranchSuspension`, `BranchName`), and
each branch runs with its own conversation and step path, so one branch waiting for a person does
not disturb the others. `LoopStep` keys its cache per iteration, so resuming re-enters the iteration
that suspended rather than restarting the loop.

## Current limits

Worth knowing before you build on this:

- **Persisting the context is your job.** Nothing in the framework calls
  `IPipelineContextStorage.SaveAsync`; it only reads through `PipelineContextFactory.CreateFromId`.
- **`ResumeRequest` is not wired up.** The type exists and describes the right shape
  (`TargetStepPath`, `UserInput`, `Metadata`), but no code path consumes it. Resuming today means
  passing the answer as the pipeline input, as shown above.
- **The correlation id is generated, not chosen.** `PipelineContextFactory.Create` always mints a
  new `Guid`, so you have to read `context.CorrelationId` after the fact rather than supplying your
  own — awkward when you want to key a resume off a business id.

## See also

- [Event System](event-system.md) — subscribing by correlation id
- [Steps](steps.md) — `LoopStep`, `BreakStep`, and the step lifecycle
