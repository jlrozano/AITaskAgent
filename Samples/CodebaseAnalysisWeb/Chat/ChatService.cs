using CodebaseAnalysis.Core.Chat;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
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

    /// <summary>Los modelos seleccionables del chat y el de por defecto.</summary>
    public ChatModelCatalog Models => host.ChatModels;

    /// <summary>
    /// Fija el modelo de una sesión (bajo el cerrojo común, como el borrado): no se cambia mientras la sesión tiene un
    /// turno en curso, porque ese turno ya está usando el anterior. Devuelve la sesión actualizada.
    /// </summary>
    public (ChatSession? Session, ApiError? Error, int StatusCode) SetModel(string sessionId, string? modelId)
    {
        var option = host.ChatModels.Find(modelId);
        if (option == null)
            return (null, new ApiError(ApiErrorCodes.InvalidRequest, $"Modelo de chat desconocido: '{modelId}'."), StatusCodes.Status400BadRequest);

        lock (workspace.Gate)
        {
            var store = workspace.ChatStore;
            if (store == null)
                return (null, ApiErrorsFactory.NoWorkspace(), StatusCodes.Status409Conflict);

            var session = store.TryGet(sessionId, out var corrupt);
            if (session == null)
                return (null, ApiErrorsFactory.NotFound(ChatSessionStore.NotFoundMessage(sessionId, corrupt)), StatusCodes.Status404NotFound);

            if (workspace.IsChatTurnRunning(sessionId))
                return (null, ApiErrorsFactory.ChatTurnRunning(), StatusCodes.Status409Conflict);

            session.ModelId = option.Id;
            store.Save(session);
            return (session, null, StatusCodes.Status200OK);
        }
    }

    /// <summary>Ejecuta el turno reservado. Los eventos salen por <paramref name="onEvent"/> en orden.</summary>
    public Task<ChatTurn> RunTurnAsync(ChatTurnLease lease, string question, Action<ChatStreamEvent> onEvent, CancellationToken cancellationToken)
    {
        // El modelo de la sesión al empezar el turno: un cambio posterior vale para los siguientes. Si ya no está en la
        // configuración, responde el de por defecto y se avisa en el log.
        var model = host.ChatModels.Resolve(lease.Session.ModelId, message => logger.LogWarning("Sesión {SessionId}: {Message}", lease.Session.Id, message));
        var agent = new ChatAgent(serviceProvider, model.Role, host.TemplateProvider, host.ChatOptions, logger,
            knowledgeServices: new ChatKnowledgeServices { ImageDescriber = host.ImageDescriber, Embeddings = embeddings },
            modelId: model.Id);
        return agent.RunTurnAsync(lease.Store, lease.Session, question, WorkspaceWithReadRoots(lease.Workspace), onEvent, cancellationToken);
    }

    /// <summary>
    /// Las rutas de lectura adicionales de un repositorio tal como las ve la API: las de la configuración (<c>global</c>) y las
    /// del usuario, cada una con el motivo si ya no vale (la carpeta se borró, o ahora es un enlace a un sitio prohibido).
    /// </summary>
    public ChatReadRootsDto ReadRoots(ActiveWorkspace active)
    {
        var options = host.ChatOptions;
        ChatReadRootDto Describe(string path, bool global)
        {
            try
            {
                AllowedReadRoots.Validate(path, active.Knowledge, options.AllowUncPaths);
                return new ChatReadRootDto(path, global, null);
            }
            catch (ReadRootException ex)
            {
                return new ChatReadRootDto(path, global, ex.Message);
            }
        }

        return new ChatReadRootsDto([
            .. options.AllowedReadRoots.Select(r => Describe(r, true)),
            .. new ReadRootsStore(active.Knowledge, options.AllowUncPaths).List().Select(r => Describe(r, false))
        ]);
    }

    /// <summary>Guarda una ruta de lectura adicional del usuario.</summary>
    /// <exception cref="ReadRootException">La ruta no vale o ya hay demasiadas.</exception>
    public void AddReadRoot(ActiveWorkspace active, string? path) =>
        new ReadRootsStore(active.Knowledge, host.ChatOptions.AllowUncPaths).Add(path);

    public bool RemoveReadRoot(ActiveWorkspace active, string? path) =>
        new ReadRootsStore(active.Knowledge, host.ChatOptions.AllowUncPaths).Remove(path);

    /// <summary>Las rutas de lectura adicionales vigentes de un repositorio: las de la configuración y las del usuario, revalidadas.</summary>
    public IReadOnlyList<string> EffectiveReadRoots(ActiveWorkspace active) =>
        AllowedReadRoots.Revalidate(
            [.. host.ChatOptions.AllowedReadRoots, .. new ReadRootsStore(active.Knowledge, host.ChatOptions.AllowUncPaths).List()],
            active.Knowledge, host.ChatOptions.AllowUncPaths,
            (path, reason) => logger.LogWarning("Ruta de lectura adicional descartada '{Path}': {Reason}", path, reason));

    /// <summary>
    /// El workspace del turno con sus rutas de lectura adicionales, ya puestas en las carpetas permitidas de FileTools. Solo
    /// se llama con el turno reservado: no corre ningún análisis, así que esa configuración estática es del chat.
    /// </summary>
    private ChatWorkspace WorkspaceWithReadRoots(ActiveWorkspace active)
    {
        var roots = EffectiveReadRoots(active);
        AllowedReadRoots.Apply(roots);
        return active.ToChatWorkspace() with { ReadRoots = roots };
    }
}
