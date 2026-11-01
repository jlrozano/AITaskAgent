namespace AITaskAgent.Configuration;

/// <summary>
/// Where the framework keeps what outlives a single run: section <c>AITaskAgent:Storage</c>.
/// </summary>
/// <remarks>
/// <code>
/// "AITaskAgent": {
///   "Storage": {
///     "Context":      { "Type": "file", "Directory": "data/contexts" },
///     "Conversation": { "Type": "yaml", "Directory": "${APP_DATA}/conversations" }
///   }
/// }
/// </code>
/// Both default to <c>memory</c>, which is what the framework registered before this section existed.
/// The storage is built the first time it is resolved; a type that is not valid for the entry, or a
/// file type without <c>Directory</c>, is an <see cref="InvalidOperationException"/> naming the key.
/// A storage the application registers itself, before or after <c>AddAITaskAgent()</c>, takes
/// precedence over this section.
/// </remarks>
public sealed class StorageOptions
{
    /// <summary>
    /// Storage of suspended pipeline contexts (<c>IPipelineContextStorage</c>): <c>memory</c>
    /// (default) or <c>file</c>.
    /// </summary>
    public StorageLocationOptions Context { get; init; } = new();

    /// <summary>
    /// Storage of conversations (<c>IConversationStorage</c>, registered by the LLM extension):
    /// <c>memory</c> (default), <c>file</c> (JSON) or <c>yaml</c>.
    /// </summary>
    public StorageLocationOptions Conversation { get; init; } = new();
}

/// <summary>One storage entry of <see cref="StorageOptions"/>: its kind and, for files, its folder.</summary>
public sealed class StorageLocationOptions
{
    /// <summary>In-memory storage: lost when the process ends.</summary>
    public const string Memory = "memory";

    /// <summary>One JSON file per id in <see cref="Directory"/>.</summary>
    public const string File = "file";

    /// <summary>One YAML file per id in <see cref="Directory"/> (conversations only).</summary>
    public const string Yaml = "yaml";

    /// <summary>Storage kind, case-insensitive. Default <see cref="Memory"/>.</summary>
    public string Type { get; set; } = Memory;

    /// <summary>
    /// Folder of a file storage. A relative path is resolved against the current directory;
    /// <c>${VAR}</c> is replaced by the environment variable, as in every value of the section.
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>
    /// <see cref="Type"/> lower-cased, checked against <paramref name="allowed"/>.
    /// </summary>
    /// <param name="key">Configuration key of this entry, for the error message (<c>AITaskAgent:Storage:Context</c>).</param>
    /// <param name="allowed">Kinds this entry accepts.</param>
    /// <exception cref="InvalidOperationException">The kind is empty or not one of <paramref name="allowed"/>.</exception>
    public string GetValidatedType(string key, params string[] allowed)
    {
        string type = (Type ?? string.Empty).Trim().ToLowerInvariant();
        return allowed.Contains(type, StringComparer.Ordinal)
            ? type
            : throw new InvalidOperationException(
                $"'{key}:Type' is '{Type}', which is not a valid storage type here. Valid values: {string.Join(", ", allowed)}.");
    }

    /// <summary>The absolute folder of a file storage.</summary>
    /// <param name="key">Configuration key of this entry, for the error message.</param>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Directory"/> is not set, or still holds a <c>${VAR}</c> whose variable is not defined.
    /// </exception>
    public string GetRequiredDirectory(string key)
    {
        if (string.IsNullOrWhiteSpace(Directory))
        {
            throw new InvalidOperationException(
                $"'{key}:Type' is '{Type}', which stores files, but '{key}:Directory' is not set.");
        }

        return Directory.Contains("${", StringComparison.Ordinal)
            ? throw new InvalidOperationException(
                $"'{key}:Directory' is '{Directory}': an environment variable it references is not defined.")
            : Path.GetFullPath(Directory.Trim());
    }
}
