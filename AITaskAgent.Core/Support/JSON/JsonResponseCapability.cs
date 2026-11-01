namespace AITaskAgent.Support.JSON;

/// <summary>
/// Structured JSON response capabilities of an LLM model.
/// Follows the OpenAI response_format standard.
/// </summary>
public enum JsonResponseCapability
{
    /// <summary>The model does not support native structured JSON responses.</summary>
    None,

    /// <summary>The model supports json_object (free JSON without schema).</summary>
    JsonObject,

    /// <summary>The model supports json_schema (JSON with schema validated by the model).</summary>
    JsonSchema
}

