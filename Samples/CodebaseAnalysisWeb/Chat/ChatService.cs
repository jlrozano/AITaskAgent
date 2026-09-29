using CodebaseAnalysis.Core.Chat;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysisWeb.Analysis;
using CodebaseAnalysisWeb.Api;
using CodebaseAnalysisWeb.Hosting;
using CodebaseAnalysisWeb.Workspace;

namespace CodebaseAnalysisWeb.Chat;

/// <summary>Un turno reservado: la sesión, el almacén y el workspace en que corre; al liberarlo se suelta la sesión.</summary>
public sealed class ChatTurnLease(ChatSession session, ChatSessionStore store, ActiveWorkspace workspace, Action release) : IDisposable
{
    private int _released;

    public ChatSession Session { get; } = session;
    public ChatSessionStore Store { get; } = store;
    public ActiveWorkspace Workspace { get; } = workspace;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            release();
    }
}

/// <summary>
/// Sesiones y turnos del chat del workspace activo. Un turno a la vez por sesión, y ninguno mientras corre
/// un análisis (plan D3): el análisis sobrescribe la BD del grafo y mueve los informes. Los turnos en curso
/// se apuntan en <see cref="WorkspaceState"/>, bajo el mismo cerrojo que el arranque del análisis.
/// </summary>
public sealed class ChatService(
    WorkspaceState workspace,
    AnalysisRunManager analysis,
    AnalysisHost host,
    CodebaseAnalysis.Core.Knowledge.Embeddings.EmbeddingsRuntime embeddings,
    IServiceProvider serviceProvider,
    ILogger<ChatService> logger)
{
    /// <summary>Longitud máxima de una pregunta.</summary>
    public const int MaxMessageLength = 8000;

    /// <summary>True si hay algún turno en curso, en cualquier sesión.</summary>
    public bool AnyTurnRunning => workspace.AnyChatTurnRunning;

    public bool IsTurnRunning(string sessionId) => workspace.IsChatTurnRunning(sessionId);

    /// <summary>
    /// Reserva un turno: comprueba (bajo el cerrojo común) que hay workspace, que no corre un análisis, que
    /// la sesión existe y que no tiene otro turno en curso.
    /// </summary>
    public (ChatTurnLease? Lease, ApiError? Error, int StatusCode) TryBeginTurn(string sessionId)
    {
        lock (workspace.Gate)
        {
            var active = workspace.Current;
            var store = workspace.ChatStore;
            if (active == null || store == null)
                return (null, ApiErrorsFactory.NoWorkspace(), StatusCodes.Status409Conflict);

            if (analysis.IsRunning)
                return (null, ApiErrorsFactory.AnalysisRunning(analysis.Last?.RunId), StatusCodes.Status409Conflict);

            if (workspace.AnyKnowledgeOperationRunning)
                return (null, ApiErrorsFactory.KnowledgeOperationRunning(), StatusCodes.Status409Conflict);

            var session = store.TryGet(sessionId, out var corrupt);
            if (session == null)
                return (null, ApiErrorsFactory.NotFound(ChatSessionStore.NotFoundMessage(sessionId, corrupt)), StatusCodes.Status404NotFound);

            if (!workspace.TryReserveChatTurn(sessionId))
                return (null, ApiErrorsFactory.ChatTurnRunning(), StatusCodes.Status409Conflict);

            return (new ChatTurnLease(session, store, active, () => workspace.ReleaseChatTurn(sessionId)), null, StatusCodes.Status200OK);
        }
    }

    /// <summary>Ejecuta el turno reservado. Los eventos salen por <paramref name="onEvent"/> en orden.</summary>
    public Task<ChatTurn> RunTurnAsync(ChatTurnLease lease, string question, Action<ChatStreamEvent> onEvent, CancellationToken cancellationToken)
    {
        var agent = new ChatAgent(serviceProvider, host.ChatRole, host.TemplateProvider, host.ChatOptions, logger,
            knowledgeServices: new ChatKnowledgeServices { ImageDescriber = host.ImageDescriber, Embeddings = embeddings });
        return agent.RunTurnAsync(lease.Store, lease.Session, question, lease.Workspace.ToChatWorkspace(), onEvent, cancellationToken);
    }
}
