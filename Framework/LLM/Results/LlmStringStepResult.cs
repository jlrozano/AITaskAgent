using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
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

    public override Task<(bool IsValid, string? Error)> ValidateAsync(PipelineContext context)
    {
        // Valid if either Content is present OR ToolCalls are present
        bool hasContent = !string.IsNullOrEmpty(Content);
        bool hasToolCalls = ToolCalls != null && ToolCalls.Count > 0;

        bool isValid = hasContent || hasToolCalls;
        string? error = isValid
            ? null
            : "Response must have either Content or ToolCalls";

        return Task.FromResult((isValid, error));
    }
}

