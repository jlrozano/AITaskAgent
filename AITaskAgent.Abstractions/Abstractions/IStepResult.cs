using AITaskAgent.Core.Models;

namespace AITaskAgent.Core.Abstractions;

/// <summary>
/// Represents the result of a step execution.
/// </summary>
public interface IStepResult
{
    /// <summary>Gets whether this is an error result.</summary>
    bool HasError => Error != null;

    /// <summary>Gets error information if HasError is true.</summary>
    IStepError? Error { get; set; }

    /// <summary>Gets or sets information related to suspension.</summary>
    object? SuspendInfo { get; set; }

    /// <summary>Validates the result content.</summary>
    Task<(bool IsValid, string? Error)> ValidateAsync(PipelineContext context)
        => Task.FromResult((true, (string?)null));
    string StepName { get; }
    string StepType { get; }
    /// <summary>Gets the result value.</summary>
    object? Value { get; }

    /// <summary>
    /// Returns additional steps to execute after this result.
    /// Used by routing steps (IntentionRouter, Switch) to dynamically determine next steps.
    /// Default implementation returns empty collection.
    /// </summary>
    List<IStep> NextSteps { get; }
}

/// <summary>
/// Strongly-typed step result interface.
/// </summary>
/// <typeparam name="T">The type of the result value.</typeparam>
public interface IStepResult<T> : IStepResult
{
    /// <summary>Gets the strongly-typed result value.</summary>
    new T? Value { get; }
}

