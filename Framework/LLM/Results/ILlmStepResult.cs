using AITaskAgent.Core.Abstractions;

namespace AITaskAgent.LLM.Results;

public interface ILlmStepResult : IStepResult
{
    string AssistantMessage { get; set; }
    decimal? CostUsd { get; }
    string? FinishReason { get; }
    string? Model { get; }
    int? TokensUsed { get; }

    string GetAssistantMessageOrDefault(string? defaultMessage = null);
}

public interface ILlmStepResult<T> : ILlmStepResult, IStepResult<T>
{
}
