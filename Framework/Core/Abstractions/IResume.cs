namespace AITaskAgent.Core.Abstractions;

/// <summary>
/// Interface for saving and restoring step state.
/// </summary>
public interface IResume
{
    /// <summary>
    /// Saves the state of the calling step asynchronously.
    /// </summary>
    /// <param name="callingStep">The step that is calling the save operation.</param>
    /// <returns>A task that represents the asynchronous save operation. The result contains an object representing the saved state, or null if no state is needed.</returns>
    Task<object?> SaveStateAsync(IStep callingStep);

    /// <summary>
    /// Restores the state of the step asynchronously.
    /// </summary>
    /// <param name="step">The step to restore state to.</param>
    /// <param name="state">The state object to restore.</param>
    /// <param name="ResumeResponse"> The hitl response</param>
    Task RestoreStateAsync(IStep step, object? state, object? ResumeResponse);
}
