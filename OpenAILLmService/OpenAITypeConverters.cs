using AITaskAgent.LLM.Models;
using OpenAI.Chat;

namespace OpenAILLmService;

/// <summary>
/// Type converters between framework-agnostic models and OpenAI-specific types.
/// Implements smart conversion: reuse native objects when provider matches, convert otherwise.
/// </summary>
internal static class OpenAITypeConverters
{
    private const string ProviderName = "OpenAI";

    #region Message Conversions

    /// <summary>
    /// Converts framework Message to OpenAI ChatMessage.
    /// Reuses native object if it came from OpenAI.
    /// </summary>
    public static ChatMessage ToChatMessage(this Message message)
    {
        // OPTIMIZATION: Reuse native if same provider
        if (message.Native?.Provider == ProviderName &&
            message.Native.Value is ChatMessage nativeMessage)
        {
            return nativeMessage;
        }

        // Convert from agnostic model
        return message.Role switch
        {
            "system" => ChatMessage.CreateSystemMessage(message.Content),
            "user" => ChatMessage.CreateUserMessage(message.Content),
            "assistant" => ChatMessage.CreateAssistantMessage(message.Content),
            "tool" => ChatMessage.CreateToolMessage(message.ToolCallId!, message.Content),
            _ => throw new ArgumentException($"Unknown role: {message.Role}")
        };
    }

    /// <summary>
    /// Converts OpenAI ChatMessage to framework Message.
    /// Stores native object with provider metadata.
    /// </summary>
    public static Message ToFrameworkMessage(this ChatMessage chatMessage)
    {
        // Determine role and extract content based on concrete message type
        string role;
        var content = string.Empty;
        string? toolCallId = null;

        switch (chatMessage)
        {
            case SystemChatMessage systemMsg:
                role = "system";
                content = ExtractTextContent(systemMsg.Content);
                break;

            case UserChatMessage userMsg:
                role = "user";
                content = ExtractTextContent(userMsg.Content);
                break;

            case AssistantChatMessage assistantMsg:
                role = "assistant";
                content = ExtractTextContent(assistantMsg.Content);
                break;

            case ToolChatMessage toolMsg:
                role = "tool";
                toolCallId = toolMsg.ToolCallId;
                content = ExtractTextContent(toolMsg.Content);
                break;

            default:
                throw new ArgumentException($"Unknown ChatMessage type: {chatMessage.GetType().Name}");
        }

        return new Message
        {
            Role = role,
            Content = content,
            ToolCallId = toolCallId,
            Native = new NativeObject
            {
                Provider = ProviderName,
                Value = chatMessage,
                TypeName = chatMessage.GetType().AssemblyQualifiedName!
            }
        };
    }

    /// <summary>
    /// Extracts text content from ChatMessageContentPart collection.
    /// </summary>
    private static string ExtractTextContent(IEnumerable<ChatMessageContentPart> contentParts)
    {
        return string.Concat(contentParts
            .Where(part => part.Kind == ChatMessageContentPartKind.Text)
            .Select(part => part.Text));
    }

    #endregion

    #region Tool Conversions

    /// <summary>
    /// Converts framework ToolDefinition to OpenAI ChatTool.
    /// Reuses native if it came from OpenAI.
    /// </summary>
    public static ChatTool ToChatTool(this ToolDefinition tool)
    {
        // OPTIMIZATION: Reuse native if same provider
        return tool.Native?.Provider == ProviderName &&
            tool.Native.Value is ChatTool nativeTool
            ? nativeTool
            : ChatTool.CreateFunctionTool(
            tool.Name,
            tool.Description,
            BinaryData.FromString(tool.ParametersJsonSchema));
    }

    /// <summary>
    /// Converts OpenAI ChatToolCall to framework ToolCall.
    /// Stores native object with provider metadata.
    /// </summary>
    public static ToolCall ToFrameworkToolCall(this ChatToolCall chatToolCall)
    {
        return new ToolCall
        {
            Id = chatToolCall.Id,
            Name = chatToolCall.FunctionName,
            Arguments = chatToolCall.FunctionArguments.ToString(),
            Native = new NativeObject
            {
                Provider = ProviderName,
                Value = chatToolCall,
                TypeName = chatToolCall.GetType().AssemblyQualifiedName!
            }
        };
    }

    /// <summary>
    /// Converts framework ToolCall to OpenAI ChatToolCall.
    /// Reuses native if it came from OpenAI.
    /// </summary>
    public static ChatToolCall ToChatToolCall(this ToolCall toolCall)
    {
        //  OPTIMIZATION: Reuse native if same provider
        return toolCall.Native?.Provider == ProviderName &&
            toolCall.Native.Value is ChatToolCall nativeToolCall
            ? nativeToolCall
            : ChatToolCall.CreateFunctionToolCall(
            toolCall.Id,
            toolCall.Name,
            BinaryData.FromString(toolCall.Arguments));
    }

    /// <summary>
    /// Converts OpenAI StreamingChatToolCallUpdate to framework ToolCallUpdate.
    /// </summary>
    public static ToolCallUpdate ToFrameworkToolCallUpdate(this StreamingChatToolCallUpdate update)
    {
        return new ToolCallUpdate
        {
            Index = update.Index,
            ToolCallId = update.ToolCallId,
            FunctionName = update.FunctionName,
            FunctionArgumentsUpdate = update.FunctionArgumentsUpdate?.ToString()
        };
    }

    #endregion
}
