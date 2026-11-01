using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.StepResults;

public class InputMessage(IStep step) : StepResult<string>(step)
{


}
