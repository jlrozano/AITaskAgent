using CodebaseAnalysis.Core.Chat;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Knowledge.Embeddings;
using CodebaseAnalysis.Core.Knowledge.Graph;
using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysisWeb.Analysis;
using CodebaseAnalysisWeb.Api;
using CodebaseAnalysisWeb.Workspace;

namespace CodebaseAnalysisWeb.Knowledge;

/// <summary>Filas de la matriz de concurrencia de §5.5 que necesitan cerrojo.</summary>
public enum KnowledgeOperation
{
    /// <summary>Subir un documento: permitida con todo, pero cuenta como operación de conocimiento.</summary>
    Upload,

    /// <summary>
    /// Borrar un documento que no está en el grafo (quita sus embeddings): como subir, permitida con todo (fila 1 de
    /// §5.5), pero cuenta mientras dura para que no empiece un análisis, un turno ni un cambio de workspace.
    /// </summary>
    DeleteDocument,

    /// <summary>Añadir o quitar del grafo; borrar un documento que está en el grafo.</summary>
    GraphWrite,

    /// <summary>Indexar o quitar embeddings de un documento o informe propio.</summary>
    Embeddings,

    /// <summary>Indexar embeddings de un informe del catálogo (el análisis lo regenera).</summary>
    CatalogEmbeddings,

    /// <summary>Describir una imagen.</summary>
    Describe
}

/// <summary>
/// Orquesta las operaciones de conocimiento de la web (plan v2 §5.5): estado de cada fuente en el grafo y en los
/// embeddings, la matriz de concurrencia bajo <see cref="WorkspaceState.Gate"/> y las altas y bajas.
/// </summary>
public sealed class KnowledgeService(WorkspaceState workspace, AnalysisRunManager analysis, EmbeddingsRuntime embeddings)
{
    public EmbeddingsRuntime Embeddings { get; } = embeddings;

    public Task<KnowledgeState> GetStateAsync(ActiveWorkspace active, SourceRef source, CancellationToken ct) =>
        KnowledgeStateReader.GetAsync(active.Knowledge, active.DatabasePath, Embeddings.Options, source, ct);

    /// <summary>Estado de las filas de un listado con una conexión al grafo y una consulta a los embeddings.</summary>
    public Task<IReadOnlyList<KnowledgeState>> GetStatesAsync(ActiveWorkspace active, IReadOnlyList<SourceRef> sources, CancellationToken ct) =>
        KnowledgeStateReader.GetManyAsync(active.Knowledge, active.DatabasePath, Embeddings.Options, sources, ct);

    /// <summary>
    /// Comprueba la fila de la matriz y apunta la operación. Devuelve el <see cref="IDisposable"/> que la retira, o el
    /// 409 que corresponde.
    /// </summary>
    public (IDisposable? Lease, IResult? Error) Begin(KnowledgeOperation operation)
    {
        lock (workspace.Gate)
        {
            if (operation is KnowledgeOperation.GraphWrite or KnowledgeOperation.CatalogEmbeddings && analysis.IsRunning)
                return (null, ApiErrorsFactory.AnalysisRunning(analysis.Last?.RunId).ToResult(StatusCodes.Status409Conflict));
            if (operation == KnowledgeOperation.GraphWrite && workspace.AnyChatTurnRunning)
                return (null, ApiErrorsFactory.ChatTurnRunning().ToResult(StatusCodes.Status409Conflict));
            if (operation is not (KnowledgeOperation.Upload or KnowledgeOperation.DeleteDocument) && workspace.AnyKnowledgeOperationRunning)
                return (null, ApiErrorsFactory.KnowledgeOperationRunning().ToResult(StatusCodes.Status409Conflict));
            return (workspace.BeginKnowledgeOperation(), null);
        }
    }

    public static bool SourceExists(ActiveWorkspace active, SourceRef source) => source.Kind == SourceKind.Document
        ? new CodebaseAnalysis.Core.Documents.DocumentStore(active.Knowledge).TryGet(source.Id) != null
        : ReportLibrary.Exists(active.Knowledge, source);

    /// <summary>True si la fuente está en el registro del grafo o tiene nodos en él.</summary>
    public async Task<bool> IsInGraphAsync(ActiveWorkspace active, SourceRef source, CancellationToken ct) =>
        (await KnowledgeGraphIngestor.GetStateAsync(active.DatabasePath, active.Knowledge, source, ct)).State != GraphKnowledgeStates.Absent;

    /// <summary>Quita una fuente borrada del grafo y de los embeddings.</summary>
    public async Task ForgetSourceAsync(ActiveWorkspace active, SourceRef source, CancellationToken ct)
    {
        await KnowledgeGraphIngestor.RemoveAsync(active.DatabasePath, active.Knowledge, source, ct);
        await Embeddings.For(active.Knowledge).RemoveAsync(source, ct);
    }

    public KnowledgeSummaryDto Summary(ActiveWorkspace? active)
    {
        var enabled = Embeddings.Enabled;
        var model = enabled ? Embeddings.Options.Model : null;
        int? dimensions = enabled ? Embeddings.Options.EffectiveDimensions : null;
        if (active == null)
            return new KnowledgeSummaryDto(new KnowledgeGraphSummaryDto(0), new KnowledgeEmbeddingsSummaryDto(enabled, model, dimensions, 0, 0, 0));

        var summary = Embeddings.For(active.Knowledge).Summary();
        return new KnowledgeSummaryDto(
            new KnowledgeGraphSummaryDto(new GraphSourceRegistry(active.Knowledge).Sources().Count),
            new KnowledgeEmbeddingsSummaryDto(enabled, model, dimensions, summary.SourceCount, summary.ChunkCount, summary.OutdatedCount));
    }

    /// <summary>Serializa las marcas de artefactos, que leen y reescriben sesiones enteras fuera de <see cref="WorkspaceState.Gate"/>.</summary>
    private static readonly Lock ArtifactsLock = new();

    /// <summary>
    /// Marca como deshechos los artefactos de chat de un informe (hueco 2 del frontend): los que «Deshacer» revierte al
    /// restaurar <paramref name="restoredVersionId"/> (su <c>previousVersionId</c>, con <c>undoneAction: restored</c>), o
    /// todos si se borra el informe (<paramref name="restoredVersionId"/> null, <c>undoneAction: deleted</c>, también los
    /// ya deshechos por una restauración: el informe ya no existe y el chip no debe ofrecer «Abrir»). Se llama fuera de
    /// <see cref="WorkspaceState.Gate"/> pero con la operación de conocimiento tomada, que impide empezar un turno.
    /// </summary>
    public static void MarkArtifactsUndone(ChatSessionStore? store, SourceRef report, string? restoredVersionId, string undoneBy)
    {
        if (store == null)
            return;

        var id = ReportLibrary.ToId(report);
        var deleted = restoredVersionId == null;
        var action = deleted ? ReportVersionActionNames.Deleted : ReportVersionActionNames.Restored;
        lock (ArtifactsLock)
        {
            foreach (var summary in store.List())
            {
                var session = store.TryGet(summary.Id);
                if (session == null)
                    continue;

                var changed = false;
                foreach (var turn in session.Turns)
                {
                    if (turn.Artifacts == null)
                        continue;
                    for (var i = 0; i < turn.Artifacts.Count; i++)
                    {
                        var artifact = turn.Artifacts[i];
                        if (artifact.Id != id || artifact.UndoneAction == ReportVersionActionNames.Deleted)
                            continue;
                        if (!deleted && (artifact.UndoneByVersionId != null || artifact.PreviousVersionId != restoredVersionId))
                            continue;
                        turn.Artifacts[i] = artifact with { UndoneByVersionId = undoneBy, UndoneAction = action };
                        changed = true;
                    }
                }

                if (changed)
                    store.Save(session);
            }
        }
    }
}
