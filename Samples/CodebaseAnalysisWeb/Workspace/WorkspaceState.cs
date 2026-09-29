using AITaskAgent.FileTools;
using CodebaseAnalysis.Core.Chat;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Models;

namespace CodebaseAnalysisWeb.Workspace;

/// <summary>El repositorio activo y sus rutas, resueltas con las mismas reglas que la CLI.</summary>
public sealed record ActiveWorkspace(
    string RepositoryPath,
    string DatabasePath,
    string ReportsPath,
    string ChatPath,
    string LogsPath)
{
    /// <summary>Rutas de V2 (documentos, informes propios, historial, conocimiento), plan v2 §4.1.</summary>
    public KnowledgePaths Knowledge { get; } = new(RepositoryPath, ReportsPath);

    public ChatWorkspace ToChatWorkspace() => new(RepositoryPath, DatabasePath, ReportsPath);
}

/// <summary>El repositorio no admite escritura: no se puede crear ni escribir en la carpeta del chat.</summary>
public sealed class WorkspaceNotWritableException(string path, Exception inner)
    : Exception($"No se puede escribir en '{path}': {inner.Message}", inner)
{
    public string Path { get; } = path;
}

/// <summary>
/// Único repositorio activo del proceso (plan D2): <c>FileToolsConfiguration</c> y el registro de tools son
/// estáticos. <see cref="Gate"/> es el cerrojo que comparten el workspace, el análisis y el chat para las
/// comprobaciones de «¿hay algo en curso?» y el cambio de estado que las sigue. Aquí viven también los
/// turnos de chat en curso, para que el análisis los vea sin depender del servicio del chat.
/// </summary>
public sealed class WorkspaceState(IConfiguration configuration, WorkspaceSettingsStore settings, ILogger<WorkspaceState> logger)
{
    private readonly HashSet<string> _runningTurns = new(StringComparer.Ordinal);
    private int _knowledgeOperations;
    private ActiveWorkspace? _current;
    private ChatSessionStore? _chatStore;

    /// <summary>Cerrojo común de workspace, análisis y chat.</summary>
    public Lock Gate { get; } = new();

    public ActiveWorkspace? Current
    {
        get
        {
            lock (Gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Almacén de sesiones del workspace activo; null si no hay.</summary>
    public ChatSessionStore? ChatStore
    {
        get
        {
            lock (Gate)
            {
                return _chatStore;
            }
        }
    }

    /// <summary>True si hay algún turno de chat en curso, en cualquier sesión.</summary>
    public bool AnyChatTurnRunning
    {
        get
        {
            lock (Gate)
            {
                return _runningTurns.Count > 0;
            }
        }
    }

    public bool IsChatTurnRunning(string sessionId)
    {
        lock (Gate)
        {
            return _runningTurns.Contains(sessionId);
        }
    }

    /// <summary>Reserva la sesión para un turno. Llamar con <see cref="Gate"/> tomado, tras las demás comprobaciones.</summary>
    public bool TryReserveChatTurn(string sessionId)
    {
        lock (Gate)
        {
            return _runningTurns.Add(sessionId);
        }
    }

    public void ReleaseChatTurn(string sessionId)
    {
        lock (Gate)
        {
            _runningTurns.Remove(sessionId);
        }
    }

    /// <summary>
    /// True mientras dura alguna operación de conocimiento (plan v2 §5.5): escribir en el grafo, indexar o quitar
    /// embeddings, subir o describir un documento. Con una en curso, lanzar un análisis, enviar una pregunta y
    /// cambiar de workspace responden 409 <c>knowledge_operation_running</c>.
    /// </summary>
    public bool AnyKnowledgeOperationRunning
    {
        get
        {
            lock (Gate)
            {
                return _knowledgeOperations > 0;
            }
        }
    }

    /// <summary>
    /// Apunta una operación de conocimiento. Llamar con <see cref="Gate"/> tomado, después de las comprobaciones de la
    /// matriz de §5.5 que correspondan (análisis, turno, otra operación). El <see cref="IDisposable"/> la retira; se
    /// puede liberar más de una vez sin efecto.
    /// </summary>
    public IDisposable BeginKnowledgeOperation()
    {
        lock (Gate)
        {
            _knowledgeOperations++;
        }
        return new KnowledgeOperationLease(this);
    }

    private void EndKnowledgeOperation()
    {
        lock (Gate)
        {
            _knowledgeOperations--;
        }
    }

    private sealed class KnowledgeOperationLease(WorkspaceState owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.EndKnowledgeOperation();
        }
    }

    /// <summary>
    /// Rutas de un repositorio: BD e informes con <c>CodebaseAnalysis:DatabasePath</c> y
    /// <c>OutputReportsPath</c> relativos a él (igual que la CLI); el chat, en <c>&lt;carpeta de la BD&gt;/chat</c>.
    /// </summary>
    public ActiveWorkspace Resolve(string repositoryPath)
    {
        var repo = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var db = AnalysisRunOptions.ResolveDatabasePath(configuration, repo);
        var reports = AnalysisRunOptions.ResolveReportsPath(configuration, repo);
        var chat = Path.Combine(Path.GetDirectoryName(db) ?? repo, "chat");
        return new ActiveWorkspace(repo, db, reports, chat, Path.Combine(repo, "logs"));
    }

    /// <summary>
    /// Activa un repositorio. Llamar con <see cref="Gate"/> tomado, tras comprobar que no corre nada. Crea la
    /// carpeta del chat y comprueba que se puede escribir en ella antes de tocar ningún estado, registra las
    /// tools (igual que la CLI), deja el sandbox de FileTools solo en este repositorio y lo recuerda para el
    /// próximo arranque.
    /// </summary>
    /// <exception cref="WorkspaceNotWritableException">No se puede escribir en la carpeta del chat.</exception>
    public ActiveWorkspace Activate(string repositoryPath)
    {
        var workspace = Resolve(repositoryPath);
        EnsureWritable(workspace.ChatPath);

        // Con la codificación configurada, para que el visor y las tools lean igual que el análisis (H-8).
        var paths = ToPipelinePaths(workspace);
        paths.SourceDefaultEncoding = configuration[$"{JavaAnalyzerOptions.SectionName}:{nameof(JavaAnalyzerOptions.DefaultEncoding)}"] ?? string.Empty;
        AnalysisToolRegistration.Register(paths);

        // Register fija RootDirectory con el setter, que no conserva la raíz anterior; aun así se vacían
        // las carpetas adicionales para que el sandbox no arrastre nada de un repositorio previo (plan R2).
        FileToolsConfiguration.ClearAdditionalAllowedDirectories();

        _current = workspace;
        _chatStore = new ChatSessionStore(workspace.ChatPath);
        settings.Save(new WorkspaceSettings(workspace.RepositoryPath));
        return workspace;
    }

    /// <summary>
    /// Reabre el último repositorio guardado si la ruta sigue existiendo. Si no hay ninguno, ya no existe o
    /// falla al abrirse, la web arranca sin workspace y lo deja en el log.
    /// </summary>
    public ActiveWorkspace? TryRestoreLast()
    {
        var last = settings.Load().LastRepositoryPath;
        if (string.IsNullOrWhiteSpace(last))
            return null;

        if (!Path.IsPathFullyQualified(last) || !Directory.Exists(last))
        {
            logger.LogWarning("El último repositorio abierto ya no existe ({Path}): la web arranca sin workspace.", last);
            return null;
        }

        try
        {
            lock (Gate)
            {
                var active = Activate(last);
                logger.LogInformation("Reabierto el último repositorio: {Path}", active.RepositoryPath);
                return active;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo reabrir el último repositorio ({Path}): la web arranca sin workspace.", last);
            return null;
        }
    }

    /// <summary>Crea la carpeta y escribe y borra un fichero de prueba: que exista no demuestra que se pueda escribir.</summary>
    private static void EnsureWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WorkspaceNotWritableException(directory, ex);
        }
    }

    /// <summary>Entrada mínima para registrar las tools: solo se usan las rutas.</summary>
    public static AnalysisPipelineInput ToPipelinePaths(ActiveWorkspace workspace) => new()
    {
        RepositoryPath = workspace.RepositoryPath,
        DatabasePath = workspace.DatabasePath,
        OutputReportsPath = workspace.ReportsPath
    };
}
