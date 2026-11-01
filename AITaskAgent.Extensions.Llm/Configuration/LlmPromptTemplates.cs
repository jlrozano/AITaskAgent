namespace AITaskAgent.LLM.Configuration;

/// <summary>
/// Default prompt templates for LLM JSON schema injection.
/// Templates are read-only constants. To override one for a single call, pass
/// <c>customTemplate</c> to <see cref="FormatSystemPrompt"/> or <see cref="FormatUserMessage"/>.
/// </summary>
public static class LlmPromptTemplates
{
    /// <summary>
    /// Default template for injecting schema in the system prompt.
    /// Used for LLMs with JsonObject support but no native JsonSchema enforcement.
    /// Placeholder: <c>{schema}</c>
    /// </summary>
    public static readonly string DefaultSystemPromptSchemaTemplate = """
        
        
        **IMPORTANT**: If you do not call any tool, you MUST respond with a plain text JSON object 
        conforming to this schema:

        ```json
        {schema}
        ```

        This schema applies ONLY to your text response, never to tool arguments.

        Pay attention to:
        - Property descriptions explain what each field should contain
        - Required fields must be present
        - Enum values and their descriptions show valid options
        - Validation constraints (min/max, patterns, lengths) must be respected

        Do not include any text outside the JSON object.
        
        """;

    /// <summary>
    /// Default template for injecting schema in the user message.
    /// Used for LLMs without native JSON mode support.
    /// Placeholders: <c>{message}</c>, <c>{schema}</c>
    /// </summary>
    public static readonly string DefaultUserMessageSchemaTemplate = """
        {message}

        **RESPONSE FORMAT**

        If you do not call any tool, you MUST respond with a plain text JSON object 
        conforming to this schema:
        
        ```json
        {schema}
        ```
        
        This schema applies ONLY to your text response, never to tool arguments.
       
        CRITICAL RULES:
        1. Output ONLY the JSON object with your actual content values
        2. Fields with "type": "string" MUST be plain text strings, NOT nested JSON objects
        3. Read each property's "description" - it explains what content to provide
        4. Include ALL required fields
        5. For enum fields, use only the listed values
        6. No text or explanation outside the JSON object

        """;

    // ── Back-compat aliases (read-only; use LlmProviderConfig for per-profile overrides) ──

    /// <summary>
    /// The active system-prompt schema template.
    /// Read-only — equals <see cref="DefaultSystemPromptSchemaTemplate"/>.
    /// To override, pass <c>customTemplate</c> to <see cref="FormatSystemPrompt"/>.
    /// </summary>
    public static string SystemPromptSchemaTemplate => DefaultSystemPromptSchemaTemplate;

    /// <summary>
    /// The active user-message schema template.
    /// Read-only — equals <see cref="DefaultUserMessageSchemaTemplate"/>.
    /// To override, pass <c>customTemplate</c> to <see cref="FormatUserMessage"/>.
    /// </summary>
    public static string UserMessageSchemaTemplate => DefaultUserMessageSchemaTemplate;

    // ── Format helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the system-prompt schema template with <c>{schema}</c> replaced by
    /// <paramref name="schemaJson"/>.
    /// </summary>
    /// <param name="schemaJson">The JSON schema string to embed.</param>
    /// <param name="customTemplate">
    /// Optional per-profile override. When <c>null</c>, <see cref="DefaultSystemPromptSchemaTemplate"/>
    /// is used.
    /// </param>
    public static string FormatSystemPrompt(string schemaJson, string? customTemplate = null)
        => (customTemplate ?? DefaultSystemPromptSchemaTemplate)
            .Replace("{schema}", schemaJson);

    /// <summary>
    /// Returns the user-message schema template with <c>{message}</c> and <c>{schema}</c>
    /// replaced by <paramref name="message"/> and <paramref name="schemaJson"/> respectively.
    /// </summary>
    /// <param name="message">The user's original message text.</param>
    /// <param name="schemaJson">The JSON schema string to embed.</param>
    /// <param name="customTemplate">
    /// Optional per-profile override. When <c>null</c>, <see cref="DefaultUserMessageSchemaTemplate"/>
    /// is used.
    /// </param>
    public static string FormatUserMessage(string message, string schemaJson, string? customTemplate = null)
        => (customTemplate ?? DefaultUserMessageSchemaTemplate)
            .Replace("{message}", message)
            .Replace("{schema}", schemaJson);
}
