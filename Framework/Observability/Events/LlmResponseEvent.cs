using AITaskAgent.LLM.Constants;

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
    /// Raw finish reason string from LLM when FinishReason is Other.
    /// </summary>
    public string? RawFinishReason { get; init; }

    /// <summary>Whether this chunk is internal thinking/reasoning content.</summary>
    public bool IsThinking { get; init; }

    /// <summary>Total tokens used (only set on final response).</summary>
    public int TokensUsed { get; init; }

    /// <summary>LLM model used.</summary>
    public string? Model { get; init; }

    /// <summary>LLM provider used.</summary>
    public string? Provider { get; init; }
}
