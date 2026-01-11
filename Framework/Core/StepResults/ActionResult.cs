using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;

namespace AITaskAgent.Core.StepResults
{
    public class ActionResult : StepResult<string>
    {

        private ActionResult(IStep step, string message, IStepError? error) : base(step)
        {
            Value = message;
            base.Error = error;
        }

        public Dictionary<string, object?> Metadata { get; init; } = [];
        public static ActionResult FromExceptionAction(IStep step, Exception ex, string? message = null) => new(step, message ?? ex.Message, new StepError
        {
            Message = ex.Message,

            OriginalException = ex
        });

        /// <summary>Crea un ErrorStepResult desde un mensaje.</summary>
        public static ActionResult FromErrorMessage(IStep step, string message) => new(step, message, new StepError
        {
            Message = message,

        });

        public static ActionResult FromSuccessMessage(IStep step, string message) => new(step, message, null);
    }
}

