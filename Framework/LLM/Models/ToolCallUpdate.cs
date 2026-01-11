namespace AITaskAgent.LLM.Models;

/// <summary>
/// Framework-agnostic tool call update during streaming.
/// </summary>
public sealed record ToolCallUpdate
{
    /// <summary>Index of the tool call in the list.</summary>
    public required int Index { get; init; }

    /// <summary>Tool call ID (may be null in early updates).</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Function name (may be null in early updates).</summary>
    public string? FunctionName { get; init; }

    /// <summary>Incremental function arguments update.</summary>
    public string? FunctionArgumentsUpdate { get; init; }
}

