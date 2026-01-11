using AITaskAgent.Core.Abstractions;

namespace AITaskAgent.Core.Models;

/// <summary>
/// Structured error information for pipeline steps.
/// Allows communicating errors without throwing exceptions.
/// </summary>
public sealed record StepError : IStepError
{
    /// <summary>Descriptive error message.</summary>
    public required string Message { get; init; }

    /// <summary>Original exception if the error comes from a caught exception.</summary>
    public Exception? OriginalException { get; init; }
}

