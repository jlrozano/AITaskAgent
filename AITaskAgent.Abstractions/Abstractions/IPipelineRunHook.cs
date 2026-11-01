using AITaskAgent.Core.Models;

namespace AITaskAgent.Core.Abstractions;

/// <summary>
/// Something a kind of step needs once per run of a pipeline, before any step runs, and gives back when the run
/// ends: the JavaScript steps compile all their scripts once and keep one worker process for the run, for instance.
/// </summary>
/// <remarks>
/// <para>The pipeline calls it from <c>IPipeline.ExecuteAsync</c>, once per run (a resumed run is a new run), with the
/// steps of the run that named this hook through <see cref="IPipelineRunHookProvider"/>: the step itself, or (since 2026-10-23) one of the
/// validators or result validators of a <c>StepBase</c>, or the target object of one of its hooks (the delegate's <c>Target</c>; each step is
/// passed once per hook). Only steps whose place is known
/// before the run are passed: the top-level steps and the children of Group, Parallel, Loop and declarative Switch steps
/// (every case and the default). Steps decided at run time (<c>NextSteps</c>, the target of a programmatic switch) are
/// not, and must work without the preparation.</para>
/// <para>An exception thrown here fails the run before any step runs, as the pipeline's own validation does.</para>
/// </remarks>
public interface IPipelineRunHook
{
    /// <summary>
    /// Prepares the run. <paramref name="steps"/> pairs each step with its path in the step tree (step names joined by
    /// <c>/</c>, for example <c>"group/loop/totals"</c>), in tree order.
    /// </summary>
    /// <returns>What to dispose when the run ends, whatever the outcome (success, error, cancellation or suspension); null when there is nothing to release.</returns>
    Task<IAsyncDisposable?> RunStartingAsync(
        IReadOnlyList<KeyValuePair<string, IStep>> steps, PipelineContext context, CancellationToken cancellationToken);
}

/// <summary>
/// A step (or a validator or result validator of a <c>StepBase</c>, or the target of one of its hooks) that needs a once-per-run preparation
/// names it here; whatever returns the same instance shares one call.
/// </summary>
public interface IPipelineRunHookProvider
{
    /// <summary>The hook of this kind of step; the same instance for every step that shares the preparation.</summary>
    IPipelineRunHook RunHook { get; }
}
