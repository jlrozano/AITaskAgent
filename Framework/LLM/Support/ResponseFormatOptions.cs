using NJsonSchema;

namespace AITaskAgent.LLM.Support;

/// <summary>
/// Response format configuration for LLM outputs.
/// </summary>
public sealed record ResponseFormatOptions
{
    /// <summary>
    /// Format type. Common values:
    /// - "text" (default) - Plain text response
    /// - "json_object" - JSON mode without schema
    /// - "json_schema" - Structured JSON with schema validation
    /// </summary>
    public ResponseFormatType Type { get; init; } = ResponseFormatType.Text;

    /// <summary>
    /// JSON schema for structured outputs (only used when Type is "json_schema").
    /// Should be a valid JSON Schema string.
    /// </summary>
    public JsonSchema? JsonSchema { get; init; }

}

public enum ResponseFormatType
{
    Text,
    JsonObject
}

