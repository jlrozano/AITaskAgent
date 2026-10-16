using CodebaseAnalysisWeb.Analysis;
using CodebaseAnalysisWeb.Sse;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace CodebaseAnalysisWeb.Api;

public static class AnalysisEndpoints
{
    /// <summary>Nombre del evento SSE del análisis: el tipo va en <c>type</c> (plan §4.2).</summary>
    public const string SseEventName = "analysis";

    public static void MapAnalysisEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/analysis", (StartAnalysisRequest body, AnalysisRunManager manager) =>
        {
            var started = manager.Start(body);
            return started.Run != null
                ? Results.Json(started.Run.ToDto(), statusCode: StatusCodes.Status202Accepted)
                : started.Error!.ToResult(started.StatusCode);
        });

        app.MapGet("/api/analysis/current", (AnalysisRunManager manager) =>
        {
            var last = manager.Last;
            return last == null ? Results.NoContent() : Results.Ok(last.ToDto());
        });

        app.MapPost("/api/analysis/{runId}/cancel", (string runId, EmptyBody body, AnalysisRunManager manager) =>
        {
            var run = manager.Find(runId);
            if (run == null)
                return ApiErrorsFactory.NotFound($"No existe la ejecución '{runId}'.").ToResult(StatusCodes.Status404NotFound);

            if (!manager.Cancel(run))
                return new ApiError(ApiErrorCodes.NotRunning, "La ejecución ya no está en curso.", run.RunId).ToResult(StatusCodes.Status409Conflict);

            return Results.Json(run.ToDto(), statusCode: StatusCodes.Status202Accepted);
        });

        // Vuelve a generar informes de la última ejecución, ya terminada, con el mismo runId y el mismo log de eventos.
        app.MapPost("/api/analysis/{runId}/regenerate", (string runId, RegenerateReportsRequest body, AnalysisRunManager manager) =>
        {
            var started = manager.Regenerate(runId, body);
            return started.Run != null
                ? Results.Json(started.Run.ToDto(), statusCode: StatusCodes.Status202Accepted)
                : started.Error!.ToResult(started.StatusCode);
        });

        // Verifica informes de la última ejecución, ya terminada, contra el código (mismo runId y mismo log que regenerar).
        app.MapPost("/api/analysis/{runId}/verify", (string runId, VerifyReportsRequest body, AnalysisRunManager manager) =>
        {
            var started = manager.Verify(runId, body);
            return started.Run != null
                ? Results.Json(started.Run.ToDto(), statusCode: StatusCodes.Status202Accepted)
                : started.Error!.ToResult(started.StatusCode);
        });

        app.MapGet("/api/analysis/{runId}/events", async (
            string runId,
            long? after,
            HttpContext http,
            AnalysisRunManager manager,
            IOptions<JsonOptions> json,
            SseOptions sseOptions) =>
        {
            var run = manager.Find(runId);
            if (run == null)
            {
                await ApiErrorsFactory.NotFound($"No existe la ejecución '{runId}'.").ToResult(StatusCodes.Status404NotFound).ExecuteAsync(http);
                return;
            }

            // after explícito > Last-Event-ID > 0.
            var from = after
                ?? (long.TryParse(http.Request.Headers["Last-Event-ID"].ToString(), out var lastEventId) ? lastEventId : 0);

            var (replay, live) = run.EventLog.Subscribe(Math.Max(0, from));
            using var subscription = live;

            try
            {
                await using var sse = await SseWriter.StartAsync(http, json.Value.SerializerOptions, sseOptions);
                foreach (var evt in replay)
                    await sse.WriteEventAsync(SseEventName, evt, evt.Seq);

                await foreach (var evt in live.Reader.ReadAllAsync(http.RequestAborted))
                    await sse.WriteEventAsync(SseEventName, evt, evt.Seq);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // Cliente desconectado: al reconectar pedirá after=<último seq>.
            }
        });
    }
}
