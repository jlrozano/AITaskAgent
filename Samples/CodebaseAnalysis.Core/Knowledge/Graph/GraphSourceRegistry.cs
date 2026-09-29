using CodebaseAnalysis.Core.Reports;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Knowledge.Graph;

/// <summary>Una fuente marcada para el grafo.</summary>
public sealed record GraphSourceEntry(string Key, DateTimeOffset RequestedAt);

/// <summary>
/// <c>knowledge/graph-sources.json</c> (plan v2 §8.1.3): <c>{ schemaVersion: 1, sources: [{ key, requestedAt }] }</c>,
/// escrito de forma atómica. Es lo que se reincorpora tras una reindexación.
/// </summary>
public sealed class GraphSourceRegistry(KnowledgePaths paths, TimeProvider? clock = null)
{
    private static readonly Lock WriteLock = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public IReadOnlyList<GraphSourceEntry> Load()
    {
        if (!File.Exists(paths.GraphSourcesFile))
            return [];
        try
        {
            return JsonSerializer.Deserialize<RegistryFile>(File.ReadAllText(paths.GraphSourcesFile), DocumentJson.Options)?.Sources ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    /// <summary>Fuentes válidas del registro, en orden de alta.</summary>
    public IReadOnlyList<SourceRef> Sources() =>
        Load().Select(e => SourceRef.TryParseKey(e.Key, out var s) ? s : null).OfType<SourceRef>().ToList();

    public bool Contains(SourceRef source) => Load().Any(e => e.Key == source.Key);

    public void Add(SourceRef source)
    {
        lock (WriteLock)
        {
            var entries = Load().Where(e => e.Key != source.Key).ToList();
            entries.Add(new GraphSourceEntry(source.Key, _clock.GetUtcNow()));
            Save(entries);
        }
    }

    public void Remove(SourceRef source)
    {
        lock (WriteLock)
        {
            var entries = Load().ToList();
            if (entries.RemoveAll(e => e.Key == source.Key) > 0)
                Save(entries);
        }
    }

    private void Save(List<GraphSourceEntry> entries) =>
        ReportHistoryStore.WriteAtomic(paths.GraphSourcesFile, JsonSerializer.SerializeToUtf8Bytes(new RegistryFile(1, entries), DocumentJson.Options));

    private sealed record RegistryFile(int SchemaVersion, List<GraphSourceEntry> Sources);
}

/// <summary>Opciones JSON de los ficheros de V2 (camelCase, indentado, sin escapar tildes).</summary>
internal static class DocumentJson
{
    public static JsonSerializerOptions Options => Documents.DocumentStore.JsonOptions;
}
