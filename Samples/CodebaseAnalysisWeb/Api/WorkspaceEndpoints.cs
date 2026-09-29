using CodebaseAnalysis.Core.Support;
using CodebaseAnalysisWeb.Analysis;
using CodebaseAnalysisWeb.Chat;
using CodebaseAnalysisWeb.Workspace;

namespace CodebaseAnalysisWeb.Api;

public sealed record WorkspaceDto(
    string? RepositoryPath,
    string? DatabasePath,
    string? ReportsPath,
    string? ChatPath,
    string? LogsPath,
    bool HasGraph,
    int ReportCount,
    bool AnalysisRunning,
    string? DocumentsPath,
    int DocumentCount,
    int CustomReportCount);

public sealed record OpenWorkspaceRequest(string? RepositoryPath);

public static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/workspace", async (WorkspaceState workspace, AnalysisRunManager analysis, CancellationToken ct) =>
            Results.Ok(await ToDtoAsync(workspace.Current, analysis.IsRunning, ct)));

        app.MapPut("/api/workspace", async (OpenWorkspaceRequest body, WorkspaceState workspace, AnalysisRunManager analysis, ChatService chat, CancellationToken ct) =>
        {
            var path = body.RepositoryPath?.Trim();
            if (string.IsNullOrEmpty(path) || !IsFullyQualified(path) || !Directory.Exists(path))
            {
                return new ApiError(ApiErrorCodes.InvalidPath,
                    $"La ruta '{path}' no es válida: tiene que ser absoluta y el directorio debe existir.").ToResult(StatusCodes.Status400BadRequest);
            }

            ActiveWorkspace active;
            lock (workspace.Gate)
            {
                if (analysis.IsRunning)
                    return ApiErrorsFactory.AnalysisRunning(analysis.Last?.RunId).ToResult(StatusCodes.Status409Conflict);
                if (chat.AnyTurnRunning)
                    return ApiErrorsFactory.ChatTurnRunning().ToResult(StatusCodes.Status409Conflict);
                if (workspace.AnyKnowledgeOperationRunning)
                    return ApiErrorsFactory.KnowledgeOperationRunning().ToResult(StatusCodes.Status409Conflict);

                try
                {
                    active = workspace.Activate(path);
                }
                catch (WorkspaceNotWritableException ex)
                {
                    // Activate comprueba la escritura antes de cambiar nada: el workspace anterior sigue activo.
                    return new ApiError(ApiErrorCodes.WorkspaceNotWritable, ex.Message).ToResult(StatusCodes.Status400BadRequest);
                }
            }

            return Results.Ok(await ToDtoAsync(active, analysis.IsRunning, ct));
        });
    }

    private static bool IsFullyQualified(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static async Task<WorkspaceDto> ToDtoAsync(ActiveWorkspace? active, bool analysisRunning, CancellationToken ct)
    {
        if (active == null)
            return new WorkspaceDto(null, null, null, null, null, false, 0, analysisRunning, null, 0, 0);

        // Mismo criterio que el 409 no_graph de las operaciones de grafo (plan v2 §8.1.3, hueco 4 del frontend).
        return new WorkspaceDto(
            active.RepositoryPath,
            active.DatabasePath,
            active.ReportsPath,
            active.ChatPath,
            active.LogsPath,
            await CodebaseAnalysis.Core.Knowledge.Graph.KnowledgeGraphIngestor.HasGraphAsync(active.DatabasePath, ct),
            ReportFiles.List(active.ReportsPath).Count,
            analysisRunning,
            active.Knowledge.DocumentsPath,
            active.Knowledge.CountDocuments(),
            active.Knowledge.CountCustomReports());
    }
}
