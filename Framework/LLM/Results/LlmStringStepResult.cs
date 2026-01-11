using AITaskAgent.Core.Abstractions;
using AITaskAgent.LLM.Models;

namespace AITaskAgent.LLM.Results;

/// <summary>
/// String result from an LLM step with metrics.
/// Uses framework-agnostic ToolCall types.
/// </summary>
public sealed class LlmStringStepResult(IStep step) : LlmStepResult<string>(step)
{
    public string? Content { get => Value; init => Value = value; }
    public List<ToolCall>? ToolCalls { get; init; }

    public override Task<(bool IsValid, string? Error)> ValidateAsync()
    {
        // Valid if either Content is present OR ToolCalls are present
        var hasContent = !string.IsNullOrEmpty(Content);
        var hasToolCalls = ToolCalls != null && ToolCalls.Count > 0;

        var isValid = hasContent || hasToolCalls;
        var error = isValid
            ? null
            : "Response must have either Content or ToolCalls";

        return Task.FromResult((isValid, error));
    }
}

