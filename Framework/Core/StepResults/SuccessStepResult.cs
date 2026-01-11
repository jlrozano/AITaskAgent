using AITaskAgent.Core.Abstractions;

namespace AITaskAgent.Core.StepResults
{
    public sealed class SuccessStepResult : StepResult<string>
    {
        public SuccessStepResult(IStep step, string message) : base(step)
        {
            Value = message;
        }
    }
}

