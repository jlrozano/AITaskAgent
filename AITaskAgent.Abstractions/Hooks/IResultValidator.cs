using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;

namespace AITaskAgent.Core.Hooks;

/// <summary>
/// Validates the response itself ("is it well formed"), as opposed to <see cref="IStepValidator"/>
/// ("is it acceptable for this step"). Attached to a result by its step and run by
/// <c>StepResult.ValidateAsync</c>, so it takes part in the structural validation slot of the retry loop.
/// </summary>
public interface IResultValidator
{
    /// <summary>Name the validator is referenced by (the YAML <c>resultValidators</c> entry).</summary>
    string Id { get; }

    /// <summary>Validates <paramref name="result"/>; an error message makes the attempt fail and retry.</summary>
    Task<(bool IsValid, string? Error)> ValidateAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken);
}
