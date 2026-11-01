using AITaskAgent.Core.Abstractions;

namespace AITaskAgent.Core.StepResults;

/// <summary>
/// The result a <see cref="Steps.SuspendStep"/> suspends the pipeline with: what it asks for
/// (<see cref="Message"/>, also the result's value), what to look at (<c>DataToReview</c>) and,
/// when a previous response was rejected, why (<see cref="RejectionReason"/>).
/// <c>SuspendInfo</c> holds the step's marker.
/// </summary>
public sealed class AwaitingInputResult(IStep step, string? message = null) : SuspendStepResult(step, message)
{
    /// <summary>The question or reason shown to whoever answers, rendered from the step's <c>message</c>.</summary>
    public string? Message { get; init; } = message;

    /// <summary>
    /// Null on the first suspension. When a response did not match the step's <c>outputSchema</c>,
    /// the step suspends again and this holds the validation errors, so the caller can ask again.
    /// </summary>
    public string? RejectionReason { get; init; }
}
