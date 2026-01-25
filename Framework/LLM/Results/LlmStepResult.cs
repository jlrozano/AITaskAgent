using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.StepResults;
using AITaskAgent.LLM.Constants;

namespace AITaskAgent.LLM.Results;

public class LlmStepResult(IStep step) : StepResult(step), ILlmStepResult
{
    private string? _assistantMessage;
    public decimal? CostUsd { get; internal set; }
    public FinishReason? FinishReason { get; internal set; }
    public string? Model { get; internal set; }
    public int? TokensUsed { get; internal set; }
    public string AssistantMessage { get => GetAssistantMessageOrDefault(); set => _assistantMessage = value; }

    public string GetAssistantMessageOrDefault(string? defaultMessage = null)
    {
        var stringValue = _assistantMessage ?? ((IStepResult)this).Value?.ToString();
        return Error != null ? Error.Message : string.IsNullOrEmpty(stringValue) ? defaultMessage ?? "Action executed successfully" : stringValue;
    }
}


/// <summary>
/// Base class for results from LLM steps, includes metrics.
/// </summary>
public class LlmStepResult<T>(IStep step) : LlmStepResult(step), ILlmStepResult<T>
{
    new public T? Value { get => (T?)_value; internal protected set => _value = value; }
}


