namespace CodebaseAnalysis.Core.Knowledge;

/// <summary>Estados de una fuente en el grafo (plan v2 §8.1.3), tal como salen en la API.</summary>
public static class GraphKnowledgeStates
{
    /// <summary>Ni en el registro ni en el grafo.</summary>
    public const string Absent = "absent";

    /// <summary>En el grafo con el SHA-256 del contenido actual.</summary>
    public const string Present = "present";

    /// <summary>En el grafo, pero con otro SHA-256: el contenido cambió después de añadirla.</summary>
    public const string Stale = "stale";

    /// <summary>En el registro (<c>graph-sources.json</c>) pero no en el grafo: pendiente de reincorporar tras una reindexación.</summary>
    public const string Pending = "pending";
}

/// <summary>Estados de una fuente en los embeddings (plan v2 §8.2.3), tal como salen en la API.</summary>
public static class EmbeddingKnowledgeStates
{
    /// <summary>Sin trozos indexados.</summary>
    public const string Absent = "absent";

    /// <summary>Indexada con el SHA-256 actual, el modelo y las dimensiones configurados.</summary>
    public const string Indexed = "indexed";

    /// <summary>Indexada con otro SHA-256, o el fichero ya no existe.</summary>
    public const string Outdated = "outdated";

    /// <summary>Indexada con otro modelo u otras dimensiones que los configurados.</summary>
    public const string ModelMismatch = "model_mismatch";
}

/// <summary>Estado de una fuente en el grafo. Con <c>absent</c> y <c>pending</c>, recuentos a 0 y <see cref="AddedAt"/> null.</summary>
public sealed record GraphKnowledgeState(string State, int NodeCount, int EdgeCount, DateTimeOffset? AddedAt)
{
    public static GraphKnowledgeState Absent { get; } = new(GraphKnowledgeStates.Absent, 0, 0, null);
}

/// <summary>Estado de una fuente en los embeddings. Con <c>absent</c>, <see cref="ChunkCount"/> 0 y el resto null.</summary>
public sealed record EmbeddingsKnowledgeState(string State, int ChunkCount, DateTimeOffset? IndexedAt, string? Model)
{
    public static EmbeddingsKnowledgeState Absent { get; } = new(EmbeddingKnowledgeStates.Absent, 0, null, null);
}

/// <summary>
/// <c>KnowledgeState</c> de la API (plan v2 §5.2): lo llevan <c>ReportSummaryDto</c>, <c>DocumentSummaryDto</c> y
/// <c>KnowledgeStatusDto</c>, y lo leen las tools del chat. Se serializa tal cual.
/// </summary>
public sealed record KnowledgeState(GraphKnowledgeState Graph, EmbeddingsKnowledgeState Embeddings)
{
    public static KnowledgeState Absent { get; } = new(GraphKnowledgeState.Absent, EmbeddingsKnowledgeState.Absent);
}
