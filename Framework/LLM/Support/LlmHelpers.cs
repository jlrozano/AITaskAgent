using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NJsonSchema;

namespace AITaskAgent.LLM.Support;

/// <summary>
/// Static helper methods for LLM operations.
/// </summary>
public static class LlmHelpers
{
    /// <summary>
    /// Cleans JSON response by removing markdown code blocks (```json ... ```).
    /// </summary>
    public static string CleanJsonResponse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        var cleaned = content.Trim();

        // Remove ```json or ``` at start
        if (cleaned.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[7..];
        }
        else if (cleaned.StartsWith("```"))
        {
            cleaned = cleaned[3..];
        }

        // Remove ``` at end
        if (cleaned.EndsWith("```"))
        {
            cleaned = cleaned[..^3];
        }

        return cleaned.Trim();
    }

    /// <summary>
    /// Creates a normalized key for a tool call that is independent of JSON property order.
    /// This ensures {"a":1,"b":2} and {"b":2,"a":1} are treated as equal.
    /// </summary>
    public static string NormalizeToolCallKey(ToolCall toolCall)
    {
        var functionName = toolCall.Name;
        var argsString = toolCall.Arguments;

        try
        {
            var args = JObject.Parse(argsString);
            if (args != null)
            {
                // Sort properties alphabetically for consistent comparison
                var normalizedArgs = JsonConvert.SerializeObject(
                    args.Properties()
                        .OrderBy(p => p.Name)
                        .ToDictionary(p => p.Name, p => p.Value));

                return $"{functionName}:{normalizedArgs}";
            }
        }
        catch (JsonException)
        {
            // If parsing fails, use raw string
        }

        return $"{functionName}:{argsString}";
    }

    /// <summary>
    /// Injects JSON schema into the system prompt using configurable template.
    /// The schema includes descriptions, validations, and all metadata.
    /// </summary>
    public static string InjectSchemaInSystemPrompt(string? existingPrompt, JsonSchema schema)
    {
        var schemaJson = schema.ToJson();
        var schemaInstruction = LlmPromptTemplates.FormatSystemPrompt(schemaJson);

        return string.IsNullOrEmpty(existingPrompt)
            ? schemaInstruction.Trim()
            : existingPrompt + schemaInstruction;
    }

    /// <summary>
    /// Injects JSON schema into the user message using configurable template.
    /// The schema includes descriptions, validations, and all metadata.
    /// </summary>
    public static string InjectSchemaInUserMessage(string message, JsonSchema schema)
    {
        var schemaJson = schema.ToJson();
        return LlmPromptTemplates.FormatUserMessage(message, schemaJson);
    }

    /// <summary>
    /// Truncates content for error messages to avoid overwhelming the LLM.
    /// </summary>
    public static string TruncateForError(string content, int maxLength = 500)
    {
        return string.IsNullOrEmpty(content)
            ? "(empty response)"
            : content.Length <= maxLength ? content : string.Concat(content.AsSpan(0, maxLength), "\n... (truncated)");
    }

    /// <summary>
    /// Converts a string value to the specified target type.
    /// Supports primitives, enums, DateTime, Guid, and types with string constructors.
    /// </summary>
    public static object ConvertOrThrow(string value, Type targetType)
    {
        if (targetType == typeof(string))
        {
            return value;
        }

        // Nullable<T> → use underlying type
        var underlying = Nullable.GetUnderlyingType(targetType);
        if (underlying != null)
        {
            targetType = underlying;
        }

        // Handle common types explicitly for better performance
        if (targetType == typeof(int))
        {
            return int.Parse(value.Trim());
        }

        if (targetType == typeof(long))
        {
            return long.Parse(value.Trim());
        }

        if (targetType == typeof(double))
        {
            return double.Parse(value.Trim());
        }

        if (targetType == typeof(decimal))
        {
            return decimal.Parse(value.Trim());
        }

        if (targetType == typeof(bool))
        {
            return bool.Parse(value.Trim());
        }

        if (targetType == typeof(DateTime))
        {
            return DateTime.Parse(value.Trim());
        }

        if (targetType == typeof(Guid))
        {
            return Guid.Parse(value.Trim());
        }

        // Enums
        if (targetType.IsEnum)
        {
            return Enum.Parse(targetType, value.Trim(), ignoreCase: true);
        }

        // Types with string constructor
        var ctor = targetType.GetConstructor([typeof(string)]);
        return ctor != null
            ? ctor.Invoke([value])
            : throw new InvalidOperationException(
            $"Cannot convert string '{LlmHelpers.TruncateForError(value, 100)}' to type {targetType.Name}");
    }

    /// <summary>
    /// Converts a string value to the specified type T.
    /// </summary>
    public static T ConvertOrThrow<T>(string value)
    {
        return (T)ConvertOrThrow(value, typeof(T));
    }
}

