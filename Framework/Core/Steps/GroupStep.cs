using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;

namespace AITaskAgent.Core.Steps;

/// <summary>
/// Groups multiple steps for sequential execution within the parent pipeline.
/// Unlike PipelineStep, does NOT create a nested pipeline - instead returns 
/// the input with NextSteps set to the internal step list, letting the parent
/// Pipeline execute them in sequence.
/// </summary>
/// <typeparam name="TIn">Input type (also output type - pass-through).</typeparam>
public sealed class GroupStep<TIn> : TypedStep<TIn, TIn>
    where TIn : IStepResult

{
    private readonly IReadOnlyList<IStep> _steps;

    /// <summary>
    /// Creates a GroupStep with the specified steps.
    /// </summary>
    /// <param name="name">Name of this step group.</param>
    /// <param name="steps">Steps to execute sequentially.</param>
    public GroupStep(string name, IReadOnlyList<IStep> steps) : base(name)
    {
        ArgumentNullException.ThrowIfNull(steps, nameof(steps));
        _steps = steps;

        // Validate first step accepts TIn
        if (_steps.Count > 0 && !_steps[0].InputType.IsAssignableFrom(typeof(TIn)))
        {
            throw new ArgumentException(
                $"First step '{_steps[0].Name}' expects input type '{_steps[0].InputType.Name}' " +
                $"but GroupStep input is '{typeof(TIn).Name}'.");
        }
        MaxRetries = 1; // No retries for GroupStep itself
        OutputType = _steps.Count > 0 ? _steps[_steps.Count - 1].OutputType : typeof(TIn);
    }

    /// <summary>
    /// Gets the steps contained in this group.
    /// </summary>
    public IReadOnlyList<IStep> Steps => _steps;

    /// <summary>
    /// Returns the input with NextSteps set to the internal step list.
    /// The parent Pipeline will execute these steps sequentially.
    /// </summary>
    protected override Task<TIn> ExecuteAsync(TIn input, PipelineContext context, int Attempt, TIn? LastStepResult, CancellationToken cancellationToken)
    {
        input.NextSteps.Clear();
        input.NextSteps.AddRange(_steps);
        return Task.FromResult(input);
    }

}
