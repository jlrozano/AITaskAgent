namespace AITaskAgent.LLM.Models;

/// <summary>
/// Framework-agnostic message representation.
/// Supports multi-provider conversations with smart conversion.
/// </summary>
public sealed record Message
{
    /// <summary>Message role: "system", "user", "assistant", "tool".</summary>
    public required string Role { get; init; }

    /// <summary>Message content.</summary>
    public string? Content { get; init; }

    /// <summary>Optional name for tool messages.</summary>
    public string? Name { get; init; }

    /// <summary>Optional tool call ID for tool response messages.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Optional tool calls for assistant messages requesting tool execution.</summary>
    public List<ToolCall>? ToolCalls { get; init; }

    /// <summary>
    /// Native provider-specific message object with metadata.
    /// Used for smart conversion: if current provider matches, reuse native object.
    /// </summary>
    public NativeObject? Native { get; init; }
}


