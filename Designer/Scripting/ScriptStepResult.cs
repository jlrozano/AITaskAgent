namespace AITaskAgent.Designer.Scripting;

using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.StepResults;

/// <summary>
/// Result type for script-based steps.
/// </summary>
public class ScriptStepResult : StepResult
{
    public ScriptStepResult(IStep step, object? value = null) : base(step, value)
    {
    }

    /// <summary>
    /// Creates a success result.
    /// </summary>
    public static ScriptStepResult Success(IStep step, object? value = null) =>
    new(step, value);

    /// <summary>
    /// Creates an error result.
    /// </summary>
    public static ScriptStepResult WithError(IStep step, string message, Exception? ex = null)
    {
        var result = new ScriptStepResult(step)
        {
            Error = new ScriptStepError(message, ex)
        };
        return result;
    }
}
