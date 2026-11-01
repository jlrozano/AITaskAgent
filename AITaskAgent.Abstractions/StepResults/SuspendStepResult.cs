using AITaskAgent.Core.Abstractions;

namespace AITaskAgent.Core.StepResults;

/// <summary>
/// Represents a step result that requires suspending the pipeline for human input.
/// Stores the internal state needed to resume the step execution later.
/// </summary>
/// <remarks>
/// Initializes a new instance of the SuspendStepResult.
/// </remarks>
/// <param name="step">The step that is requesting suspension.</param>
/// <param name="state">The operational state to save for when the step resumes.</param>
public class SuspendStepResult(IStep step, object? state = null) : StepResult<object>(step, state)
{


    /// <summary>
    /// Gets the data to be reviewed by the human user.
    /// </summary>
    public object? DataToReview { get; init; }
}
