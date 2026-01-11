namespace AITaskAgent.LLM.Models;

/// <summary>
/// Response from an LLM service using provider-agnostic types.
/// </summary>
public sealed class LlmResponse
{
    /// <summary>The generated content.</summary>
    public required string Content { get; init; }

    /// <summary>Total tokens used (prompt + completion).</summary>
    public int? TokensUsed { get; init; }

    /// <summary>Prompt tokens used.</summary>
    public int? PromptTokens { get; init; }

    /// <summary>Completion tokens generated.</summary>
    public int? CompletionTokens { get; init; }

    /// <summary>Estimated cost in USD.</summary>
    public decimal? CostUsd { get; init; }

    /// <summary>Reason the generation finished.</summary>
    public string? FinishReason { get; init; }

    /// <summary>Tool calls requested by the LLM.</summary>
    public List<ToolCall>? ToolCalls { get; init; }

    /// <summary>Model used for generation.</summary>
    public string? Model { get; init; }

    /// <summary>Native provider-specific response object for advanced scenarios.</summary>
    public object? NativeResponse { get; init; }
}

