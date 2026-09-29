using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Knowledge.Graph;
using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysisWeb.Knowledge;
using CodebaseAnalysisWeb.Workspace;
using Microsoft.AspNetCore.Mvc;

namespace CodebaseAnalysisWeb.Api;

/// <summary>Grafo y embeddings de las fuentes de conocimiento (plan v2 §5.1, §8), con la matriz de §5.5.</summary>
public static class KnowledgeEndpoints
{
    public static void MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/knowledge", (WorkspaceState workspace, KnowledgeService knowledge) =>
            Results.Ok(knowledge.Summary(workspace.Current)));

        // ── Grafo (D1) ────────────────────────────────────────────────────────
        app.MapPost("/api/knowledge/graph", async (KnowledgeSourceRequest body, WorkspaceState workspace, KnowledgeService knowledge, CancellationToken ct) =>
        {
            if (!TryResolve(workspace, body.Source?.Kind, body.Source?.Id, requireExisting: true, out var active, out var source, out var error))
                return error;

            var (lease, busy) = knowledge.Begin(KnowledgeOperation.GraphWrite);
            if (lease == null)
                return busy!;
            using (lease)
            {
                try
                {
                    await KnowledgeGraphIngestor.AddAsync(active.DatabasePath, active.Knowledge, source, ct);
                    return Results.Ok(KnowledgeStatusDto.From(source, await knowledge.GetStateAsync(active, source, ct)));
                }
                catch (KnowledgeOperationException ex)
                {
                    return ApiErrorsFactory.FromKnowledge(ex);
                }
            }
        }).WithMetadata(new RequestSizeLimitAttribute(DocumentsEndpoints.SmallBodyLimit));

        app.MapDelete("/api/knowledge/graph/{kind}/{id}", async (string kind, string id, WorkspaceState workspace, KnowledgeService knowledge, CancellationToken ct) =>
        {
            if (!TryResolve(workspace, kind, id, requireExisting: false, out var active, out var source, out var error))
                return error;
            if (!KnowledgeService.SourceExists(active, source) && !await knowledge.IsInGraphAsync(active, source, ct))
                return NotFound(source);

            var (lease, busy) = knowledge.Begin(KnowledgeOperation.GraphWrite);
            if (lease == null)
                return busy!;
            using (lease)
            {
                await KnowledgeGraphIngestor.RemoveAsync(active.DatabasePath, active.Knowledge, source, ct);
                return Results.Ok(KnowledgeStatusDto.From(source, await knowledge.GetStateAsync(active, source, ct)));
            }
        });

        // ── Embeddings (D2) ───────────────────────────────────────────────────
        app.MapPost("/api/knowledge/embeddings", async (KnowledgeSourceRequest body, WorkspaceState workspace, KnowledgeService knowledge, CancellationToken ct) =>
        {
            if (!TryResolve(workspace, body.Source?.Kind, body.Source?.Id, requireExisting: true, out var active, out var source, out var error))
                return error;
            if (!knowledge.Embeddings.Enabled)
            {
                return new ApiError(ApiErrorCodes.EmbeddingsDisabled, knowledge.Embeddings.DisabledReason ?? "Los embeddings no están disponibles.")
                    .ToResult(StatusCodes.Status409Conflict);
            }

            var (lease, busy) = knowledge.Begin(source.Kind == SourceKind.Report ? KnowledgeOperation.CatalogEmbeddings : KnowledgeOperation.Embeddings);
            if (lease == null)
                return busy!;
            using (lease)
            {
                try
                {
                    await knowledge.Embeddings.For(active.Knowledge).IndexAsync(source, ct);
                    return Results.Ok(KnowledgeStatusDto.From(source, await knowledge.GetStateAsync(active, source, ct)));
                }
                catch (KnowledgeOperationException ex)
                {
                    return ApiErrorsFactory.FromKnowledge(ex);
                }
            }
        }).WithMetadata(new RequestSizeLimitAttribute(DocumentsEndpoints.SmallBodyLimit));

        app.MapDelete("/api/knowledge/embeddings/{kind}/{id}", async (string kind, string id, WorkspaceState workspace, KnowledgeService knowledge, CancellationToken ct) =>
        {
            if (!TryResolve(workspace, kind, id, requireExisting: false, out var active, out var source, out var error))
                return error;
            var service = knowledge.Embeddings.For(active.Knowledge);
            if (!KnowledgeService.SourceExists(active, source) && service.GetState(source).State == EmbeddingKnowledgeStates.Absent)
                return NotFound(source);

            var (lease, busy) = knowledge.Begin(KnowledgeOperation.Embeddings);
            if (lease == null)
                return busy!;
            using (lease)
            {
                await service.RemoveAsync(source, ct);
                return Results.Ok(KnowledgeStatusDto.From(source, await knowledge.GetStateAsync(active, source, ct)));
            }
        });
    }

    /// <summary>Workspace y fuente validada (decisión 0-3): 409 sin workspace, 400 con tipo o id inválidos, 404 si no existe.</summary>
    private static bool TryResolve(WorkspaceState workspace, string? kind, string? id, bool requireExisting,
        out ActiveWorkspace active, out SourceRef source, out IResult error)
    {
        active = workspace.Current!;
        source = null!;
        error = null!;
        if (active == null)
        {
            error = ApiErrorsFactory.NoWorkspace().ToResult(StatusCodes.Status409Conflict);
            return false;
        }

        if (!SourceRef.TryCreate(kind, id, out var parsed, out var message))
        {
            error = new ApiError(ApiErrorCodes.InvalidRequest, message).ToResult(StatusCodes.Status400BadRequest);
            return false;
        }

        source = parsed;
        if (requireExisting && !KnowledgeService.SourceExists(active, source))
        {
            error = NotFound(source);
            return false;
        }
        return true;
    }

    private static IResult NotFound(SourceRef source) =>
        ApiErrorsFactory.NotFound($"No existe la fuente '{source.KindName}/{source.Id}'.").ToResult(StatusCodes.Status404NotFound);
}
