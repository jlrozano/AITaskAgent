namespace AITaskAgent.LLM.Configuration;

/// <summary>
/// Configurable prompt templates for LLM JSON schema injection.
/// These can be modified at runtime to customize how schemas are presented to different LLMs.
/// </summary>
public static class LlmPromptTemplates
{
    /// <summary>
    /// Template for injecting schema in system prompt (for LLMs with JsonObject support but no JsonSchema).
    /// Placeholders: {schema}
    /// </summary>
    public static string SystemPromptSchemaTemplate { get; set; } = """
        
        IMPORTANT: You MUST respond with valid JSON matching this schema:
        ```json
        {schema}
        ```
        
        Pay attention to:
        - Property descriptions explain what each field should contain
        - Required fields must be present
        - Enum values and their descriptions show valid options
        - Validation constraints (min/max, patterns, lengths) must be respected
        
        Do not include any text outside the JSON object.
        """;

    /// <summary>
    /// Template for injecting schema in user message (for LLMs without JSON support).
    /// Placeholders: {message}, {schema}
    /// </summary>
    public static string UserMessageSchemaTemplate { get; set; } = """
        {message}
        
        RESPONSE FORMAT:
        Respond with a JSON object that matches the schema below. The schema describes the STRUCTURE of your response - do NOT copy the schema metadata ($schema, type, properties) into your response.
        
        Schema:
        ```json
        {schema}
        ```
        
        CRITICAL RULES:
        1. Output ONLY the JSON object with your actual content values
        2. Fields with "type": "string" MUST be plain text strings, NOT nested JSON objects
        3. Read each property's "description" - it explains what content to provide
        4. Include ALL required fields
        5. For enum fields, use only the listed values
        6. No text or explanation outside the JSON object
        
        """;

    /// <summary>
    /// Gets the system prompt schema template with the schema injected.
    /// </summary>
    public static string FormatSystemPrompt(string schemaJson)
    {
        return SystemPromptSchemaTemplate.Replace("{schema}", schemaJson);
    }

    /// <summary>
    /// Gets the user message schema template with message and schema injected.
    /// </summary>
    public static string FormatUserMessage(string message, string schemaJson)
    {
        return UserMessageSchemaTemplate
            .Replace("{message}", message)
            .Replace("{schema}", schemaJson);
    }
}

