using AITaskAgent.Core.Abstractions;

namespace AITaskAgent.LLM.Abstractions
{
    public interface IRouterResult : IStepResult
    {
        string SelectedOption { get; }

    }
}
