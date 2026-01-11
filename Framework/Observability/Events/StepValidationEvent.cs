namespace AITaskAgent.Observability.Events;

/// <summary>
/// Event emitted when a step result is validated.
/// </summary>
public sealed record StepValidationEvent : ProgressEventBase
{
    /// <inheritdoc />
    public override string EventType => AITaskAgent.Core.AITaskAgentConstants.EventTypes.StepValidation;

    /// <summary>Whether the validation passed.</summary>
    public bool IsValid { get; init; }

    /// <summary>Error message if validation failed.</summary>
    public string? ValidationError { get; init; }

    /// <summary>Type of validation: "structural" (ValidateAsync) or "semantic" (resultValidator).</summary>
    public string ValidationType { get; init; } = AITaskAgent.Core.AITaskAgentConstants.ValidationTypes.Structural;

    /// <summary>Current attempt number when validation occurred.</summary>
    public int AttemptNumber { get; init; } = 1;
}
