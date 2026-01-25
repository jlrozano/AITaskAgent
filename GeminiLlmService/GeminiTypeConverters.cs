using AITaskAgent.LLM.Models;
using Google.GenAI.Types;
using Newtonsoft.Json;
using STJ = System.Text.Json;

namespace GeminiLlmService;

/// <summary>
/// Type converters between framework-agnostic models and Gemini-specific types.
/// Implements smart conversion: reuse native objects when provider matches, convert otherwise.
/// </summary>
internal static class GeminiTypeConverters
{
    private const string ProviderName = "Gemini";

    #region Message Conversions

    /// <summary>
    /// Converts framework Message to Gemini Content.
    /// Reuses native object if it came from Gemini.
    /// </summary>
    public static Content ToGeminiContent(this Message message)
    {
        // OPTIMIZATION: Reuse native if same provider
        if (message.Native?.Provider == ProviderName &&
            message.Native.Value is Content nativeContent)
        {
            return nativeContent;
        }

        // Map framework roles to Gemini roles
        var role = message.Role switch
        {
            "user" => "user",
            "assistant" => "model",
            "tool" => "function",
            "system" => "user", // System handled separately via SystemInstruction
            _ => "user"
        };

        var parts = new List<Part>();

        // Add text content
        if (!string.IsNullOrEmpty(message.Content))
        {
            parts.Add(new Part { Text = message.Content });
        }

        // Add tool calls if present (for assistant messages)
        if (message.ToolCalls != null)
        {
            foreach (var toolCall in message.ToolCalls)
            {
                var argsDict = JsonConvert.DeserializeObject<Dictionary<string, object>>(toolCall.Arguments)
                    ?? new Dictionary<string, object>();

                parts.Add(new Part
                {
                    FunctionCall = new FunctionCall
                    {
                        Name = toolCall.Name,
                        Args = argsDict
                    }
                });
            }
        }

        // Handle tool response messages
        if (message.Role == "tool" && !string.IsNullOrEmpty(message.ToolCallId))
        {
            return new Content
            {
                Role = "function",
                Parts =
                [
                    new Part
                    {
                        FunctionResponse = new FunctionResponse
                        {
                            Name = message.Name ?? message.ToolCallId,
                            Response = new Dictionary<string, object> { ["result"] = message.Content ?? string.Empty }
                        }
                    }
                ]
            };
        }

        return new Content
        {
            Role = role,
            Parts = parts
        };
    }

    /// <summary>
    /// Converts Gemini Content to framework Message.
    /// Stores native object with provider metadata.
    /// </summary>
    public static Message ToFrameworkMessage(this Content content)
    {
        var role = content.Role switch
        {
            "user" => "user",
            "model" => "assistant",
            "function" => "tool",
            _ => "user"
        };

        var textContent = string.Concat(
            content.Parts?
                .Where(p => p.Text != null)
                .Select(p => p.Text) ?? []);

        var toolCalls = content.Parts?
            .Where(p => p.FunctionCall != null)
            .Select(p => p.FunctionCall!.ToFrameworkToolCall())
            .ToList();

        string? toolCallId = null;
        string? name = null;

        // Handle function response
        var functionResponse = content.Parts?.FirstOrDefault(p => p.FunctionResponse != null)?.FunctionResponse;
        if (functionResponse != null)
        {
            role = "tool";
            name = functionResponse.Name;
            toolCallId = functionResponse.Name;
            textContent = functionResponse.Response?.ToString() ?? string.Empty;
        }

        return new Message
        {
            Role = role,
            Content = textContent,
            Name = name,
            ToolCallId = toolCallId,
            ToolCalls = toolCalls?.Count > 0 ? toolCalls : null,
            Native = new NativeObject
            {
                Provider = ProviderName,
                Value = content,
                TypeName = content.GetType().AssemblyQualifiedName!
            }
        };
    }

    #endregion

    #region Tool Conversions

    /// <summary>
    /// Converts framework ToolDefinition to Gemini Tool.
    /// </summary>
    public static Tool ToGeminiTool(this ToolDefinition tool)
    {
        // OPTIMIZATION: Reuse native if same provider
        if (tool.Native?.Provider == ProviderName &&
            tool.Native.Value is Tool nativeTool)
        {
            return nativeTool;
        }

        // Parse JSON schema to build Gemini Schema
        var schemaDict = JsonConvert.DeserializeObject<Dictionary<string, object>>(tool.ParametersJsonSchema);
        var geminiSchema = ConvertJsonSchemaToGeminiSchema(schemaDict);

        return new Tool
        {
            FunctionDeclarations =
            [
                new FunctionDeclaration
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    Parameters = geminiSchema
                }
            ]
        };
    }

    /// <summary>
    /// Converts Gemini FunctionCall to framework ToolCall.
    /// </summary>
    public static ToolCall ToFrameworkToolCall(this FunctionCall functionCall)
    {
        var arguments = functionCall.Args != null
            ? JsonConvert.SerializeObject(SanitizeArgsDictionary(functionCall.Args))
            : "{}";

        return new ToolCall
        {
            Id = $"call_{functionCall.Name}_{Guid.NewGuid():N}",
            Name = functionCall.Name ?? string.Empty,
            Arguments = arguments,
            Native = new NativeObject
            {
                Provider = ProviderName,
                Value = functionCall,
                TypeName = functionCall.GetType().AssemblyQualifiedName!
            }
        };
    }

    /// <summary>
    /// Converts framework ToolCall to Gemini FunctionCall.
    /// </summary>
    public static FunctionCall ToGeminiFunctionCall(this ToolCall toolCall)
    {
        // OPTIMIZATION: Reuse native if same provider
        if (toolCall.Native?.Provider == ProviderName &&
            toolCall.Native.Value is FunctionCall nativeFunctionCall)
        {
            return nativeFunctionCall;
        }

        var argsDict = JsonConvert.DeserializeObject<Dictionary<string, object>>(toolCall.Arguments)
            ?? new Dictionary<string, object>();

        return new FunctionCall
        {
            Name = toolCall.Name,
            Args = argsDict
        };
    }

    #endregion

    #region Schema Conversion

    /// <summary>
    /// Converts a JSON Schema dictionary to Gemini Schema.
    /// </summary>
    private static Schema ConvertJsonSchemaToGeminiSchema(Dictionary<string, object>? schemaDict)
    {
        if (schemaDict == null)
        {
            return new Schema { Type = Google.GenAI.Types.Type.OBJECT };
        }

        var schema = new Schema();

        if (schemaDict.TryGetValue("type", out var typeValue))
        {
            schema.Type = typeValue?.ToString()?.ToUpperInvariant() switch
            {
                "OBJECT" => Google.GenAI.Types.Type.OBJECT,
                "STRING" => Google.GenAI.Types.Type.STRING,
                "NUMBER" => Google.GenAI.Types.Type.NUMBER,
                "INTEGER" => Google.GenAI.Types.Type.INTEGER,
                "BOOLEAN" => Google.GenAI.Types.Type.BOOLEAN,
                "ARRAY" => Google.GenAI.Types.Type.ARRAY,
                _ => Google.GenAI.Types.Type.OBJECT
            };
        }

        if (schemaDict.TryGetValue("description", out var descValue))
        {
            schema.Description = descValue?.ToString();
        }

        if (schemaDict.TryGetValue("properties", out var propsValue) &&
            propsValue is Dictionary<string, object> props)
        {
            schema.Properties = new Dictionary<string, Schema>();
            foreach (var (key, value) in props)
            {
                if (value is Dictionary<string, object> propSchema)
                {
                    schema.Properties[key] = ConvertJsonSchemaToGeminiSchema(propSchema);
                }
            }
        }

        if (schemaDict.TryGetValue("required", out var requiredValue) &&
            requiredValue is List<object> required)
        {
            schema.Required = required.Select(r => r.ToString()!).ToList();
        }

        if (schemaDict.TryGetValue("items", out var itemsValue) &&
            itemsValue is Dictionary<string, object> items)
        {
            schema.Items = ConvertJsonSchemaToGeminiSchema(items);
        }

        return schema;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Recursively converts System.Text.Json types to native .NET types to ensure proper serialization by Newtonsoft.Json.
    /// This fixes the issue where Gemini SDK returns JsonElement objects that get serialized as {"ValueKind":...}
    /// </summary>
    internal static Dictionary<string, object?> SanitizeArgsDictionary(IDictionary<string, object> args)
    {
        return args.ToDictionary(k => k.Key, k => SanitizeValue(k.Value));
    }

    private static object? SanitizeValue(object? value)
    {
        if (value is STJ.JsonElement element)
        {
            return element.ValueKind switch
            {
                STJ.JsonValueKind.String => element.GetString(),
                STJ.JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
                STJ.JsonValueKind.True => true,
                STJ.JsonValueKind.False => false,
                STJ.JsonValueKind.Null => null,
                STJ.JsonValueKind.Array => element.EnumerateArray().Select(e => SanitizeValue(e)).ToList(),
                STJ.JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => SanitizeValue(p.Value)),
                _ => value.ToString() // Fallback
            };
        }

        if (value is IDictionary<string, object> dict)
        {
            return dict.ToDictionary(k => k.Key, k => SanitizeValue(k.Value));
        }

        if (value is IList<object> list)
        {
            return list.Select(SanitizeValue).ToList();
        }

        return value;
    }

    #endregion
}
