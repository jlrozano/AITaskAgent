using CodebaseAnalysis.Core.Knowledge.Graph;
using CodebaseAnalysis.Core.Reports;

namespace CodebaseAnalysis.Core.Knowledge.Embeddings;

/// <summary>Un resultado de <c>semantic_search</c> (plan v2 §8.2.4).</summary>
public sealed record SemanticSearchResult(
    float Score, SourceRef Source, string Title, string? Section, int? StartLine, int? EndLine, string Text, bool Stale);

/// <summary>Resumen de los embeddings del repositorio (<c>KnowledgeSummaryDto.embeddings</c>).</summary>
public sealed record EmbeddingsSummary(int SourceCount, int ChunkCount, int OutdatedCount);

/// <summary>
/// Indexar, quitar, consultar el estado y buscar (plan v2 §8.2). El estado no necesita red: compara el SHA-256 actual
/// de la fuente y el modelo configurado con lo guardado. Sin cliente (desactivado o sin clave), indexar y buscar son
/// <c>embeddings_disabled</c>.
/// </summary>
public sealed class KnowledgeEmbeddingService(KnowledgePaths paths, EmbeddingOptions options, IEmbeddingClient? client)
{
    public const int DefaultTopK = 8;
    public const int MaxTopK = 20;
    public const int MaxResultChars = 1200;

    public EmbeddingOptions Options { get; } = options;

    public EmbeddingStore Store { get; } = new(paths.EmbeddingsDatabasePath);

    public EmbeddingsKnowledgeState GetState(SourceRef source) => StateOf(source, Store.GetSource(source.Key));

    /// <summary><see cref="GetState"/> de varias fuentes con una sola consulta a la BD de embeddings, en su orden.</summary>
    public IReadOnlyList<EmbeddingsKnowledgeState> GetStates(IReadOnlyList<SourceRef> sources)
    {
        if (sources.Count == 0)
            return [];
        var rows = Store.ListSources().ToDictionary(s => s.SourceKey, StringComparer.Ordinal);
        return sources.Select(s => StateOf(s, rows.GetValueOrDefault(s.Key))).ToList();
    }

    private EmbeddingsKnowledgeState StateOf(SourceRef source, EmbeddedSource? row)
    {
        if (row == null)
            return EmbeddingsKnowledgeState.Absent;

        string state;
        if (row.Model != Options.Model || Options.Dimensions is int dims && dims != row.Dimensions)
            state = EmbeddingKnowledgeStates.ModelMismatch;
        else if (KnowledgeSourceReader.CurrentSha(paths, source) is not { } sha || sha != row.ContentSha256)
            state = EmbeddingKnowledgeStates.Outdated;
        else
            state = EmbeddingKnowledgeStates.Indexed;
        return new EmbeddingsKnowledgeState(state, row.ChunkCount, row.IndexedAt, row.Model);
    }

    public EmbeddingsSummary Summary()
    {
        var sources = Store.ListSources();
        var outdated = sources.Count(s => SourceRef.TryParseKey(s.SourceKey, out var source)
                                          && StateOf(source, s).State is EmbeddingKnowledgeStates.Outdated or EmbeddingKnowledgeStates.ModelMismatch);
        return new EmbeddingsSummary(sources.Count, sources.Sum(s => s.ChunkCount), outdated);
    }

    /// <summary>Indexa (o reindexa) la fuente entera.</summary>
    /// <exception cref="KnowledgeOperationException"><c>embeddings_disabled</c>, <c>not_found</c>, <c>invalid_request</c> o <c>embedding_provider_error</c>.</exception>
    public async Task<EmbeddingsKnowledgeState> IndexAsync(SourceRef source, CancellationToken ct)
    {
        var embeddings = RequireClient();
        var content = KnowledgeSourceReader.TryRead(paths, source)
            ?? throw new KnowledgeOperationException(KnowledgeErrorCodes.NotFound, $"No existe la fuente '{source.Key}'.");
        var chunks = Chunker.Split(content, Options.MaxChunkChars, Options.ChunkOverlapChars);
        if (chunks.Count == 0)
            throw new KnowledgeOperationException(KnowledgeErrorCodes.InvalidRequest, $"'{content.Title}' no tiene texto que indexar.");

        var vectors = await embeddings.EmbedAsync(chunks.Select(c => c.Text).ToList(), ct);
        var dimensions = vectors[0].Length;

        await KnowledgeGraphIngestor.WriteGate.WaitAsync(ct);
        try
        {
            // La llamada de red queda fuera del cerrojo: la fuente se ha podido borrar mientras tanto, y su baja ya
            // habría pasado por aquí sin nada que quitar.
            if (KnowledgeSourceReader.CurrentSha(paths, source) == null)
                throw new KnowledgeOperationException(KnowledgeErrorCodes.NotFound, $"La fuente '{source.Key}' se ha borrado mientras se indexaba.");
            Store.Replace(source, content.Title, content.Sha256, Options.Model, dimensions,
                chunks.Select((c, i) => new EmbeddedChunk(c, vectors[i])).ToList(), null);
        }
        finally
        {
            KnowledgeGraphIngestor.WriteGate.Release();
        }
        return GetState(source);
    }

    public async Task<EmbeddingsKnowledgeState> RemoveAsync(SourceRef source, CancellationToken ct)
    {
        await KnowledgeGraphIngestor.WriteGate.WaitAsync(ct);
        try
        {
            Store.Remove(source);
        }
        finally
        {
            KnowledgeGraphIngestor.WriteGate.Release();
        }
        return GetState(source);
    }

    /// <summary>Búsqueda por coseno sobre los trozos del modelo actual.</summary>
    public async Task<IReadOnlyList<SemanticSearchResult>> SearchAsync(string query, int? topK, IReadOnlyCollection<SourceKind>? kinds, CancellationToken ct)
    {
        var embeddings = RequireClient();
        if (string.IsNullOrWhiteSpace(query))
            throw new KnowledgeOperationException(KnowledgeErrorCodes.InvalidRequest, "La consulta está vacía.");

        var k = Math.Clamp(topK ?? DefaultTopK, 1, MaxTopK);
        var vector = (await embeddings.EmbedAsync([query.Trim()], ct))[0];
        var sources = Store.ListSources().ToDictionary(s => s.SourceKey);
        var hits = Store.Search(vector, Options.Model, vector.Length, k,
            key => kinds == null || kinds.Count == 0 || SourceRef.TryParseKey(key, out var s) && kinds.Contains(s.Kind));

        var result = new List<SemanticSearchResult>();
        foreach (var hit in hits)
        {
            if (!SourceRef.TryParseKey(hit.SourceKey, out var source))
                continue;
            sources.TryGetValue(hit.SourceKey, out var row);
            var stale = StateOf(source, row).State != EmbeddingKnowledgeStates.Indexed;
            var text = hit.Text.Length > MaxResultChars ? hit.Text[..MaxResultChars] + "…" : hit.Text;
            result.Add(new SemanticSearchResult(hit.Score, source, row?.Title ?? source.Id, hit.Heading, hit.StartLine, hit.EndLine, text, stale));
        }
        return result;
    }

    private IEmbeddingClient RequireClient() => client
        ?? throw new KnowledgeOperationException(KnowledgeErrorCodes.EmbeddingsDisabled, "Los embeddings no están disponibles en esta configuración.");
}
