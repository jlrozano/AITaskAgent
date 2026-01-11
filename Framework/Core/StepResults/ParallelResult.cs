using AITaskAgent.Core.Abstractions;
using System.Collections.Concurrent;

namespace AITaskAgent.Core.StepResults;

/// <summary>
/// Result container for parallel step execution.
/// Contains results from all parallel branches keyed by step name.
/// </summary>
/// <remarks>
/// Creates a new parallel result for the specified step.
/// </remarks>
public sealed class ParallelResult(IStep step) : StepResult(step), IStepResult<IReadOnlyDictionary<string, IStepResult>>
{
    private readonly ConcurrentDictionary<string, IStepResult> _results = [];

    /// <summary>
    /// Results from all parallel branches, keyed by step name.
    /// </summary>
    public new IReadOnlyDictionary<string, IStepResult> Value => _results.AsReadOnly();

    /// <inheritdoc />
    object? IStepResult.Value => _results.AsReadOnly();

    /// <summary>
    /// Adds a result from a parallel branch.
    /// </summary>
    internal void AddResult(string stepName, IStepResult result)
    {
        _results.TryAdd(stepName, result);
    }

    /// <summary>
    /// Gets a result by step name.
    /// </summary>
    public IStepResult? GetResult(string stepName)
        => _results.TryGetValue(stepName, out var result) ? result : null;

    /// <summary>
    /// Gets a typed result by step name.
    /// </summary>
    public TResult? GetResult<TResult>(string stepName) where TResult : class, IStepResult
        => GetResult(stepName) as TResult;

    /// <summary>
    /// Indexer that provides direct access to the Value of each step result.
    /// Enables template syntax like {{StepName}} instead of {{Value.StepName.Value}}.
    /// </summary>
    /// <param name="stepName">Name of the step whose value to retrieve.</param>
    /// <returns>The Value property of the step result, or null if not found.</returns>
    public object? this[string stepName]
        => GetResult(stepName)?.Value;
}
