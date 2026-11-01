namespace AITaskAgent.Core.Models;

/// <summary>
/// Encapsulates the configuration required to resume a suspended pipeline execution.
/// </summary>
public class ResumeRequest
{
    /// <summary>
    /// The exact path to the step that was awaiting input (e.g., "Loop1/Iter_10/AskUser").
    /// </summary>
    public string TargetStepPath { get; set; } = string.Empty;

    /// <summary>
    /// The actual data provided by the human, which will be injected back into the suspended step.
    /// </summary>
    public object UserInput { get; set; } = new();

    /// <summary>
    /// An optional custom state provided by the user application to inject into the pipeline logic upon resuming.
    /// </summary>
    public object? Metadata { get; set; }
}
