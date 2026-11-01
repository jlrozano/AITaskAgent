using NJsonSchema;
using System.Diagnostics.CodeAnalysis;

namespace AITaskAgent.YAML.Schemas;

/// <summary>
/// JSON Schemas a YAML pipeline refers to by name in <c>inputSchema</c> / <c>outputSchema</c>.
/// Schemas are kept as NJsonSchema <see cref="JsonSchema"/> objects and checked at run time;
/// no C# type is generated from them.
/// </summary>
/// <remarks>
/// Resolution rules: a schema's name is its file name without the <c>.json</c> extension (or the
/// name given to <see cref="Add"/>); lookups ignore case and accept the name with or without a
/// trailing <c>.json</c>. Two schemas with the same name are an error, also when they come from
/// different subfolders. Thread-safe for reads once loaded; loading is not meant to race with use.
/// </remarks>
public sealed class SchemaRegistry
{
    private readonly Dictionary<string, (JsonSchema Schema, string Source)> _schemas = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>Names of the registered schemas, sorted.</summary>
    public IReadOnlyList<string> Names
    {
        get
        {
            lock (_lock)
            {
                return [.. _schemas.Keys.Order(StringComparer.OrdinalIgnoreCase)];
            }
        }
    }

    /// <summary>
    /// Loads every <c>*.json</c> file under <paramref name="folder"/> (subfolders included) as a
    /// schema named after its file. Relative <c>$ref</c>s resolve against the file's location.
    /// </summary>
    /// <returns>The number of schemas loaded.</returns>
    /// <exception cref="InvalidOperationException">A file is not a valid JSON Schema, or a name repeats.</exception>
    public async Task<int> LoadFromFolderAsync(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        string[] files = Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            JsonSchema schema;
            try
            {
                schema = await JsonSchema.FromFileAsync(file);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Schema file '{file}' is not a valid JSON Schema: {ex.Message}", ex);
            }

            AddCore(Path.GetFileNameWithoutExtension(file), schema, file);
        }

        return files.Length;
    }

    /// <summary>Parses <paramref name="json"/> and registers it as <paramref name="name"/>.</summary>
    /// <exception cref="InvalidOperationException">Invalid JSON Schema, or the name is taken.</exception>
    public async Task<JsonSchema> AddAsync(string name, string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonSchema schema;
        try
        {
            schema = await JsonSchema.FromJsonAsync(json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Schema '{name}' is not a valid JSON Schema: {ex.Message}", ex);
        }

        AddCore(name, schema, "(inline)");
        return schema;
    }

    /// <summary>Registers <paramref name="schema"/> as <paramref name="name"/>.</summary>
    /// <exception cref="InvalidOperationException">The name is taken.</exception>
    public void Add(string name, JsonSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        AddCore(name, schema, "(code)");
    }

    /// <summary>Looks a schema up by name (see the resolution rules in the remarks).</summary>
    public bool TryGet(string name, [NotNullWhen(true)] out JsonSchema? schema)
    {
        schema = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        lock (_lock)
        {
            if (_schemas.TryGetValue(Normalize(name), out var entry))
            {
                schema = entry.Schema;
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the schema named <paramref name="name"/>.</summary>
    /// <exception cref="KeyNotFoundException">Not registered; the message lists the registered names.</exception>
    public JsonSchema Get(string name)
        => TryGet(name, out JsonSchema? schema)
            ? schema
            : throw new KeyNotFoundException($"Schema '{name}' is not registered. {DescribeRegistered()}");

    /// <summary>"Registered schemas: a, b." or a hint on how to load them when there are none.</summary>
    public string DescribeRegistered()
    {
        IReadOnlyList<string> names = Names;
        return names.Count == 0
            ? "No schemas are registered (load them with SchemaRegistry.LoadFromFolderAsync)."
            : $"Registered schemas: {string.Join(", ", names)}.";
    }

    /// <summary>Trims the name and removes a trailing <c>.json</c>.</summary>
    public static string Normalize(string name)
    {
        string trimmed = name.Trim();
        return trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? trimmed[..^5] : trimmed;
    }

    private void AddCore(string name, JsonSchema schema, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string key = Normalize(name);

        lock (_lock)
        {
            if (_schemas.TryGetValue(key, out var existing))
            {
                throw new InvalidOperationException(
                    $"Schema '{key}' is already registered (from {existing.Source}); cannot register it again from {source}.");
            }

            _schemas[key] = (schema, source);
        }
    }
}
