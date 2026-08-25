# Designer — parked

This project is **not part of the build**. It is excluded from `AITaskAgent.slnx` deliberately, and
the code is kept here to be picked up again once the YAML engine has settled.

## What it is

A dynamic step registry and scripting engine for a visual pipeline designer: register step types by
tag, describe them with a JSON Schema so a UI can render a form, assemble a `PipelineSchema` from
JSON, and run JavaScript steps. Around 1.400 lines, ported from an earlier rules engine — each class
carries an `Equivalent to BRMS <X>` comment.

## Why it is parked

It overlaps with the YAML pipeline engine, which solves the same problem — describing a pipeline as
data rather than code — and is further along: it compiles JSON Schemas into real C# types, validates
the graph before running, and has a working sample. Two competing answers to one question is worse
than one, so this one waits until the YAML engine's shape is settled and it is clear what a designer
should build on.

It also has no consumers. Nothing in the repository references it, and it has not been touched since
January 2026.

## What to adapt when bringing it back

It was written against an older core, so expect these:

- **`IStepResult.Step` is gone.** Results now expose `StepName` and `StepType` instead. Affects
  `ScriptStepResult` and anything reading the producing step back off a result.
- **`IStep` gained `IResume? Resume`** and **`IStepResult` gained `object? SuspendInfo`**. Types here
  implement `IStep` directly rather than deriving from `StepBase`, so they must supply both.
- **`PipelineContext.CurrentPath` is now `CurrentStepPath`**, and the context carries a `Runtime`
  reference to the engine executing it.
- **`StepManager` collides by name** with `Framework/Core/Steps/StepManager.cs`, which arrived with
  the merge and does much the same job — registering step types by tag and building them from
  configuration. Before adapting this one, decide whether it should exist at all or whether the
  designer should use the framework's.

## The open decision

This project uses **Jint** for JavaScript; the YAML engine uses **ClearScript V8** for schema
validation. Shipping two JavaScript engines in one product is hard to justify. Whichever survives,
that choice should be made before this is revived rather than after.

## Housekeeping

Several files here are unindented, every line flush to column zero. Worth a formatting pass at the
same time.
