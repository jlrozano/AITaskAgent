using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Support;

namespace AITaskAgent.LLM.Models;

/// <summary>
/// Request to an LLM service using provider-agnostic types.
/// </summary>
public sealed record LlmRequest
{
    /// <summary>The conversation messages.</summary>
    public required ConversationContext Conversation { get; init; }
    public bool UseSlidingWindow { get; init; } = true;
    public int SlidingWindowMaxTokens { get; init; } = 8192;
    public int MessageMaxTokens { get; init; } = 8192;
    /// <summary>Optional system prompt (prepended to messages).</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>LLM profile name to use (e.g., "default", "fast", "reasoning"). Model comes from profile configuration.</summary>
    public required LlmProviderConfig Profile { get; init; }

    /// <summary>Temperature for response randomness (0.0-2.0).</summary>
    public float? Temperature { get; init; }

    /// <summary>Maximum tokens to generate.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Response format configuration.</summary>
    public ResponseFormatOptions? ResponseFormat { get; init; }

    /// <summary>Stop sequences.</summary>
    public string[]? Stop { get; init; }

    /// <summary>Nucleus sampling (0.0-1.0).</summary>
    public float? TopP { get; init; }

    /// <summary>Top-K sampling.</summary>
    public int? TopK { get; init; }

    /// <summary>Frequency penalty (-2.0 to 2.0).</summary>
    public float? FrequencyPenalty { get; init; }

    /// <summary>Presence penalty (-2.0 to 2.0).</summary>
    public float? PresencePenalty { get; init; }

    /// <summary>Enable reasoning/thinking output (o1, Gemini Thinking, DeepSeek R1). Overrides Profile.</summary>
    public bool? EnableThinking { get; init; }

    /// <summary>Budget for thinking tokens. Overrides Profile.</summary>
    public int? ThinkingBudget { get; init; }

    /// <summary>Number of chat completion choices to generate. Overrides Profile.</summary>
    public int? ChoiceCount { get; init; }

    /// <summary>User identifier for tracking.</summary>
    public string? User { get; init; }

    /// <summary>Available tools for the LLM to call.</summary>
    public List<ToolDefinition>? Tools { get; init; }

    /// <summary>Whether to use streaming mode for this request.</summary>
    public bool UseStreaming { get; init; } = false;

    /// <summary>
    /// Creates a shallow copy of this request.
    /// Useful for modifying request properties without mutating the original.
    /// </summary>
    public LlmRequest Copy() => this with { };
}

