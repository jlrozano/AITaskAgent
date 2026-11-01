namespace AITaskAgent.Core.Models;

/// <summary>
/// Exception thrown when a deterministic validation step fails.
/// </summary>
public class StepValidationException : Exception
{
    public string StepName { get; }
    public string ValidationError { get; }

    public StepValidationException(string stepName, string validationError)
        : base($"Step '{stepName}' failed validation: {validationError}")
    {
        StepName = stepName;
        ValidationError = validationError;
    }

    public StepValidationException(string stepName, string validationError, Exception innerException)
        : base($"Step '{stepName}' failed validation: {validationError}", innerException)
    {
        StepName = stepName;
        ValidationError = validationError;
    }
}

