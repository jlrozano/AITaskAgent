using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;

namespace AITaskAgent.Core.Execution;

/// <summary>
/// The suspend/resume state the pipeline keeps in <see cref="PipelineContext.Metadata"/>, and the
/// questions a host or a step asks about it.
/// </summary>
/// <remarks>
/// Every key is scoped by <see cref="PipelineContext.CorrelationId"/>, so nested pipelines and
/// parallel branches that share a context do not read each other's state. They live in
/// <c>Metadata</c> on purpose: that is what a context storage persists, so a suspended run carries
/// them across a restart.
/// </remarks>
public static class PipelineResume
{
    /// <summary>Key of the state <c>IResume.SaveStateAsync</c> returned when the run suspended.</summary>
    internal static string ResumeStateKey(PipelineContext context) =>
        $"__resume_state_{context.CorrelationId}__";

    /// <summary>Key of the input a resumed run was started with, until the suspended step consumes it.</summary>
    internal static string PendingResumeKey(PipelineContext context) =>
        $"__pipeline_pending_resume_{context.CorrelationId}__";

    /// <summary>Key of the cache path of the step that suspended the run.</summary>
    internal static string SuspendedStepPathKey(PipelineContext context) =>
        $"__suspended_step_path_{context.CorrelationId}__";

    /// <summary>Key of the response handed to the step being resumed, while it runs.</summary>
    internal static string ResumeResponseKey(PipelineContext context) =>
        $"__resume_response_{context.CorrelationId}__";

    /// <summary>
    /// True when <paramref name="context"/> holds a suspended run: executing it again resumes that
    /// run, and the pipeline input is taken as the response to the step that suspended.
    /// </summary>
    public static bool IsSuspended(PipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Metadata.ContainsKey(ResumeStateKey(context));
    }

    /// <summary>
    /// Cache path of the step that suspended the run (for example <c>"loop/Iter_2/ask"</c>), or null
    /// when the context holds no suspension. It is the key the step's result is cached under and the
    /// <c>TargetStepPath</c> of the <c>PipelineSuspendedEvent</c>.
    /// </summary>
    public static string? GetSuspendedStepPath(PipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Metadata.TryGetValue(SuspendedStepPathKey(context), out object? path) ? path as string : null;
    }

    /// <summary>
    /// For a step being re-run because the pipeline resumed it: the input the resumed run was started
    /// with (the response to the suspension). False for an ordinary execution. Only visible while the
    /// resumed step runs; the pipeline removes it as soon as the step returns.
    /// </summary>
    public static bool TryGetResumeResponse(PipelineContext context, out IStepResult? response)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Metadata.TryGetValue(ResumeResponseKey(context), out object? value) && value is IStepResult result)
        {
            response = result;
            return true;
        }

        response = null;
        return false;
    }
}
