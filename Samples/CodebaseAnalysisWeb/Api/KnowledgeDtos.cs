using CodebaseAnalysis.Core.Knowledge;

namespace CodebaseAnalysisWeb.Api;

/// <summary>
/// Fuente de conocimiento en la API: <c>{ "kind": "document" | "report" | "custom", "id": "…" }</c>. Con <c>custom</c>, el
/// id es el nombre del fichero sin <c>custom/</c>. Se valida con <see cref="SourceRef.TryCreate"/>: un tipo o un id
/// inválidos son 400 <c>invalid_request</c>; una fuente válida que no existe, 404 <c>not_found</c>.
/// </summary>
public sealed record SourceRefDto(string Kind, string Id)
{
    public static SourceRefDto From(SourceRef source) => new(source.KindName, source.Id);
}

/// <summary>Cuerpo de <c>POST /api/knowledge/graph</c> y <c>POST /api/knowledge/embeddings</c>.</summary>
public sealed record KnowledgeSourceRequest(SourceRefDto? Source);

/// <summary>Respuesta de las operaciones de conocimiento: la fuente y su estado después de la operación.</summary>
public sealed record KnowledgeStatusDto(SourceRefDto Source, GraphKnowledgeState Graph, EmbeddingsKnowledgeState Embeddings)
{
    public static KnowledgeStatusDto From(SourceRef source, KnowledgeState state) =>
        new(SourceRefDto.From(source), state.Graph, state.Embeddings);
}

/// <summary><c>KnowledgeSummaryDto.graph</c>: fuentes del registro del grafo.</summary>
public sealed record KnowledgeGraphSummaryDto(int SourceCount);

/// <summary>
/// <c>KnowledgeSummaryDto.embeddings</c>. <c>model</c> y <c>dimensions</c>: los configurados (null si está desactivado).
/// <c>outdatedCount</c> cuenta las fuentes <c>outdated</c> y <c>model_mismatch</c>, que son las que actualiza
/// «Actualizar desactualizados».
/// </summary>
public sealed record KnowledgeEmbeddingsSummaryDto(
    bool Enabled,
    string? Model,
    int? Dimensions,
    int SourceCount,
    int ChunkCount,
    int OutdatedCount);

/// <summary><c>GET /api/knowledge</c> (plan v2 §5.2). Sin workspace, todo a 0 con <c>enabled</c> según la configuración.</summary>
public sealed record KnowledgeSummaryDto(KnowledgeGraphSummaryDto Graph, KnowledgeEmbeddingsSummaryDto Embeddings);
