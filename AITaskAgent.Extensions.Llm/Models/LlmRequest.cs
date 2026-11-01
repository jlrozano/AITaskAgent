using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Support;

namespace AITaskAgent.LLM.Models;

/// <summary>
/// Request to an LLM service using provider-agnostic types.
/// </summary>
public sealed record LlmRequest : LlmInferenceSettings
{
    /// <summary>The conversation messages.</summary>
    public required ConversationContext Conversation { get; init; }
    public bool UseSlidingWindow { get; init; } = true;
    public int SlidingWindowMaxTokens { get; init; } = 8192;
    public int MessageMaxTokens { get; init; } = 8192;
    /// <summary>Optional system prompt (prepended to messages).</summary>
    public string? SystemPrompt { get; init; }
    /// <summary>The model name to use.</summary>
    public required string Model { get; init; }
    public ResponseFormatOptions? ResponseFormat { get; init; }
    /// <summary>User identifier for tracking.</summary>
    public string? User { get; init; }
    /// <summary>Available tools for the LLM to call.</summary>
    public IList<ToolDefinition>? Tools { get; init; }
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string? ApiVersion { get; init; }
    /// <summary>
    /// Creates a shallow copy of this request.
    /// Useful for modifying request properties without mutating the original.
    /// </summary>
    public LlmRequest Copy() => this with { };
}

