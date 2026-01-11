using AITaskAgent.LLM.Support;

namespace AITaskAgent.LLM.Models;


/// <summary>
/// Configuration options for LLM invocation.
/// </summary>
public sealed record LlmOptions
{
    /// <summary>Model identifier (e.g., "gpt-4", "claude-3-sonnet").</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Sampling temperature (0.0-2.0). Higher = more random.
    /// Default varies by model, typically 0.7-1.0.
    /// </summary>
    public float? Temperature { get; init; }

    /// <summary>Maximum tokens to generate in the response.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Response format configuration.
    /// Defaults to text mode if not specified.
    /// </summary>
    public ResponseFormatOptions? ResponseFormat { get; init; }

    /// <summary>
    /// Stop sequences. Generation stops when any of these strings is encountered.
    /// Useful for structured outputs or preventing unwanted continuation.
    /// </summary>
    public string[]? Stop { get; init; }

    /// <summary>
    /// Nucleus sampling threshold (0.0-1.0). Alternative to temperature.
    /// Only tokens with cumulative probability less-equals TopP are considered.
    /// E.g., 0.9 means "use tokens that make up top 90% probability mass".
    /// </summary>
    public float? TopP { get; init; }

    /// <summary>
    /// Top-K sampling. Only the K most likely tokens are considered.
    /// Used by some models (e.g., Gemini). Typically 40-100.
    /// </summary>
    public int? TopK { get; init; }

    /// <summary>
    /// Frequency penalty (-2.0 to 2.0). Penalizes tokens based on their frequency in the text so far.
    /// Positive values reduce repetition of exact tokens.
    /// </summary>
    public float? FrequencyPenalty { get; init; }

    /// <summary>
    /// Presence penalty (-2.0 to 2.0). Penalizes tokens that have appeared at all.
    /// Positive values encourage topic diversity.
    /// </summary>
    public float? PresencePenalty { get; init; }

    /// <summary>
    /// Seed for reproducible outputs. When set, the model attempts to generate
    /// deterministic results for the same inputs.
    /// Note: Not all models support this.
    /// </summary>
    public long? Seed { get; init; }

    /// <summary>
    /// Names of tools to make available to the LLM.
    /// Tools will be resolved from IToolRegistry using these names.
    /// </summary>
    public string[]? ToolNames { get; init; }

    /// <summary>
    /// User identifier for tracking and rate limiting (optional).
    /// </summary>
    public string? User { get; init; }
}

