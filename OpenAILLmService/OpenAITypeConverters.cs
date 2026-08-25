using AITaskAgent.LLM.Models;
using Newtonsoft.Json.Linq;

namespace OpenAILLmService;

internal static class OpenAITypeConverters
{
    private const string ProviderName = "OpenAI";

    #region Message → JObject (request)

    public static JObject ToJObject(this Message message)
    {
        // Misma optimización: si el native ya es un JObject de este provider, reusar
        if (message.Native?.Provider == ProviderName &&
            message.Native.Value is JObject nativeObj)
        {
            return nativeObj;
        }

        return message.Role switch
        {
            "system" => new JObject { ["role"] = "system", ["content"] = message.Content },

            "user" => new JObject { ["role"] = "user", ["content"] = message.Content },

            "assistant" when message.ToolCalls?.Count > 0 => new JObject
            {
                ["role"]       = "assistant",
                ["content"]    = message.Content,  // puede ser null, la API lo acepta
                ["tool_calls"] = new JArray(message.ToolCalls.Select(tc => tc.ToJObject()))
            },

            "assistant" => new JObject { ["role"] = "assistant", ["content"] = message.Content },

            "tool" => new JObject
            {
                ["role"]         = "tool",
                ["tool_call_id"] = message.ToolCallId,
                ["content"]      = message.Content
            },

            _ => throw new ArgumentException($"Unknown role: {message.Role}")
        };
    }

    // Usado al construir el array tool_calls de un mensaje assistant
    private static JObject ToJObject(this ToolCall tc) => new()
    {
        ["type"] = "function",
        ["id"]   = tc.Id,
        ["function"] = new JObject
        {
            ["name"]      = tc.Name,
            ["arguments"] = tc.Arguments
        }
    };

    #endregion

    #region JToken → Message (historial de respuesta)

    /// <summary>
    /// Convierte el "message" de una choice del JSON de respuesta a Message del framework.
    /// Guarda el JObject como native para poder reutilizarlo en la siguiente llamada.
    /// </summary>
    public static Message ToFrameworkMessage(this JToken messageToken)
    {
        var role      = messageToken["role"]?.Value<string>() ?? "assistant";
        var content   = messageToken["content"]?.Value<string>() ?? string.Empty;
        var toolCallId = messageToken["tool_call_id"]?.Value<string>();

        List<ToolCall>? toolCalls = null;
        if (messageToken["tool_calls"] is JArray tcArray && tcArray.Count > 0)
            toolCalls = [.. tcArray.Select(t => t.ToFrameworkToolCall())];

        return new Message
        {
            Role       = role,
            Content    = content,
            ToolCallId = toolCallId,
            ToolCalls  = toolCalls,
            Native     = new NativeObject
            {
                Provider = ProviderName,
                Value    = messageToken,          // JToken como native: round-trip sin re-parse
                TypeName = typeof(JToken).AssemblyQualifiedName!
            }
        };
    }

    #endregion

    #region Tool definitions

    public static JObject ToJObject(this ToolDefinition tool)
    {
        if (tool.Native?.Provider == ProviderName &&
            tool.Native.Value is JObject nativeTool)
            return nativeTool;

        return new JObject
        {
            ["type"] = "function",
            ["function"] = new JObject
            {
                ["name"]        = tool.Name,
                ["description"] = tool.Description,
                ["parameters"]  = JObject.Parse(tool.ParametersJsonSchema)
            }
        };
    }

    #endregion

    #region Tool calls (respuesta y streaming)

    public static ToolCall ToFrameworkToolCall(this JToken token)
    {
        return new ToolCall
        {
            Id        = token["id"]?.Value<string>() ?? string.Empty,
            Name      = token["function"]?["name"]?.Value<string>() ?? string.Empty,
            Arguments = token["function"]?["arguments"]?.Value<string>() ?? string.Empty,
            Native    = new NativeObject
            {
                Provider = ProviderName,
                Value    = token,
                TypeName = typeof(JToken).AssemblyQualifiedName!
            }
        };
    }

    public static ToolCallUpdate ToFrameworkToolCallUpdate(this JToken token)
    {
        return new ToolCallUpdate
        {
            Index                   = token["index"]?.Value<int>() ?? 0,
            ToolCallId              = token["id"]?.Value<string>(),
            FunctionName            = token["function"]?["name"]?.Value<string>(),
            FunctionArgumentsUpdate = token["function"]?["arguments"]?.Value<string>()
        };
    }

    #endregion
}