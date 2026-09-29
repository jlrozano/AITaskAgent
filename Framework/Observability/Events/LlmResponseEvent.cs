using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;

namespace AITaskAgent.Observability.Events;

/// <summary>
/// Event emitted for LLM responses (streaming chunks or final).
/// FinishReason = Streaming indicates a chunk, any other value indicates final response.
/// </summary>
public sealed record LlmResponseEvent : ProgressEventBase
{
    /// <inheritdoc />
    public override string EventType => AITaskAgent.Core.AITaskAgentConstants.EventTypes.LlmResponse;

    /// <summary>Response content from the LLM (chunk or complete).</summary>
    public required string Content { get; init; }

    /// <summary>
    /// Finish reason. Streaming = chunk, any other value = final response.
    /// </summary>
    public FinishReason FinishReason { get; init; } = FinishReason.Streaming;

    /// <summary>
    /// The index of the choice this event belongs to.
    /// </summary>
    public int ChoiceIndex { get; init; } = 0;

    /// <summary>
    /// Raw finish reason string from LLM when FinishReason is Other.
    /// </summary>
    public string? RawFinishReason { get; init; }

    /// <summary>Whether this chunk is internal thinking/reasoning content.</summary>
    public bool IsThinking { get; init; }

    /// <summary>Total tokens used (only set on final response).</summary>
    public int TokensUsed { get; init; }

    /// <summary>
    /// Prompt tokens of this call, as the service reported them. Only set on the final event of the primary choice, so
    /// summing it over the final events of a run counts every call once; null when the service reports no split.
    /// </summary>
    public int? PromptTokens { get; init; }

    /// <summary>Completion tokens of this call; set like <see cref="PromptTokens"/>.</summary>
    public int? CompletionTokens { get; init; }

    /// <summary>
    /// Cost in USD of this call, from the prices of <c>LlmProvider.Models</c>; set like <see cref="PromptTokens"/>, and
    /// null when the model has no price configured.
    /// </summary>
    public decimal? CostUsd { get; init; }

    /// <summary>LLM model used.</summary>
    public string? Model { get; init; }

    /// <summary>LLM provider used.</summary>
    public string? Provider { get; init; }

    /// <summary>Tool calls requested by the LLM (only set on final response or tool-call chunks).</summary>
    public List<ToolCall>? ToolCalls { get; init; }
}
