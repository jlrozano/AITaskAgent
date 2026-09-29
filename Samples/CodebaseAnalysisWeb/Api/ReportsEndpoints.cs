using CodebaseAnalysis.Core.Chat;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysisWeb.Analysis;
using CodebaseAnalysisWeb.Knowledge;
using CodebaseAnalysisWeb.Workspace;

namespace CodebaseAnalysisWeb.Api;

public static class ReportsEndpoints
{
    public static void MapReportsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/reports", async (WorkspaceState workspace, KnowledgeService knowledge, CancellationToken ct) =>
        {
            var active = workspace.Current;
            if (active == null)
                return ApiErrorsFactory.NoWorkspace().ToResult(StatusCodes.Status409Conflict);

            // Solo el catálogo, como en v1: los propios van por GET /api/reports/custom (plan v2 §5.1).
            var result = new List<ReportSummaryDto>();
            foreach (var report in ReportFiles.List(active.ReportsPath))
                result.Add(await ReportSummaries.CatalogAsync(active, knowledge, report, ct));
            return Results.Ok(result);
        });

        app.MapGet("/api/reports/custom", async (WorkspaceState workspace, KnowledgeService knowledge, CancellationToken ct) =>
        {
            var active = workspace.Current;
            if (active == null)
                return ApiErrorsFactory.NoWorkspace().ToResult(StatusCodes.Status409Conflict);

            var result = new List<ReportSummaryDto>();
            foreach (var report in ReportLibrary.ListCustom(active.Knowledge))
                result.Add(await ReportSummaries.CustomAsync(active, knowledge, report, ct));
            return Results.Ok(result);
        });

        // Solo nombres del catálogo: cualquier otro (incluido "..%2F...") es 404 sin tocar el disco.
        app.MapGet("/api/reports/{fileName}", (string fileName, WorkspaceState workspace, CancellationToken ct) =>
            ReadAsync(workspace, SourceRef.ReportKindName, fileName, ct));

        app.MapGet("/api/reports/custom/{fileName}", (string fileName, WorkspaceState workspace, CancellationToken ct) =>
            ReadAsync(workspace, SourceRef.CustomKindName, fileName, ct));

        // Borrar un propio (solo la UI; el chat no tiene tool de borrado): versión «deleted» y fuera del grafo y de
        // los embeddings. La comprobación y el borrado, bajo el cerrojo común como restaurar (§5.5); las marcas de los
        // artefactos y el grafo, fuera, con la operación de conocimiento tomada (no deja empezar un turno ni un análisis).
        app.MapDelete("/api/reports/custom/{fileName}", async (string fileName, WorkspaceState workspace, AnalysisRunManager analysis,
            KnowledgeService knowledge, ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (!TryResolve(workspace, SourceRef.CustomKindName, fileName, out var active, out var source, out var error))
                return error;

            IDisposable lease;
            ReportVersion deleted;
            ChatSessionStore? chats;
            lock (workspace.Gate)
            {
                if (Busy(workspace, analysis) is { } busy)
                    return busy;
                if (!ReportLibrary.Exists(active.Knowledge, source))
                    return NotFound(fileName);

                deleted = new ReportWriter(active.Knowledge).DeleteCustom(source, ReportVersionOrigin.Ui);
                chats = workspace.ChatStore;
                lease = workspace.BeginKnowledgeOperation();
            }

            using (lease)
            {
                await AfterUndoAsync(loggers, source, deleted.VersionId, async () =>
                {
                    KnowledgeService.MarkArtifactsUndone(chats, source, null, deleted.VersionId);
                    await knowledge.ForgetSourceAsync(active, source, ct);
                });
            }
            return Results.NoContent();
        });

        MapVersionRoutes(app, "/api/reports/{fileName}", SourceRef.ReportKindName);
        MapVersionRoutes(app, "/api/reports/custom/{fileName}", SourceRef.CustomKindName);
    }

    private static void MapVersionRoutes(IEndpointRouteBuilder app, string prefix, string kind)
    {
        app.MapGet(prefix + "/versions", (string fileName, WorkspaceState workspace) =>
        {
            if (!TryResolve(workspace, kind, fileName, out var active, out var source, out var error))
                return error;

            var history = new ReportHistoryStore(active.Knowledge);
            var versions = history.List(source);
            if (versions.Count == 0 && !ReportLibrary.Exists(active.Knowledge, source))
                return NotFound(fileName);

            // isCurrent: a lo sumo una, la más reciente con el SHA del fichero (una restauración repite SHA).
            var currentSha = ReportHistoryStore.FileSha256(ReportLibrary.GetFilePath(active.Knowledge, source));
            var currentId = versions.LastOrDefault(v => v.Sha256 == currentSha)?.VersionId;
            return Results.Ok(versions.Reverse().Select(v => ReportSummaries.ToDto(v, v.VersionId == currentId)).ToList());
        });

        app.MapGet(prefix + "/versions/{versionId}", (string fileName, string versionId, WorkspaceState workspace) =>
        {
            if (!TryResolve(workspace, kind, fileName, out var active, out var source, out var error))
                return error;

            var content = new ReportHistoryStore(active.Knowledge).ReadContent(source, versionId);
            return content == null
                ? VersionNotFound(fileName, versionId)
                : Results.Text(content, "text/markdown; charset=utf-8");
        });

        // Restaurar, bajo el cerrojo común (§5.5). Las marcas de los artefactos y la reingesta en el grafo van fuera del
        // cerrojo con la operación de conocimiento tomada, y un fallo en ellas ya no es un 500: la restauración está hecha.
        app.MapPost(prefix + "/versions/{versionId}/restore", async (string fileName, string versionId, EmptyBody _,
            WorkspaceState workspace, AnalysisRunManager analysis, KnowledgeService knowledge, ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (!TryResolve(workspace, kind, fileName, out var active, out var source, out var error))
                return error;

            IDisposable lease;
            ReportVersion restored;
            ChatSessionStore? chats;
            lock (workspace.Gate)
            {
                if (Busy(workspace, analysis) is { } busy)
                    return busy;

                try
                {
                    restored = new ReportWriter(active.Knowledge).Restore(source, versionId, ReportVersionOrigin.Ui);
                }
                catch (KnowledgeOperationException ex) when (ex.Code == KnowledgeErrorCodes.NotFound)
                {
                    return NotFound(fileName);
                }
                catch (KnowledgeOperationException ex) when (ex.Code == KnowledgeErrorCodes.VersionNotFound)
                {
                    return VersionNotFound(fileName, versionId);
                }
                chats = workspace.ChatStore;
                lease = workspace.BeginKnowledgeOperation();
            }

            using (lease)
            {
                await AfterUndoAsync(loggers, source, restored.VersionId, async () =>
                {
                    KnowledgeService.MarkArtifactsUndone(chats, source, versionId, restored.VersionId);
                    await ReportKnowledgeHooks.AfterWriteAsync(active.Knowledge, active.DatabasePath, source, ct);
                });
                return Results.Ok(await ReportSummaries.GetAsync(active, knowledge, source, ct));
            }
        });
    }

    /// <summary>
    /// Lo que sigue a restaurar o borrar (marcas de artefactos, grafo, embeddings): el cambio del informe ya está hecho y
    /// versionado, así que un fallo aquí se registra como aviso en lugar de convertir la respuesta en un 500.
    /// </summary>
    private static async Task AfterUndoAsync(ILoggerFactory loggers, SourceRef source, string versionId, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggers.CreateLogger(typeof(ReportsEndpoints)).LogWarning(ex,
                "{Source} quedó en {VersionId}, pero no se pudieron marcar los artefactos o actualizar el grafo", source.Key, versionId);
        }
    }

    private static async Task<IResult> ReadAsync(WorkspaceState workspace, string kind, string fileName, CancellationToken ct)
    {
        if (!TryResolve(workspace, kind, fileName, out var active, out var source, out var error))
            return error;

        var path = ReportLibrary.GetFilePath(active.Knowledge, source);
        try
        {
            if (!File.Exists(path))
                return NotFound(fileName);
            return Results.Text(await File.ReadAllTextAsync(path, ct), "text/markdown; charset=utf-8");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Borrado entre la comprobación y la lectura.
            return NotFound(fileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Bloqueado por quien lo escribe (un análisis, un editor) o sin permiso: es transitorio o ajeno a
            // la petición, así que 503 y el cliente puede reintentar.
            return new ApiError(ApiErrorCodes.ReportUnavailable, $"No se puede leer el informe '{fileName}' ahora: {ex.Message}")
                .ToResult(StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>Workspace activo y fuente validada; si no, el error (409 sin workspace, 404 con un nombre que no vale).</summary>
    private static bool TryResolve(WorkspaceState workspace, string kind, string fileName,
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

        if (!SourceRef.TryCreate(kind, fileName, out var parsed, out _))
        {
            error = NotFound(fileName);
            return false;
        }

        source = parsed;
        return true;
    }

    /// <summary>Restaurar y borrar no conviven con un análisis ni con un turno de chat (§5.5). Llamar con el cerrojo tomado.</summary>
    private static IResult? Busy(WorkspaceState workspace, AnalysisRunManager analysis)
    {
        if (analysis.IsRunning)
            return ApiErrorsFactory.AnalysisRunning(analysis.Last?.RunId).ToResult(StatusCodes.Status409Conflict);
        if (workspace.AnyChatTurnRunning)
            return ApiErrorsFactory.ChatTurnRunning().ToResult(StatusCodes.Status409Conflict);
        return null;
    }

    private static IResult NotFound(string fileName) =>
        ApiErrorsFactory.NotFound($"No existe el informe '{fileName}'.").ToResult(StatusCodes.Status404NotFound);

    private static IResult VersionNotFound(string fileName, string versionId) =>
        new ApiError(ApiErrorCodes.VersionNotFound, $"No existe la versión '{versionId}' del informe '{fileName}'.")
            .ToResult(StatusCodes.Status404NotFound);
}

/// <summary><see cref="ReportSummaryDto"/> con historial y conocimiento reales (plan v2 §5.2).</summary>
internal static class ReportSummaries
{
    public static async Task<ReportSummaryDto> CatalogAsync(ActiveWorkspace active, KnowledgeService knowledge, ReportSummary report, CancellationToken ct)
    {
        var source = SourceRef.Report(report.FileName);
        var writer = new ReportWriter(active.Knowledge);
        return ReportSummaryDto.FromCatalog(report) with
        {
            ModifiedByChat = writer.IsModifiedByChat(source),
            VersionCount = writer.History.List(source).Count,
            Knowledge = await knowledge.GetStateAsync(active, source, ct)
        };
    }

    public static async Task<ReportSummaryDto> CustomAsync(ActiveWorkspace active, KnowledgeService knowledge, CustomReportSummary report, CancellationToken ct)
    {
        var source = SourceRef.Custom(report.FileName);
        var writer = new ReportWriter(active.Knowledge);
        var versions = writer.History.List(source);
        var origin = versions.Count > 0 && versions[0].Origin.Type != ReportVersionOrigin.ChatType && versions[0].Action != ReportVersionActionNames.Created
            ? ReportOrigins.Pipeline
            : ReportOrigins.Chat;
        return new ReportSummaryDto(
            report.FileName,
            null,
            report.Title,
            report.Health,
            report.CriticalFindings,
            report.SizeBytes,
            report.LastModified,
            ReportKinds.Custom,
            ReportLibrary.ToId(source),
            origin,
            writer.IsModifiedByChat(source),
            versions.Count,
            await knowledge.GetStateAsync(active, source, ct));
    }

    public static async Task<ReportSummaryDto?> GetAsync(ActiveWorkspace active, KnowledgeService knowledge, SourceRef source, CancellationToken ct)
    {
        if (source.Kind == SourceKind.Custom)
            return ReportLibrary.TryGetCustom(active.Knowledge, source) is { } custom ? await CustomAsync(active, knowledge, custom, ct) : null;

        return ReportFiles.List(active.ReportsPath).FirstOrDefault(r => r.FileName == source.Id) is { } catalog
            ? await CatalogAsync(active, knowledge, catalog, ct)
            : null;
    }

    public static ReportVersionDto ToDto(ReportVersion version, bool isCurrent) => new(
        version.VersionId,
        version.CreatedAt,
        version.Action,
        new ReportVersionOriginDto(
            version.Origin.Type,
            version.Origin.Type == ReportVersionOrigin.ChatType ? version.Origin.SessionId : null,
            version.Origin.Type == ReportVersionOrigin.ChatType ? version.Origin.TurnId : null),
        version.Note,
        version.SizeBytes,
        version.Sha256,
        isCurrent);
}
