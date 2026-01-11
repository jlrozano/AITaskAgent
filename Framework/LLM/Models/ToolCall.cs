namespace AITaskAgent.LLM.Models;

/// <summary>
/// Framework-agnostic tool call from LLM.
/// </summary>
public sealed record ToolCall
{
    /// <summary>Unique tool call ID.</summary>
    public required string Id { get; init; }

    /// <summary>Tool/function name.</summary>
    public required string Name { get; init; }

    /// <summary>Tool arguments as JSON string.</summary>
    public required string Arguments { get; init; }

    /// <summary>Native provider-specific tool call object with metadata.</summary>
    public NativeObject? Native { get; init; }
}

