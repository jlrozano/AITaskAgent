using AITaskAgent.Core.Models;

namespace AITaskAgent.Core.Abstractions;

/// <summary>
/// Represents a step in the pipeline execution.
/// </summary>
public interface IStep
{
    /// <summary>Gets the step name for observability.</summary>
    string Name { get; }
    /// <summary>
    /// Gets the maximum number of retry attempts allowed for an operation.
    /// </summary>
    int MaxRetries => 1;
    /// <summary>
    /// Gets the number of milliseconds to wait between retry attempts.
    /// </summary>
    int MiillisecondsBetweenRetries => 100;
    /// <summary>
    /// Optional timeout for this step's execution.
    /// If null, uses Pipeline.DefaultStepTimeout.
    /// </summary>
    TimeSpan? Timeout => TimeSpan.FromMinutes(1);
    /// <summary>Executes the step with the given input and execution context.</summary>
    Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken);
    /// <summary>
    /// Performs finalization logic asynchronously using the specified step result and pipeline context.
    /// </summary>
    /// <param name="input">The result of the previous step to be used during finalization. Cannot be null.</param>
    /// <param name="context">The pipeline context that provides information and services for the current operation. Cannot be null.</param>
    /// <param name="cancellationToken">A token that can be used to request cancellation of the asynchronous operation.</param>
    /// <returns>A task that represents the asynchronous finalization operation.</returns>
    Task FinalizeAsync(IStepResult input, PipelineContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    /// <summary>
    /// Asynchronously validates the specified step result within the given pipeline context.
    /// </summary>
    /// <param name="result">The step result to validate. Cannot be null.</param>
    /// <param name="context">The pipeline context in which the validation is performed. Cannot be null.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the validation operation.</param>
    /// <returns>A task that represents the asynchronous validation operation. The result contains a boolean indicating whether
    /// the step result is valid, and a string describing the error if validation fails; otherwise, null.</returns>
    Task<(bool IsValid, string? Error)> ValidateAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken) => Task.FromResult((true, (string?)null));
    /// <summary>Gets the expected input type for this step.</summary>
    Type InputType { get; }

    /// <summary>Gets the expected output type for this step.</summary>
    Type OutputType { get; }
}

public interface IStep<TIn, TOut> : IStep
    where TIn : IStepResult
    where TOut : IStepResult
{

    async Task<IStepResult> IStep.ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
    {
        return (input.GetType().IsAssignableTo(typeof(TIn)) && (lastStepResult?.GetType().IsAssignableTo(typeof(TOut)) ?? true))
            ? await ExecuteAsync((TIn)input, context, attempt, (TOut?)lastStepResult, cancellationToken)
            : throw new InvalidOperationException(
                $"Step '{Name}' expected input type '{typeof(TIn).Name}' " +
                $"but received '{input.GetType().Name}'.");

    }
    /// <summary>Executes the step with the given typed input and execution context.</summary>
    Task<TOut> ExecuteAsync(TIn input, PipelineContext context, int Attempt, TOut? lastStepResult, CancellationToken cancellationToken);

    new Type InputType => typeof(TIn);
    new Type OutputType => typeof(TOut);
}



