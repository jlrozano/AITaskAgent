using CodebaseAnalysis.Core.Chat;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysisWeb.Chat;
using CodebaseAnalysisWeb.Sse;
using CodebaseAnalysisWeb.Workspace;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace CodebaseAnalysisWeb.Api;

public sealed record CreateChatSessionRequest(string? Title = null, string? ModelId = null);

public sealed record SetChatModelRequest(string? ModelId);

/// <summary>Un modelo seleccionable del chat; los precios salen solo si el proveedor los declara.</summary>
public sealed record ChatModelDto(
    string Id,
    string Label,
    string Provider,
    string Model,
    bool Streaming,
    decimal? InputPricePerMillion,
    decimal? OutputPricePerMillion);

public sealed record ChatModelsDto(string DefaultId, IReadOnlyList<ChatModelDto> Models);

public sealed record ChatMessageRequest(string? Message);

public sealed record AddChatReadRootRequest(string? Path);

/// <summary>Ruta de solo lectura adicional del chat. <c>global</c>: viene de la configuración y no se quita aquí. <c>problem</c>: por qué ya no vale, o null.</summary>
public sealed record ChatReadRootDto(string Path, bool Global, string? Problem);

public sealed record ChatReadRootsDto(IReadOnlyList<ChatReadRootDto> Roots);

public sealed record ChatSessionSummaryDto(string Id, string? Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int TurnCount);

public static class ChatEndpoints
{
    /// <summary>Nombre del evento SSE del chat: el tipo va en <c>type</c> (plan §4.4).</summary>
    public const string SseEventName = "chat";

    public static void MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        // Sin workspace no hay carpeta de sesiones: el listado queda vacío (el contrato solo prevé 200).
        app.MapGet("/api/chat/sessions", (WorkspaceState workspace) =>
            Results.Ok((workspace.ChatStore?.List() ?? [])
                .Select(s => new ChatSessionSummaryDto(s.Id, s.Title, s.CreatedAt, s.UpdatedAt, s.TurnCount))
                .ToList()));

        // Rutas de solo lectura, además del repositorio, que las tools de ficheros del chat pueden recorrer. Solo se guarda la
        // ruta: no se indexa, se extrae ni se copia nada, y nunca se escribe en ella. Añadir y quitar es instantáneo.
        app.MapGet("/api/chat/read-roots", (WorkspaceState workspace, ChatService chat) =>
            workspace.Current is { } active
                ? Results.Ok(chat.ReadRoots(active))
                : ApiErrorsFactory.NoWorkspace().ToResult(StatusCodes.Status409Conflict));

        app.MapPost("/api/chat/read-roots", (AddChatReadRootRequest body, WorkspaceState workspace, ChatService chat) =>
        {
            if (workspace.Current is not { } active)
                return ApiErrorsFactory.NoWorkspace().ToResult(StatusCodes.Status409Conflict);
            try
            {
                chat.AddReadRoot(active, body.Path);
            }
            catch (ReadRootException ex)
            {
                return new ApiError(ApiErrorCodes.InvalidPath, ex.Message).ToResult(StatusCodes.Status400BadRequest);
            }
            return Results.Created("/api/chat/read-roots", chat.ReadRoots(active));
        });

        // La ruta va en la query (`?path=`): un DELETE no lleva cuerpo.
        app.MapDelete("/api/chat/read-roots", (string? path, WorkspaceState workspace, ChatService chat) =>
        {
            if (workspace.Current is not { } active)
                return ApiErrorsFactory.NoWorkspace().ToResult(StatusCodes.Status409Conflict);
            return chat.RemoveReadRoot(active, path)
                ? Results.NoContent()
                : ApiErrorsFactory.NotFound($"'{path}' no está en las rutas de lectura adicionales.").ToResult(StatusCodes.Status404NotFound);
        });

        app.MapGet("/api/chat/models", (ChatService chat) =>
            Results.Ok(new ChatModelsDto(
                chat.Models.Default.Id,
                chat.Models.Models
                    .Select(m => new ChatModelDto(m.Id, m.Label, m.ProviderName, m.Model, m.Streaming, m.InputPricePerMillion, m.OutputPricePerMillion))
                    .ToList())));

        // El modelo cambia a mitad de conversación y vale para los turnos siguientes; la respuesta es la sesión.
        app.MapPut("/api/chat/sessions/{id}/model", (string id, SetChatModelRequest body, ChatService chat) =>
        {
            var (session, error, status) = chat.SetModel(id, body.ModelId);
            return session != null ? Results.Ok(session) : error!.ToResult(status);
        });

        app.MapPost("/api/chat/sessions", (CreateChatSessionRequest body, WorkspaceState workspace, ChatService chat) =>
        {
            if (!string.IsNullOrWhiteSpace(body.ModelId) && chat.Models.Find(body.ModelId) == null)
                return new ApiError(ApiErrorCodes.InvalidRequest, $"Modelo de chat desconocido: '{body.ModelId}'.").ToResult(StatusCodes.Status400BadRequest);

            var active = workspace.Current;
            var store = workspace.ChatStore;
            if (active == null || store == null)
                return ApiErrorsFactory.NoWorkspace().ToResult(StatusCodes.Status409Conflict);

            var session = store.Create(active.RepositoryPath, body.Title, chat.Models.Find(body.ModelId)?.Id ?? chat.Models.Default.Id);
            return Results.Created($"/api/chat/sessions/{session.Id}", session);
        });

        // Un fichero dañado es 404, igual que en el listado, que lo omite; el mensaje lo dice.
        app.MapGet("/api/chat/sessions/{id}", (string id, WorkspaceState workspace) =>
        {
            ChatSession? session = null;
            var corrupt = false;
            if (workspace.ChatStore is { } store)
                session = store.TryGet(id, out corrupt);

            return session == null
                ? ApiErrorsFactory.NotFound(ChatSessionStore.NotFoundMessage(id, corrupt)).ToResult(StatusCodes.Status404NotFound)
                : Results.Ok(session);
        });

        // Comprobar y borrar bajo el cerrojo común: TryBeginTurn reserva el turno bajo el mismo cerrojo, así
        // que no se puede borrar una sesión entre la comprobación y el inicio de un turno. Un fichero dañado
        // se puede borrar.
        app.MapDelete("/api/chat/sessions/{id}", (string id, WorkspaceState workspace) =>
        {
            lock (workspace.Gate)
            {
                var store = workspace.ChatStore;
                if (store == null || !store.Exists(id))
                    return ApiErrorsFactory.NotFound($"No existe la sesión '{id}'.").ToResult(StatusCodes.Status404NotFound);

                if (workspace.IsChatTurnRunning(id))
                    return ApiErrorsFactory.ChatTurnRunning().ToResult(StatusCodes.Status409Conflict);

                store.Delete(id);
                return Results.NoContent();
            }
        });

        app.MapPost("/api/chat/sessions/{id}/messages", async (
            string id,
            ChatMessageRequest body,
            HttpContext http,
            ChatService chat,
            IOptions<JsonOptions> json,
            SseOptions sseOptions,
            IHostApplicationLifetime lifetime,
            ILoggerFactory loggerFactory) =>
        {
            var question = body.Message?.Trim() ?? string.Empty;
            if (question.Length == 0)
            {
                await new ApiError(ApiErrorCodes.EmptyMessage, "La pregunta está vacía.")
                    .ToResult(StatusCodes.Status400BadRequest).ExecuteAsync(http);
                return;
            }
            if (question.Length > ChatService.MaxMessageLength)
            {
                await new ApiError(ApiErrorCodes.MessageTooLong, $"La pregunta supera los {ChatService.MaxMessageLength} caracteres.")
                    .ToResult(StatusCodes.Status400BadRequest).ExecuteAsync(http);
                return;
            }

            var (lease, error, status) = chat.TryBeginTurn(id);
            if (lease == null)
            {
                await error!.ToResult(status).ExecuteAsync(http);
                return;
            }

            using (lease)
            {
                await StreamTurnAsync(http, chat, lease, question, json.Value, sseOptions, lifetime.ApplicationStopping,
                    loggerFactory.CreateLogger("CodebaseAnalysisWeb.Chat"));
            }
        });
    }

    /// <summary>
    /// El turno corre con el token de la petición: si el cliente aborta el fetch (botón Parar, pestaña
    /// cerrada), se cancela y queda persistido como cancelado. También se cancela al parar el servidor, que
    /// si no esperaría al turno hasta agotar el plazo de apagado. Los eventos pasan por una cola sin límite
    /// para que un cliente lento no haga perder deltas en el buffer del canal de eventos.
    /// </summary>
    private static async Task StreamTurnAsync(
        HttpContext http,
        ChatService chat,
        ChatTurnLease lease,
        string question,
        JsonOptions json,
        SseOptions sseOptions,
        CancellationToken stopping,
        ILogger logger)
    {
        var queue = Channel.CreateUnbounded<ChatStreamEvent>(new UnboundedChannelOptions { SingleReader = true });
        using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, stopping);
        var aborted = turnCancellation.Token;

        var turnTask = Task.Run(async () =>
        {
            try
            {
                await chat.RunTurnAsync(lease, question, e => queue.Writer.TryWrite(e), aborted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "El turno de chat de la sesión {SessionId} falló", lease.Session.Id);
                queue.Writer.TryWrite(new ChatStreamEvent(ChatStreamEventTypes.TurnFailed, new Dictionary<string, object?>
                {
                    ["turnId"] = null,
                    ["status"] = ChatTurnStatus.Failed,
                    ["error"] = ex.Message
                }));
            }
            finally
            {
                queue.Writer.TryComplete();
            }
        }, CancellationToken.None);

        try
        {
            await using var sse = await SseWriter.StartAsync(http, json.SerializerOptions, sseOptions);
            await foreach (var evt in queue.Reader.ReadAllAsync(CancellationToken.None))
            {
                await sse.WriteEventAsync(SseEventName, evt.ToPayload());
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // Cliente desconectado: el turno ve el mismo token cancelado y se guarda como cancelado.
        }
        finally
        {
            // La sesión no se libera hasta que el turno está persistido.
            await turnTask;
        }
    }
}
