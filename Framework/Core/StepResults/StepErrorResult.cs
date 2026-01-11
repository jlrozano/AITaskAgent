using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;

namespace AITaskAgent.Core.StepResults;


/// <summary>
/// Error result for untyped steps.
/// Used internally by the framework when a step fails and has no specific return type.
/// </summary>
public sealed class ErrorStepResult : StepResult<string>
{
    private ErrorStepResult(IStep step, IStepError error) : base(step)
    {
        Error = error;
        Value = error.Message;
    }

    /// <summary>Creates an ErrorStepResult from an exception.</summary>
    public static ErrorStepResult FromException(IStep step, Exception ex) => new(step, new StepError
    {
        Message = ex.Message,
        OriginalException = ex
    });

    /// <summary>Creates an ErrorStepResult from a message.</summary>
    public static ErrorStepResult FromMessage(IStep step, string message) => new(step, new StepError
    {
        Message = message,
    });
}
