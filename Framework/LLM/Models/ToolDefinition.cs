namespace AITaskAgent.LLM.Models;

/// <summary>
/// Framework-agnostic tool definition.
/// </summary>
public sealed record ToolDefinition
{
    /// <summary>Tool name.</summary>
    public required string Name { get; init; }

    /// <summary>Tool description.</summary>
    public required string Description { get; init; }

    /// <summary>JSON Schema for tool parameters.</summary>
    public required string ParametersJsonSchema { get; init; }

    /// <summary>Native provider-specific tool object with metadata.</summary>
    public NativeObject? Native { get; init; }
}

