namespace AITaskAgent.Observability;

/// <summary>
/// Represents the status of a step execution.
/// </summary>
public enum StepStatus
{
    /// <summary>Step has started execution.</summary>
    Started,

    /// <summary>Step is in progress.</summary>
    InProgress,

    /// <summary>Step is retrying after a failure.</summary>
    Retrying,

    /// <summary>Step completed successfully.</summary>
    Completed,

    /// <summary>Step failed.</summary>
    Failed
}

