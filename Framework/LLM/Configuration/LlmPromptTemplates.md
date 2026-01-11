# LLM Prompt Templates - Customization Guide

## Overview

The `LlmPromptTemplates` class provides configurable templates for how JSON schemas are presented to LLMs. This allows you to customize the instructions based on the LLM's characteristics or your specific needs.

## Default Templates

### System Prompt Template
Used when LLM supports `JsonObject` but not `JsonSchema`:

```csharp
LlmPromptTemplates.SystemPromptSchemaTemplate = """
    
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
```

### User Message Template
Used when LLM has no JSON support:

```csharp
LlmPromptTemplates.UserMessageSchemaTemplate = """
    {message}
    
    IMPORTANT: Respond ONLY with valid JSON matching this schema:
    ```json
    {schema}
    ```
    
    Guidelines:
    - Read property descriptions carefully - they explain what to put in each field
    - All required fields must be included
    - For enums, use only the listed values (descriptions explain each option)
    - Respect validation rules (min/max values, string lengths, patterns)
    
    Do not include any explanation or text outside the JSON object.
    """;
```

## Customization Examples

### Example 1: Simplified Template for Advanced Models
```csharp
// For models like GPT-4 that understand schemas well
LlmPromptTemplates.SystemPromptSchemaTemplate = """
    
    Respond with JSON matching this schema:
    ```json
    {schema}
    ```
    """;
```

### Example 2: More Verbose for Less Capable Models
```csharp
// For older or smaller models that need more guidance
LlmPromptTemplates.UserMessageSchemaTemplate = """
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
    """;
```

### Example 3: Localized Templates (Spanish)
```csharp
LlmPromptTemplates.SystemPromptSchemaTemplate = """
    
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
    """;
```

### Example 4: Runtime Configuration Based on Model
```csharp
public void ConfigureTemplatesForModel(string modelName)
{
    if (modelName.Contains("gpt-4"))
    {
        // GPT-4 needs minimal instructions
        LlmPromptTemplates.SystemPromptSchemaTemplate = """
            Respond with JSON matching: ```json
            {schema}
            ```
            """;
    }
    else if (modelName.Contains("claude"))
    {
        // Claude benefits from structured guidelines
        LlmPromptTemplates.SystemPromptSchemaTemplate = """
            
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
        // Use default verbose template for unknown models
        // (already set by default)
    }
}
```

## How It Works

1. **Schema Generation**: `StepResultFactory` generates `JsonSchema` from your result types, including all `[Description]` attributes
2. **Template Application**: `LlmHelpers` uses `LlmPromptTemplates` to format the schema into prompts
3. **LLM Receives**: The formatted prompt with embedded schema (including descriptions, validations, etc.)

## Schema Content

The `{schema}` placeholder is replaced with the full JSON Schema, which includes:

- **Property descriptions** from `[Description]` attributes
- **Required fields** from nullable analysis and `required` keyword
- **Enum values and descriptions** from enum `[Description]` attributes
- **Validation rules** from `[Range]`, `[StringLength]`, `[MinLength]`, `[MaxLength]`, `[RegularExpression]`, etc.
- **Type information** (string, number, boolean, array, object)
- **Format hints** (email, uri, date-time)

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
