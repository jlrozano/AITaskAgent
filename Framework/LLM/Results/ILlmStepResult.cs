using AITaskAgent.Core.Abstractions;
using AITaskAgent.LLM.Constants;

namespace AITaskAgent.LLM.Results;

public interface ILlmStepResult : IStepResult
{
    string AssistantMessage { get; set; }
    decimal? CostUsd { get; }
    FinishReason? FinishReason { get; }
    string? Model { get; }
    int? TokensUsed { get; }

    string GetAssistantMessageOrDefault(string? defaultMessage = null);
}

public interface ILlmStepResult<T> : ILlmStepResult, IStepResult<T>
{
}
