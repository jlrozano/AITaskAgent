using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Observability;

namespace AITaskAgent.Core.Execution;

/// <summary>
/// Contract for pipeline execution.
/// Registered as singleton in DI to enable testability and remove static state.
/// </summary>
public interface IPipeline
{
    /// <summary>
    /// Default timeout for the entire pipeline execution.
    /// </summary>
    TimeSpan? DefaultPipelineTimeout { get; }

    /// <summary>
    /// Default timeout for individual steps.
    /// </summary>
    TimeSpan? DefaultStepTimeout { get; }

    /// <summary>
    /// Executes a pipeline of steps sequentially with middleware composition.
    /// </summary>
    /// <typeparam name="T">Type of the pipeline input.</typeparam>
    /// <param name="name">Pipeline name, used in events, telemetry and step paths.</param>
    /// <param name="steps">Steps to execute, in order.</param>
    /// <param name="input">Value handed to the first step.</param>
    /// <param name="context">Existing context to run within. A new one is created when omitted.</param>
    /// <param name="pipelineTimeout">Overrides <see cref="DefaultPipelineTimeout"/> for this run.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <param name="subscribers">
    /// Optional event handlers for this specific execution.
    /// The pipeline creates a local EventChannel, activates them at start,
    /// and completes them when execution finishes (success or error).
    /// Each pipeline execution is fully isolated — no cross-contamination between concurrent runs.
    /// </param>
    /// <returns>The result of the last step executed.</returns>
    Task<IStepResult> ExecuteAsync<T>(
        string name,
        IReadOnlyList<IStep> steps,
        T input,
        PipelineContext? context = null,
        TimeSpan? pipelineTimeout = null,
        CancellationToken cancellationToken = default,
        IEnumerable<Func<IProgressEvent, CancellationToken, Task>>? subscribers = null);
}
