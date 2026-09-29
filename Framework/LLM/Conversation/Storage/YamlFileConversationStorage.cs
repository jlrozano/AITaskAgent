using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using System.Globalization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AITaskAgent.LLM.Conversation.Storage;

/// <summary>
/// File-based implementation of conversation storage using CodeGui legacy YAML session files.
/// </summary>
public sealed class YamlFileConversationStorage : IConversationStorage
{
    private readonly string _basePath;
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;

    /// <summary>
    /// Creates a new YamlFileConversationStorage.
    /// </summary>
    /// <param name="basePath">Base directory for storing session YAML files.</param>
    public YamlFileConversationStorage(string basePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        _basePath = basePath;
        Directory.CreateDirectory(_basePath);

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
    }

    /// <inheritdoc />
    public async Task SaveConversationAsync(ConversationContext conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        string conversationId = NormalizeConversationId(conversation.ConversationId);
        string filePath = GetFilePath(conversationId);

        LegacySessionDocument document;
        if (File.Exists(filePath))
        {
            string existingYaml = await File.ReadAllTextAsync(filePath, cancellationToken);
            document = _deserializer.Deserialize<LegacySessionDocument>(existingYaml) ?? new LegacySessionDocument();
        }
        else
        {
            document = new LegacySessionDocument();
        }

        document.Id = EnsureSessionPrefix(conversationId);
        document.Title = GetMetadataString(conversation.Metadata, "title") ?? document.Title ?? document.Id;
        document.Agents = GetMetadataStringList(conversation.Metadata, "agents") ?? document.Agents;

        document.StartedAt ??= FormatUtcTimestamp(conversation.StartedAt);
        document.CompletedAt = FormatUtcTimestamp(conversation.LastActivityAt); // Update completedAt on save

        // Preserve each surviving message's original timestamp by position instead of
        // stamping every message with "now" on every save - otherwise a message's timestamp
        // silently drifted forward on each subsequent save of the same conversation.
        List<LegacySessionMessage>? previousMessages = document.Messages;
        document.Messages = conversation.History.Messages
            .Select((message, index) => ToLegacyMessage(
                message,
                previousMessages != null && index < previousMessages.Count ? previousMessages[index].Timestamp : null))
            .ToList();

        document.Events ??= [];

        string yaml = _serializer.Serialize(document);
        await File.WriteAllTextAsync(filePath, yaml, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ConversationContext?> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        string normalizedId = NormalizeConversationId(conversationId);
        string filePath = GetFilePath(normalizedId);

        if (!File.Exists(filePath))
        {
            return null;
        }

        string yaml = await File.ReadAllTextAsync(filePath, cancellationToken);
        LegacySessionDocument document = _deserializer.Deserialize<LegacySessionDocument>(yaml);
        if (document == null)
        {
            return null;
        }

        DateTime startedAt = TryParseUtcTimestamp(document.StartedAt) ?? DateTime.UtcNow;
        DateTime lastActivityAt = TryParseUtcTimestamp(document.CompletedAt) ?? startedAt;

        var metadata = new Dictionary<string, object?>
        {
            ["title"] = document.Title,
            ["agents"] = document.Agents
        };

        var context = new ConversationContext
        {
            ConversationId = normalizedId,
            Metadata = metadata,
            StartedAt = startedAt,
            LastActivityAt = lastActivityAt
        };

        if (document.Messages != null)
        {
            foreach (LegacySessionMessage legacyMessage in document.Messages)
            {
                Message? message = FromLegacyMessage(legacyMessage);
                if (message == null)
                {
                    continue;
                }

                context.AddMessage(message);

                DateTime? messageTimestamp = TryParseUtcTimestamp(legacyMessage.Timestamp);
                if (messageTimestamp.HasValue && messageTimestamp.Value > context.LastActivityAt)
                {
                    context.LastActivityAt = messageTimestamp.Value;
                }
            }
        }

        return context;
    }

    private static string EnsureSessionPrefix(string id) =>
        id.StartsWith("session-", StringComparison.OrdinalIgnoreCase) ? id : $"session-{id}";

    private static string NormalizeConversationId(string conversationId) =>
        conversationId.StartsWith("session-", StringComparison.OrdinalIgnoreCase)
            ? conversationId["session-".Length..]
            : conversationId;

    private string GetFilePath(string normalizedConversationId)
    {
        string sessionId = EnsureSessionPrefix(normalizedConversationId);
        string safeId = string.Join("_", sessionId.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_basePath, $"{safeId}.yaml");
    }

    private static LegacySessionMessage ToLegacyMessage(Message message, string? existingTimestamp = null)
    {
        // Round-trips the actual role instead of collapsing "system"/"tool" into "agent": the
        // legacy format's "agent"/"orchestrator" split only ever applied to assistant messages,
        // so system and tool now get their own explicit legacy role.
        string role = message.Role switch
        {
            LlmConstants.MessageRoles.User => "user",
            LlmConstants.MessageRoles.System => "system",
            LlmConstants.MessageRoles.Tool => "tool",
            LlmConstants.MessageRoles.Assistant => string.Equals(message.Name, "orchestrator", StringComparison.OrdinalIgnoreCase)
                ? "orchestrator"
                : "agent",
            _ => "agent"
        };

        return new LegacySessionMessage
        {
            Role = role,
            AgentName = role is "user" or "system" or "tool" ? null : message.Name,
            Content = message.Content ?? string.Empty,
            // Reuse the timestamp this message already had on disk when there is one, so re-saving
            // an unchanged message doesn't keep bumping it to "now".
            Timestamp = !string.IsNullOrWhiteSpace(existingTimestamp) ? existingTimestamp : FormatUtcTimestamp(DateTime.UtcNow),
            ToolCallId = message.ToolCallId,
            ToolCalls = message.ToolCalls is { Count: > 0 }
                ? [.. message.ToolCalls.Select(tc => new LegacyToolCall { Id = tc.Id, Name = tc.Name, Arguments = tc.Arguments })]
                : null
        };
    }

    private static Message? FromLegacyMessage(LegacySessionMessage? legacyMessage)
    {
        if (legacyMessage == null || string.IsNullOrWhiteSpace(legacyMessage.Role))
        {
            return null;
        }

        string frameworkRole = legacyMessage.Role switch
        {
            "user" => LlmConstants.MessageRoles.User,
            "system" => LlmConstants.MessageRoles.System,
            "tool" => LlmConstants.MessageRoles.Tool,
            "orchestrator" or "agent" => LlmConstants.MessageRoles.Assistant,
            _ => LlmConstants.MessageRoles.Assistant
        };

        List<ToolCall>? toolCalls = legacyMessage.ToolCalls is { Count: > 0 }
            ? [.. legacyMessage.ToolCalls
                .Where(tc => tc.Id != null && tc.Name != null && tc.Arguments != null)
                .Select(tc => new ToolCall { Id = tc.Id!, Name = tc.Name!, Arguments = tc.Arguments! })]
            : null;

        return new Message
        {
            Role = frameworkRole,
            Name = frameworkRole == LlmConstants.MessageRoles.Assistant ? legacyMessage.AgentName : null,
            Content = legacyMessage.Content ?? string.Empty,
            ToolCallId = frameworkRole == LlmConstants.MessageRoles.Tool ? legacyMessage.ToolCallId : null,
            ToolCalls = toolCalls
        };
    }

    private static DateTime? TryParseUtcTimestamp(string? timestamp)
    {
        return string.IsNullOrWhiteSpace(timestamp)
            ? null
            : DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out global::System.DateTime dt)
            ? dt
            : null;
    }

    private static string FormatUtcTimestamp(DateTime? timestamp)
    {
        return timestamp?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string? GetMetadataString(IDictionary<string, object?>? metadata, string key)
    {
        return metadata != null && metadata.TryGetValue(key, out object? value) && value is string s ? s : null;
    }

    private static List<string>? GetMetadataStringList(IDictionary<string, object?>? metadata, string key)
    {
        if (metadata != null && metadata.TryGetValue(key, out object? value))
        {
            if (value is List<string> list)
            {
                return list;
            }

            if (value is IEnumerable<object> objList)
            {
                return objList.Select(o => o.ToString() ?? "").ToList();
            }
        }
        return null;
    }

    private class LegacySessionDocument
    {
        public string Id { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? StartedAt { get; set; }
        public string? CompletedAt { get; set; }
        public List<string>? Agents { get; set; }
        public List<LegacySessionMessage>? Messages { get; set; }
        public List<object>? Events { get; set; }
    }

    private class LegacySessionMessage
    {
        public string? Role { get; set; }
        public string? AgentName { get; set; }
        public string? Content { get; set; }
        public string? Timestamp { get; set; }
        public string? ToolCallId { get; set; }
        public List<LegacyToolCall>? ToolCalls { get; set; }
    }

    private class LegacyToolCall
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Arguments { get; set; }
    }
}
