using AITaskAgent.LLM.Constants;

namespace AITaskAgent.LLM.Models;

/// <summary>
/// Chunk from a streaming LLM response.
/// </summary>
public sealed class LlmStreamChunk
{
    /// <summary>Content delta for this chunk.</summary>
    public required string Delta { get; init; }

    /// <summary>Whether this content is an internal thought/reasoning trace.</summary>
    public bool IsThinking { get; init; }

    /// <summary>Whether this is the final chunk.</summary>
    public bool IsComplete { get; init; }

    /// <summary>Normalized finish reason if complete.</summary>
    public FinishReason? FinishReason { get; init; }

    /// <summary>Provider-specific finish reason string.</summary>
    public string? RawFinishReason { get; init; }

    /// <summary>Total tokens used (only available in final chunk).</summary>
    public int? TokensUsed { get; init; }

    /// <summary>Tool call deltas if applicable.</summary>
    public IReadOnlyList<ToolCallUpdate>? ToolCallUpdates { get; init; }

    /// <summary>Index of the choice this chunk belongs to (for multiple candidates).</summary>
    public int ChoiceIndex { get; init; }

    /// <summary>Native provider-specific chunk object for advanced scenarios and debugging.</summary>
    public object? NativeChunk { get; init; }
}

