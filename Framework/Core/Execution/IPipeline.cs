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
    /// <param name="subscribers">
    /// Optional event handlers for this specific execution.
    /// The pipeline creates a local EventChannel, activates them at start,
    /// and completes them when execution finishes (success or error).
    /// Each pipeline execution is fully isolated — no cross-contamination between concurrent runs.
    /// </param>
    Task<IStepResult> ExecuteAsync<T>(
        string name,
        IReadOnlyList<IStep> steps,
        T input,
        PipelineContext? context = null,
        TimeSpan? pipelineTimeout = null,
        CancellationToken cancellationToken = default,
        IEnumerable<Func<IProgressEvent, CancellationToken, Task>>? subscribers = null);
}
