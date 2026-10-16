using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.StepResults;
using AITaskAgent.LLM.Abstractions;
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
    public string? Reasoning { get; internal set; }

    /// <summary>
    /// Not null when the tool loop was cut by the step rather than ended by the model (see
    /// <see cref="AITaskAgent.LLM.Models.LlmResponse.ToolLoopStopMessage"/>): the result is the cut-off turn, not an answer
    /// the model chose to give.
    /// </summary>
    public string? ToolLoopStopMessage { get; internal set; }

    public string GetAssistantMessageOrDefault(string? defaultMessage = null)
    {
        string? stringValue = _assistantMessage ?? ((IStepResult)this).Value?.ToString();
        return Error != null ? Error.Message : string.IsNullOrEmpty(stringValue) ? defaultMessage ?? "Action executed successfully" : stringValue;
    }
}


/// <summary>
/// Base class for results from LLM steps, includes metrics.
/// </summary>
public class LlmStepResult<T>(IStep step) : LlmStepResult(step), ILlmStepResult<T>
{
    new public T? Value { get => (T?)base.Value; internal protected set => base.Value = value; }
}


