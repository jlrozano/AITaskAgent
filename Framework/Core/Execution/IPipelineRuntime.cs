using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace AITaskAgent.Core.Execution;

/// <summary>
/// What a running pipeline offers to the context it is executing.
/// </summary>
/// <remarks>
/// Separate from <see cref="IPipeline"/>, which is the surface an application calls. This one is
/// the surface the machinery calls back into: composite steps need to run their children, and every
/// step needs a logger.
///
/// It exists so the context can hold a reference to the engine running it instead of asking the
/// container for it. A context without an engine is a contradiction — it is the state of one
/// execution — and resolving <c>IPipeline</c> from <c>ServiceProvider</c> to find the engine that is
/// already executing you is service location, with the further cost that nothing works without a
/// container.
/// </remarks>
public interface IPipelineRuntime
{
    /// <summary>
    /// Logger factory for everything running under this pipeline. Never null: a pipeline built
    /// without one falls back to <c>NullLoggerFactory</c>, so a step can always log.
    /// </summary>
    ILoggerFactory LoggerFactory { get; }

    /// <summary>
    /// Runs a sub-pipeline, honouring the <c>NextSteps</c> each step injects.
    /// </summary>
    /// <remarks>
    /// Used by the composite steps — <c>ParallelStep</c>, <c>LoopStep</c>, routers — to execute
    /// what they contain through the same middleware chain as a top-level run.
    /// </remarks>
    /// <param name="steps">Steps to execute.</param>
    /// <param name="currentResult">Result feeding the first step.</param>
    /// <param name="context">Context to run within; for a branch, its own clone.</param>
    /// <param name="pipelineName">Name used in events and telemetry.</param>
    /// <param name="cancellationToken">Cancels the sub-pipeline.</param>
    /// <param name="isBranchScope">True when this is one branch of a parallel step.</param>
    Task<IStepResult> ExecuteStepsWithNextStepsAsync(
        IReadOnlyList<IStep> steps,
        IStepResult currentResult,
        PipelineContext context,
        string pipelineName,
        CancellationToken cancellationToken,
        bool isBranchScope = false);
}
