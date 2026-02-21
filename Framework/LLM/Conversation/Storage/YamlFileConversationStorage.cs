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

        var conversationId = NormalizeConversationId(conversation.ConversationId);
        var filePath = GetFilePath(conversationId);

        LegacySessionDocument document;
        if (File.Exists(filePath))
        {
            var existingYaml = await File.ReadAllTextAsync(filePath, cancellationToken);
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

        document.Messages = conversation.History.Messages
            .Select(ToLegacyMessage)
            .ToList();

        document.Events ??= [];

        var yaml = _serializer.Serialize(document);
        await File.WriteAllTextAsync(filePath, yaml, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ConversationContext?> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var normalizedId = NormalizeConversationId(conversationId);
        var filePath = GetFilePath(normalizedId);

        if (!File.Exists(filePath))
        {
            return null;
        }

        var yaml = await File.ReadAllTextAsync(filePath, cancellationToken);
        var document = _deserializer.Deserialize<LegacySessionDocument>(yaml);
        if (document == null)
        {
            return null;
        }

        var startedAt = TryParseUtcTimestamp(document.StartedAt) ?? DateTime.UtcNow;
        var lastActivityAt = TryParseUtcTimestamp(document.CompletedAt) ?? startedAt;

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
            foreach (var legacyMessage in document.Messages)
            {
                var message = FromLegacyMessage(legacyMessage);
                if (message == null)
                {
                    continue;
                }

                context.AddMessage(message);

                var messageTimestamp = TryParseUtcTimestamp(legacyMessage.Timestamp);
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
        var sessionId = EnsureSessionPrefix(normalizedConversationId);
        var safeId = string.Join("_", sessionId.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_basePath, $"{safeId}.yaml");
    }

    private static LegacySessionMessage ToLegacyMessage(Message message)
    {
        var role = message.Role switch
        {
            LlmConstants.MessageRoles.User => "user",
            LlmConstants.MessageRoles.Assistant => string.Equals(message.Name, "orchestrator", StringComparison.OrdinalIgnoreCase)
                ? "orchestrator"
                : "agent",
            _ => "agent"
        };

        return new LegacySessionMessage
        {
            Role = role,
            AgentName = role == "user" ? null : message.Name,
            Content = message.Content ?? string.Empty,
            Timestamp = FormatUtcTimestamp(DateTime.UtcNow) // Ideally we should have timestamp on Message
        };
    }

    private static Message? FromLegacyMessage(LegacySessionMessage? legacyMessage)
    {
        if (legacyMessage == null || string.IsNullOrWhiteSpace(legacyMessage.Role))
        {
            return null;
        }

        var frameworkRole = legacyMessage.Role switch
        {
            "user" => LlmConstants.MessageRoles.User,
            "orchestrator" or "agent" => LlmConstants.MessageRoles.Assistant,
            _ => LlmConstants.MessageRoles.Assistant
        };

        return new Message
        {
            Role = frameworkRole,
            Name = frameworkRole == LlmConstants.MessageRoles.Assistant ? legacyMessage.AgentName : null,
            Content = legacyMessage.Content ?? string.Empty
        };
    }

    private static DateTime? TryParseUtcTimestamp(string? timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp))
        {
            return null;
        }

        if (DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
        {
            return dt;
        }

        return null;
    }

    private static string FormatUtcTimestamp(DateTime? timestamp)
    {
        return timestamp?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string? GetMetadataString(IDictionary<string, object?>? metadata, string key)
    {
        if (metadata != null && metadata.TryGetValue(key, out var value) && value is string s)
        {
            return s;
        }
        return null;
    }

    private static List<string>? GetMetadataStringList(IDictionary<string, object?>? metadata, string key)
    {
        if (metadata != null && metadata.TryGetValue(key, out var value))
        {
            if (value is List<string> list) return list;
            if (value is IEnumerable<object> objList) return objList.Select(o => o.ToString() ?? "").ToList();
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
    }
}
