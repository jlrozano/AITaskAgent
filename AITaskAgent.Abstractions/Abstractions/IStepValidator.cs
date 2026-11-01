using AITaskAgent.Core.Models;

namespace AITaskAgent.Core.Abstractions;

/// <summary>
/// Represents a semantic validator for a step's result.
/// Implementations are decoupled from Core and can be provided by external modules (e.g. Js, NuGet plugins).
/// </summary>
public interface IStepValidator
{
    /// <summary>
    /// Unique identifier for this validator. Used to reference it from YAML configuration.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Validates the step result within the given pipeline context.
    /// </summary>
    /// <param name="result">The result produced by the step.</param>
    /// <param name="context">The pipeline execution context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A tuple indicating whether validation passed, and an error message if it failed.
    /// </returns>
    Task<(bool IsValid, string? Error)> ValidateAsync(
        IStepResult result,
        PipelineContext context,
        CancellationToken cancellationToken);
}
