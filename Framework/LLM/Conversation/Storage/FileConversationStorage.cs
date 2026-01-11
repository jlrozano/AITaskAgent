using AITaskAgent.LLM.Conversation.Context;
using Newtonsoft.Json;

namespace AITaskAgent.LLM.Conversation.Storage;

/// <summary>
/// File-based implementation of conversation storage using JSON files.
/// Each conversation is stored in a separate JSON file.
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
            TypeNameHandling = TypeNameHandling.Auto
        };
    }

    /// <inheritdoc />
    public async Task SaveConversationAsync(ConversationContext conversation, CancellationToken cancellationToken = default)
    {
        var filePath = GetFilePath(conversation.ConversationId);
        var json = JsonConvert.SerializeObject(conversation, _jsonSettings);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ConversationContext?> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var filePath = GetFilePath(conversationId);

        if (!File.Exists(filePath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(filePath, cancellationToken);
        return JsonConvert.DeserializeObject<ConversationContext>(json, _jsonSettings);
    }

    /// <summary>
    /// Deletes a conversation file.
    /// </summary>
    public Task DeleteConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var filePath = GetFilePath(conversationId);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Lists all conversation IDs stored on disk.
    /// </summary>
    public Task<IEnumerable<string>> ListConversationsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_basePath))
        {
            return Task.FromResult(Enumerable.Empty<string>());
        }

        var conversationIds = Directory.GetFiles(_basePath, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f));

        return Task.FromResult(conversationIds);
    }

    private string GetFilePath(string conversationId)
    {
        // Sanitize conversation ID for file system
        var safeId = string.Join("_", conversationId.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_basePath, $"{safeId}.json");
    }
}
