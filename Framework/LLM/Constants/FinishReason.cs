namespace AITaskAgent.LLM.Constants;

/// <summary>
/// Standard finish reasons for LLM responses.
/// </summary>
public enum FinishReason
{
    /// <summary>Streaming chunk (not final).</summary>
    Streaming,

    /// <summary>Natural completion (stop token reached).</summary>
    Stop,

    /// <summary>Maximum token length reached.</summary>
    Length,

    /// <summary>Tool calls requested by the LLM.</summary>
    ToolCalls,

    /// <summary>Content filtered by provider safety systems.</summary>
    ContentFilter,

    /// <summary>Unknown or provider-specific reason. Check RawFinishReason for details.</summary>
    Other
}

/// <summary>
/// Extension methods for FinishReason enum.
/// </summary>
public static class FinishReasonExtensions
{
    /// <summary>
    /// Parses a string finish reason to the FinishReason enum.
    /// Returns Other for unknown values.
    /// </summary>
    /// <param name="value">Raw finish reason string from LLM.</param>
    /// <returns>Tuple with parsed enum value and raw string if Other.</returns>
    public static (FinishReason Reason, string? Raw) Parse(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return (FinishReason.Streaming, null);

        return value.ToLowerInvariant() switch
        {
            "stop" => (FinishReason.Stop, null),
            "length" => (FinishReason.Length, null),
            "tool_calls" => (FinishReason.ToolCalls, null),
            "toolcalls" => (FinishReason.ToolCalls, null),  // SDK OpenAI devuelve PascalCase
            "content_filter" => (FinishReason.ContentFilter, null),
            "contentfilter" => (FinishReason.ContentFilter, null),  // SDK OpenAI devuelve PascalCase
            _ => (FinishReason.Other, value)
        };
    }

    /// <summary>
    /// Gets the standard string representation of the finish reason.
    /// </summary>
    public static string ToLlmString(this FinishReason reason) => reason switch
    {
        FinishReason.Stop => "stop",
        FinishReason.Length => "length",
        FinishReason.ToolCalls => "tool_calls",
        FinishReason.ContentFilter => "content_filter",
        FinishReason.Streaming => "",
        FinishReason.Other => "other",
        _ => reason.ToString().ToLowerInvariant()
    };
}
