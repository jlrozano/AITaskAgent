using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITaskAgent.Core.Execution;

/// <summary>
/// Base for pipeline engines, implementing <see cref="IPipelineRuntime"/> and nothing else.
/// </summary>
/// <remarks>
/// Deliberately does not implement <see cref="IPipeline"/>. A future engine offering a different
/// public surface derives from here and gets the runtime side — logging and sub-pipeline execution,
/// the parts the context and the composite steps depend on — without inheriting today's
/// application-facing contract.
/// </remarks>
public abstract class PipelineRuntimeBase : IPipelineRuntime
{
    /// <summary>
    /// Creates the runtime, falling back to <c>NullLoggerFactory</c> so that
    /// <see cref="LoggerFactory"/> is never null and no caller has to guard against it.
    /// </summary>
    protected PipelineRuntimeBase(ILoggerFactory? loggerFactory)
        => LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    /// <inheritdoc/>
    public ILoggerFactory LoggerFactory { get; }

    /// <inheritdoc/>
    public abstract Task<IStepResult> ExecuteStepsWithNextStepsAsync(
        IReadOnlyList<IStep> steps,
        IStepResult currentResult,
        PipelineContext context,
        string pipelineName,
        CancellationToken cancellationToken,
        bool isBranchScope = false);
}
