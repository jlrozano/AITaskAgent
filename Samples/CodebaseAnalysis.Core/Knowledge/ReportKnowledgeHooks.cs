using CodebaseAnalysis.Core.Knowledge.Embeddings;
using CodebaseAnalysis.Core.Knowledge.Graph;

namespace CodebaseAnalysis.Core.Knowledge;

/// <summary>
/// Lo que pasa en el grafo y en los embeddings cuando se escribe un informe (plan v2 §6.2): si está en el grafo se
/// reingesta en el acto (determinista y local); si tiene embeddings, quedan desactualizados (su SHA ya no coincide) y se avisa.
/// </summary>
public static class ReportKnowledgeHooks
{
    public static async Task<(bool GraphRefreshed, bool EmbeddingsOutdated)> AfterWriteAsync(
        KnowledgePaths paths, string? databasePath, SourceRef report, CancellationToken ct)
    {
        var graphRefreshed = false;
        if (databasePath != null && new GraphSourceRegistry(paths).Contains(report) && await KnowledgeGraphIngestor.HasGraphAsync(databasePath, ct))
        {
            await KnowledgeGraphIngestor.AddAsync(databasePath, paths, report, ct);
            graphRefreshed = true;
        }

        var embeddingsOutdated = new EmbeddingStore(paths.EmbeddingsDatabasePath).GetSource(report.Key) is { } row
                                 && row.ContentSha256 != KnowledgeSourceReader.CurrentSha(paths, report);
        return (graphRefreshed, embeddingsOutdated);
    }
}

/// <summary><c>KnowledgeState</c> de una fuente: grafo y embeddings, sin red.</summary>
public static class KnowledgeStateReader
{
    public static async Task<KnowledgeState> GetAsync(KnowledgePaths paths, string? databasePath, EmbeddingOptions? embeddings, SourceRef source, CancellationToken ct)
    {
        var graph = await KnowledgeGraphIngestor.GetStateAsync(databasePath, paths, source, ct);
        var embedding = new KnowledgeEmbeddingService(paths, embeddings ?? new EmbeddingOptions(), null).GetState(source);
        return new KnowledgeState(graph, embedding);
    }

    /// <summary>Estado de varias fuentes con una conexión al grafo y una consulta a los embeddings, en su orden.</summary>
    public static async Task<IReadOnlyList<KnowledgeState>> GetManyAsync(KnowledgePaths paths, string? databasePath, EmbeddingOptions? embeddings, IReadOnlyList<SourceRef> sources, CancellationToken ct)
    {
        var graph = await KnowledgeGraphIngestor.GetStatesAsync(databasePath, paths, sources, ct);
        var embedding = new KnowledgeEmbeddingService(paths, embeddings ?? new EmbeddingOptions(), null).GetStates(sources);
        return sources.Select((_, i) => new KnowledgeState(graph[i], embedding[i])).ToList();
    }
}
