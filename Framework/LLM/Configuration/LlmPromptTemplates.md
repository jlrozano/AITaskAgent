# LLM Prompt Templates - Customization Guide

## Overview

The static `LlmPromptTemplates` class holds the templates that present a JSON schema to an LLM when the provider cannot enforce it natively. The defaults are **read-only** (`static readonly` fields and get-only aliases): you cannot reassign them globally. To use a different text, pass it as `customTemplate` to `FormatSystemPrompt` / `FormatUserMessage`.

The framework itself (`BaseLlmStep` → `LlmHelpers.InjectSchemaInSystemPrompt`) always calls `FormatSystemPrompt(schemaJson)` without a custom template, so a custom template only applies where you call the format helpers yourself.

## Default Templates

### System Prompt Template
`DefaultSystemPromptSchemaTemplate` (alias `SystemPromptSchemaTemplate`). `BaseLlmStep` appends it to the system prompt when the provider has no native JSON schema support (`Inference.JsonSupport = false`); with `JsonSupport = true` the schema goes in `ResponseFormat` and no template is used:

```csharp
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
```

### User Message Template
`DefaultUserMessageSchemaTemplate` (alias `UserMessageSchemaTemplate`), applied by `FormatUserMessage` / `LlmHelpers.InjectSchemaInUserMessage`. No framework step calls it today; it is available for your own code:

```csharp
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
```

## Customization Examples

All examples pass the template per call; `schemaJson` is the schema text (for example `schema.ToJson()`).

### Example 1: Simplified Template for Advanced Models
```csharp
// For models like GPT-4 that understand schemas well
string prompt = LlmPromptTemplates.FormatSystemPrompt(schemaJson, customTemplate: """
    
    Respond with JSON matching this schema:
    ```json
    {schema}
    ```
    """);
```

### Example 2: More Verbose for Less Capable Models
```csharp
// For older or smaller models that need more guidance
string message = LlmPromptTemplates.FormatUserMessage(userText, schemaJson, customTemplate: """
    {message}
    
     CRITICAL INSTRUCTIONS 
    
    You MUST respond with VALID JSON. Here is the exact schema you must follow:
    ```json
    {schema}
    ```
    
    STEP-BY-STEP CHECKLIST:
    1. Read each property description - it tells you EXACTLY what to put there
    2. Include ALL required fields (marked as required in schema)
    3. For enum fields, ONLY use the exact values listed
    4. Check min/max constraints for numbers and string lengths
    5. Validate your JSON syntax before responding
    
    DO NOT include any text, explanation, or commentary outside the JSON
    ONLY return the JSON object
    """);
```

### Example 3: Localized Templates (Spanish)
```csharp
string prompt = LlmPromptTemplates.FormatSystemPrompt(schemaJson, customTemplate: """
    
    IMPORTANTE: Debes responder con JSON válido que coincida con este esquema:
    ```json
    {schema}
    ```
    
    Presta atención a:
    - Las descripciones de propiedades explican qué debe contener cada campo
    - Los campos requeridos deben estar presentes
    - Los valores de enum y sus descripciones muestran las opciones válidas
    - Las restricciones de validación (min/max, patrones, longitudes) deben respetarse
    
    No incluyas ningún texto fuera del objeto JSON.
    """);
```

### Example 4: Runtime Configuration Based on Model
```csharp
public static string? SelectTemplateForModel(string modelName)
{
    if (modelName.Contains("gpt-4"))
    {
        // GPT-4 needs minimal instructions
        return """
            Respond with JSON matching: ```json
            {schema}
            ```
            """;
    }
    else if (modelName.Contains("claude"))
    {
        // Claude benefits from structured guidelines
        return """
            
            Return valid JSON matching this schema:
            ```json
            {schema}
            ```
            
            Notes:
            - Property descriptions explain each field's purpose
            - Required fields are mandatory
            - Enum descriptions clarify valid options
            """;
    }
    else
    {
        // null = use DefaultSystemPromptSchemaTemplate
        return null;
    }
}

string prompt = LlmPromptTemplates.FormatSystemPrompt(schemaJson, SelectTemplateForModel(modelName));
```

## How It Works

1. **Schema Generation**: `StepResultFactory` generates `JsonSchema` from your result types, including all `[Description]` attributes
2. **Template Application**: when the provider has `JsonSupport = false`, `BaseLlmStep` calls `LlmHelpers.InjectSchemaInSystemPrompt`, which formats the schema with `LlmPromptTemplates.FormatSystemPrompt` (default template) and appends it to the system prompt
3. **LLM Receives**: The formatted prompt with embedded schema (including descriptions, validations, etc.)

## Schema Content

The `{schema}` placeholder is replaced with the full JSON Schema, which includes:

- **Property descriptions** from `[Description]` attributes
- **Required fields** (`required` list)
- **Enum values and descriptions** from enum `[Description]` attributes
- **Validation rules** from `[Range]`, `[StringLength]`, `[MinLength]`, `[MaxLength]`, `[RegularExpression]`
- **Type information** (string, number, boolean, array, object)
- **Format hints** (`email` from `[EmailAddress]`, `uri` from `[Url]`, `date-time` for date types)

Example generated schema:
```json
{
  "type": "object",
  "properties": {
    "option": {
      "type": "string",
      "description": "The selected intention category from the available options.",
      "enum": ["Search", "Create", "Update"]
    },
    "reasoning": {
      "type": "string",
      "description": "Detailed explanation of why this option was chosen.",
      "minLength": 10
    },
    "confidence": {
      "type": "number",
      "description": "Confidence score between 0.0 and 1.0.",
      "minimum": 0.0,
      "maximum": 1.0
    }
  },
  "required": ["option", "reasoning"]
}
```

## Best Practices

1. **Test with your LLM**: Different models respond better to different instruction styles
2. **Keep it concise**: Overly verbose templates can confuse some models
3. **Emphasize critical points**: Use formatting (caps, emojis) sparingly for key instructions
4. **Localize when needed**: Match the template language to your user base
5. **Version control**: Track template changes to understand impact on LLM behavior
