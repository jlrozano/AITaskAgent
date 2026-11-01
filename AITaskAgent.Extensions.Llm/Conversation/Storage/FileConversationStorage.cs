using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using Newtonsoft.Json;

namespace AITaskAgent.LLM.Conversation.Storage;

/// <summary>
/// File-based implementation of conversation storage using JSON files.
/// Each conversation is stored in a separate JSON file via a serialisable
/// <see cref="ConversationSnapshot"/> DTO so that non-serialisable dependencies
/// of <see cref="ConversationContext"/> (ILogger, tokenCounter) are never
/// included in the persisted representation.
/// </summary>
public sealed class FileConversationStorage : IConversationStorage
{
    private readonly string _basePath;
    private readonly JsonSerializerSettings _jsonSettings;

    /// <summary>
    /// Creates a new FileConversationStorage.
    /// </summary>
    /// <param name="basePath">Base directory for storing conversation files. Defaults to "conversations".</param>
    public FileConversationStorage(string basePath = "conversations")
    {
        _basePath = basePath;
        Directory.CreateDirectory(_basePath);

        _jsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            TypeNameHandling = TypeNameHandling.None
        };
    }

    /// <inheritdoc />
    public async Task SaveConversationAsync(
        ConversationContext conversation,
        CancellationToken cancellationToken = default)
    {
        ConversationSnapshot snapshot = new(
            ConversationId: conversation.ConversationId,
            MaxTokens: conversation.History.MaxTokens,
            Messages: conversation.History.Messages.ToList(),
            Metadata: conversation.Metadata,
            StartedAt: conversation.StartedAt,
            LastActivityAt: conversation.LastActivityAt);

        string filePath = GetFilePath(conversation.ConversationId);
        string json = JsonConvert.SerializeObject(snapshot, _jsonSettings);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ConversationContext?> GetConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        string filePath = GetFilePath(conversationId);

        if (!File.Exists(filePath))
        {
            return null;
        }

        string json = await File.ReadAllTextAsync(filePath, cancellationToken);
        var snapshot = JsonConvert.DeserializeObject<ConversationSnapshot>(json, _jsonSettings);

        if (snapshot is null)
        {
            return null;
        }

        // Reconstruct a fully-operational ConversationContext.
        // The tokenCounter and logger are not persisted; they default to the built-in
        // implementations (4-char-per-token estimator and NullLogger respectively).
        ConversationContext ctx = new(snapshot.MaxTokens)
        {
            ConversationId = snapshot.ConversationId,
            Metadata = snapshot.Metadata ?? [],
            StartedAt = snapshot.StartedAt,
            LastActivityAt = snapshot.LastActivityAt
        };
        ctx.History.AddMessages(snapshot.Messages);

        return ctx;
    }

    /// <summary>
    /// Deletes a conversation file.
    /// </summary>
    public Task DeleteConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        string filePath = GetFilePath(conversationId);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Lists all conversation IDs stored on disk.
    /// </summary>
    public Task<IEnumerable<string>> ListConversationsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_basePath))
        {
            return Task.FromResult(Enumerable.Empty<string>());
        }

        var conversationIds = Directory
            .GetFiles(_basePath, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f));

        return Task.FromResult(conversationIds);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string GetFilePath(string conversationId)
    {
        // Sanitize conversation ID for use as a file name
        string safeId = string.Join("_", conversationId.Split(Path.GetInvalidFileNameChars()));
        string fullBasePath = Path.GetFullPath(_basePath) + Path.DirectorySeparatorChar;
        string targetPath = Path.GetFullPath(Path.Combine(_basePath, $"{safeId}.json"));

        return !targetPath.StartsWith(fullBasePath, StringComparison.OrdinalIgnoreCase)
            ? throw new UnauthorizedAccessException(
                $"Invalid conversation ID — path traversal attempt detected: {conversationId}")
            : targetPath;
    }
}

/// <summary>
/// Serialisation-only DTO that captures the persisted state of a
/// <see cref="ConversationContext"/> without any non-serialisable dependencies.
/// </summary>
internal sealed record ConversationSnapshot(
    string ConversationId,
    int MaxTokens,
    List<Message> Messages,
    Dictionary<string, object?> Metadata,
    DateTime StartedAt,
    DateTime LastActivityAt);
