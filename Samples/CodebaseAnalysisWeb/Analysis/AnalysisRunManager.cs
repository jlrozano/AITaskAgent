using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.StepResults;
using AITaskAgent.FileTools;
using AITaskAgent.Observability;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysisWeb.Api;
using CodebaseAnalysisWeb.Workspace;
using Serilog.Context;
using System.Diagnostics;

namespace CodebaseAnalysisWeb.Analysis;

/// <summary>Cuerpo de <c>POST /api/analysis</c>.</summary>
public sealed record StartAnalysisRequest(bool ForceReindex = false, List<string>? Reports = null);

/// <summary>
/// Cuerpo de <c>POST /api/analysis/{runId}/regenerate</c>: los informes que se vuelven a generar (tipos o alias, como al
/// lanzar) y si se regenera también el resumen ejecutivo.
/// </summary>
public sealed record RegenerateReportsRequest(List<string>? Reports = null, bool Synthesis = true);

/// <summary>
/// Resultado de <see cref="AnalysisRunManager.Start"/> y <see cref="AnalysisRunManager.Regenerate"/>: la ejecución o el
/// error con su código HTTP.
/// </summary>
public sealed record StartAnalysisResult(AnalysisRun? Run, ApiError? Error, int StatusCode);

/// <summary>
/// Una ejecución de análisis a la vez (plan §5.1). La ejecución corre desacoplada de la petición HTTP:
/// cerrar la pestaña no la para. Se conserva la última hasta que empieza otra.
/// </summary>
public sealed class AnalysisRunManager(
    WorkspaceState workspace,
    IAnalysisWorkflow workflow,
    IConfiguration configuration,
    ILoggerFactory loggerFactory,
    TimeProvider clock,
    IHostApplicationLifetime lifetime) : IHostedService
{
    /// <summary>Propiedad de Serilog que elige el fichero de log de la ejecución (<c>CodebaseAnalysis:LogsPath</c>, por defecto <c>&lt;repo&gt;/logs</c>).</summary>
    public const string LogDirProperty = "AnalysisLogDir";

    public const string SourceName = "AnalysisRunManager";

    private readonly ILogger _logger = loggerFactory.CreateLogger("CodebaseAnalysisWeb.Analysis");
    private AnalysisRun? _last;
    private CancellationTokenRegistration _stoppingRegistration;

    /// <summary>Tope de eventos por ejecución (<c>CodebaseAnalysis:Web:MaxEventsPerRun</c>).</summary>
    private int MaxEvents => configuration.GetValue("CodebaseAnalysis:Web:MaxEventsPerRun", AnalysisEventLog.DefaultMaxEvents);

    /// <summary>La última ejecución, en curso o terminada; null si no hubo ninguna desde el arranque.</summary>
    public AnalysisRun? Last
    {
        get
        {
            lock (workspace.Gate)
            {
                return _last;
            }
        }
    }

    public bool IsRunning => Last?.IsRunning == true;

    public AnalysisRun? Find(string runId)
    {
        var last = Last;
        return last != null && last.RunId == runId ? last : null;
    }

    public StartAnalysisResult Start(StartAnalysisRequest request)
    {
        // Los nombres se validan antes de tomar el cerrojo: un nombre desconocido es un bug del cliente (400),
        // a diferencia de la CLI, que avisa y lo descarta.
        if (!TryParseReports(request.Reports, out var requested, out var invalid))
            return new StartAnalysisResult(null, invalid, StatusCodes.Status400BadRequest);

        AnalysisRun run;
        lock (workspace.Gate)
        {
            var active = workspace.Current;
            if (active == null)
                return new StartAnalysisResult(null, ApiErrorsFactory.NoWorkspace(), StatusCodes.Status409Conflict);

            if (_last is { IsRunning: true } running)
                return new StartAnalysisResult(null, ApiErrorsFactory.AnalysisRunning(running.RunId), StatusCodes.Status409Conflict);

            // El análisis sobrescribe la BD del grafo y los informes que un turno de chat está leyendo.
            if (workspace.AnyChatTurnRunning)
                return new StartAnalysisResult(null, ApiErrorsFactory.ChatTurnRunning(), StatusCodes.Status409Conflict);

            // Una escritura de conocimiento en el grafo o en la carpeta de documentos (plan v2 §5.5).
            if (workspace.AnyKnowledgeOperationRunning)
                return new StartAnalysisResult(null, ApiErrorsFactory.KnowledgeOperationRunning(), StatusCodes.Status409Conflict);

            // Mismas reglas que la CLI para BD, informes y la lista por defecto; crea las carpetas.
            var input = AnalysisRunOptions.BuildInput(configuration, active.RepositoryPath,
                warning => _logger.LogWarning("{Warning}", warning));
            input.ForceReindex = request.ForceReindex;
            if (requested.Count > 0)
                input.RequestedReports = requested;

            run = new AnalysisRun(Guid.NewGuid().ToString("N")[..12], input, active.LogsPath, clock.GetUtcNow(), MaxEvents);
            _last = run;
            run.Completion = Task.Run(() => ExecuteAsync(run));
        }

        return new StartAnalysisResult(run, null, StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// Vuelve a generar informes de la última ejecución, ya terminada, sobre su grafo (R3-R7 del diseño de regeneración): la
    /// misma ejecución pasa otra vez a <c>running</c>, emite <c>run.regeneration_started</c> y sigue con el mismo log y
    /// <c>seq</c> hasta un segundo <c>run.completed</c>. Solo sirve para la ejecución que el servidor tiene en memoria.
    /// </summary>
    public StartAnalysisResult Regenerate(string runId, RegenerateReportsRequest request)
    {
        var run = Find(runId);
        if (run == null)
            return new StartAnalysisResult(null, ApiErrorsFactory.NotFound($"No existe la ejecución '{runId}'."), StatusCodes.Status404NotFound);

        if (!TryParseReports(request.Reports, out var reports, out var invalid))
            return new StartAnalysisResult(null, invalid, StatusCodes.Status400BadRequest);
        if (reports.Count == 0)
            return new StartAnalysisResult(null, new ApiError(ApiErrorCodes.InvalidRequest, "Indica al menos un informe que regenerar."), StatusCodes.Status400BadRequest);

        lock (workspace.Gate)
        {
            // Entre el Find y el cerrojo pudo empezar otra ejecución, que sustituye a esta.
            if (!ReferenceEquals(_last, run))
                return new StartAnalysisResult(null, ApiErrorsFactory.NotFound($"No existe la ejecución '{runId}'."), StatusCodes.Status404NotFound);

            // Las mismas reglas que al lanzar: la regeneración escribe en el grafo y en los informes.
            if (run.IsRunning)
                return new StartAnalysisResult(null, ApiErrorsFactory.AnalysisRunning(run.RunId), StatusCodes.Status409Conflict);

            // Cambiar de repositorio no borra la última ejecución, y regenerarla reapuntaría el sandbox de FileTools y las
            // tools del grafo al repositorio de la ejecución: el chat del abierto leería otro (auditoría, H-7).
            var active = workspace.Current;
            if (active == null)
                return new StartAnalysisResult(null, ApiErrorsFactory.NoWorkspace(), StatusCodes.Status409Conflict);
            if (!SameRepository(run.Input.RepositoryPath, active.RepositoryPath))
                return new StartAnalysisResult(null, ApiErrorsFactory.RunFromOtherWorkspace(run.RunId, run.Input.RepositoryPath),
                    StatusCodes.Status409Conflict);

            if (workspace.AnyChatTurnRunning)
                return new StartAnalysisResult(null, ApiErrorsFactory.ChatTurnRunning(), StatusCodes.Status409Conflict);

            if (workspace.AnyKnowledgeOperationRunning)
                return new StartAnalysisResult(null, ApiErrorsFactory.KnowledgeOperationRunning(), StatusCodes.Status409Conflict);

            run.BeginRegeneration();
            // Dentro del cerrojo y antes de la tarea: es el primer evento de la regeneración, antes que los del pipeline.
            run.EventLog.Append(new AnalysisEventDto
            {
                RunId = run.RunId,
                Ts = clock.GetUtcNow(),
                Type = AnalysisEventDtoTypes.RunRegenerationStarted,
                Level = AnalysisEventLevels.Info,
                Message = $"Regenerando {string.Join(", ", reports)}{(request.Synthesis ? " y el resumen ejecutivo" : "")}.",
                Data = new Dictionary<string, object?>
                {
                    ["reports"] = reports.Select(r => r.ToString()).ToList(),
                    ["synthesis"] = request.Synthesis
                }
            });
            run.Completion = Task.Run(() => ExecuteRegenerationAsync(run, reports, request.Synthesis));
        }

        return new StartAnalysisResult(run, null, StatusCodes.Status202Accepted);
    }

    /// <summary>Misma ruta de repositorio, con la regla de <see cref="WorkspaceState.Resolve"/> y sin distinguir mayúsculas (Windows).</summary>
    internal static bool SameRepository(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Tipos de los nombres pedidos, sin repetir; false con el error 400 si alguno no es un informe del catálogo.</summary>
    private static bool TryParseReports(IEnumerable<string?>? names, out List<ReportType> reports, out ApiError? error)
    {
        reports = [];
        error = null;
        foreach (var name in names ?? [])
        {
            try
            {
                var type = AnalysisPipelineInput.ParseReportType(name ?? string.Empty);
                // Enum.TryParse acepta números: "99" daría un tipo inexistente.
                if (!Enum.IsDefined(type))
                    throw new ArgumentException($"Tipo de informe desconocido: {name}");
                if (!reports.Contains(type))
                    reports.Add(type);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
            {
                error = new ApiError(ApiErrorCodes.UnknownReport, ex.Message);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <c>failedReports</c> tras regenerar: los de antes, menos los regenerados que han escrito su informe, más los
    /// regenerados que no lo han escrito, más los pedidos en la ejecución original (<paramref name="requested"/>) que no
    /// tienen <c>report.written</c> en toda la ejecución (<paramref name="writtenInRun"/>). Lo último cubre los informes que
    /// la ejecución original no llegó a generar sin marcarlos como fallidos (cancelada o cortada antes de la síntesis). Si se
    /// canceló, los regenerados que no llegaron a escribirse solo entran por esa última regla. Ficheros, en el orden del
    /// catálogo.
    /// </summary>
    internal static IReadOnlyList<string> RegeneratedFailedReports(
        IReadOnlyList<string> previous, IReadOnlyList<ReportType> regenerated, IReadOnlySet<string> written, bool cancelled,
        IReadOnlyList<ReportType> requested, IReadOnlySet<string> writtenInRun)
    {
        var succeeded = regenerated.Where(r => written.Contains(r.ToString())).Select(AnalysisPipelineInput.GetReportFileName).ToHashSet();
        var failedNow = cancelled
            ? []
            : regenerated.Where(r => !written.Contains(r.ToString())).Select(AnalysisPipelineInput.GetReportFileName);
        var neverWritten = requested.Where(r => !writtenInRun.Contains(r.ToString())).Select(AnalysisPipelineInput.GetReportFileName);
        return previous.Where(f => !succeeded.Contains(f)).Concat(failedNow).Concat(neverWritten)
            .Distinct().Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Pide la cancelación. False si la ejecución ya no está en curso.</summary>
    public bool Cancel(AnalysisRun run)
    {
        if (!run.IsRunning)
            return false;

        run.Cancellation.Cancel();
        return true;
    }

    private async Task ExecuteAsync(AnalysisRun run)
    {
        var input = run.Input;
        var stopwatch = Stopwatch.StartNew();
        IStepResult? result = null;
        Exception? failure = null;
        AnalysisPipelineFinalResult? final = null;

        // Valores de una ejecución que termina por una excepción no prevista: el finally los usa para que
        // la ejecución nunca quede en "running".
        var status = RunStatuses.Failed;
        string? error = null;
        IReadOnlyList<string> failedReports = [];

        // La propiedad viaja por ExecutionContext a todo lo que corre dentro: ramas paralelas y servicios LLM.
        using (LogContext.PushProperty(LogDirProperty, run.LogsPath))
        {
            try
            {
                run.EventLog.Append(new AnalysisEventDto
                {
                    RunId = run.RunId,
                    Ts = clock.GetUtcNow(),
                    Type = AnalysisEventDtoTypes.RunStarted,
                    Level = AnalysisEventLevels.Info,
                    Message = $"Análisis iniciado en {input.RepositoryPath}",
                    Data = new Dictionary<string, object?>
                    {
                        ["repositoryPath"] = input.RepositoryPath,
                        ["databasePath"] = input.DatabasePath,
                        ["reportsPath"] = input.OutputReportsPath,
                        ["forceReindex"] = input.ForceReindex,
                        ["requestedReports"] = input.RequestedReports.Select(r => r.ToString()).ToList()
                    }
                });

                _logger.LogInformation("Análisis {RunId} iniciado en {Repository} ({Count} informes, forceReindex={ForceReindex})",
                    run.RunId, input.RepositoryPath, input.RequestedReports.Count, input.ForceReindex);

                (result, failure) = await RunWorkflowAsync(run, (channel, reporter, ct) => workflow.RunAsync(input, channel, reporter, ct));

                // Regla de la CLI (Program.cs): la cancelación se decide por el token, no por el tipo de excepción,
                // porque RetryMiddleware convierte las excepciones en resultados de error.
                final = result as AnalysisPipelineFinalResult;
                failedReports = final?.FailedReports.ToList() ?? [];
                if (run.Cancellation.IsCancellationRequested)
                {
                    status = RunStatuses.Cancelled;
                    error = "Análisis cancelado.";
                }
                else if (final != null && !final.HasError && final.Success)
                {
                    status = final.FailedReports.Count > 0 ? RunStatuses.CompletedWithFailures : RunStatuses.Succeeded;
                }
                else
                {
                    status = RunStatuses.Failed;
                    error = failure?.Message ?? result?.Error?.Message ?? $"Resultado inesperado: {result?.GetType().Name ?? "null"}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "El análisis {RunId} falló fuera del pipeline", run.RunId);
                status = run.Cancellation.IsCancellationRequested ? RunStatuses.Cancelled : RunStatuses.Failed;
                error = status == RunStatuses.Cancelled ? "Análisis cancelado." : ex.Message;
            }
            finally
            {
                stopwatch.Stop();
                CompleteRun(run, status, failedReports, error, final?.ExecutiveReportPath, stopwatch.ElapsedMilliseconds);
            }
        }
    }

    /// <summary>
    /// La regeneración de <see cref="Regenerate"/>: como <see cref="ExecuteAsync"/>, pero con el pipeline reducido y el
    /// estado recalculado sobre el de la ejecución (<see cref="RegeneratedFailedReports"/>). Un informe cuenta como
    /// regenerado cuando llega su <c>report.written</c>, que es lo que ve también el cliente.
    /// </summary>
    private async Task ExecuteRegenerationAsync(AnalysisRun run, IReadOnlyList<ReportType> reports, bool synthesis)
    {
        var stopwatch = Stopwatch.StartNew();
        var previousFailed = run.FailedReports;
        var written = new HashSet<string>(StringComparer.Ordinal);
        AnalysisPipelineFinalResult? final = null;

        var status = RunStatuses.Failed;
        string? error = null;
        var failedReports = previousFailed;

        using (LogContext.PushProperty(LogDirProperty, run.LogsPath))
        {
            try
            {
                _logger.LogInformation("Análisis {RunId}: regenerando {Reports} (síntesis={Synthesis})",
                    run.RunId, string.Join(", ", reports), synthesis);

                var (result, failure) = await RunWorkflowAsync(run,
                    (channel, reporter, ct) => workflow.RegenerateAsync(run.Input, reports, synthesis, channel, reporter, ct),
                    dto =>
                    {
                        if (dto.Type == AnalysisEventDtoTypes.ReportWritten && dto.Report != null)
                            written.Add(dto.Report);
                    });

                final = result as AnalysisPipelineFinalResult;
                var cancelled = run.Cancellation.IsCancellationRequested;
                failedReports = RegeneratedFailedReports(previousFailed, reports, written, cancelled, run.Input.RequestedReports, run.WrittenReports);
                if (cancelled)
                {
                    status = RunStatuses.Cancelled;
                    error = "Regeneración cancelada.";
                }
                // Sin síntesis, el pipeline acaba en el paralelo, que conserva el error solo si fallan todos sus auditores:
                // sigue siendo una regeneración terminada, con esos informes en failedReports.
                else if (failure == null && (final is { HasError: false, Success: true } || result is ParallelResult))
                {
                    status = failedReports.Count > 0 ? RunStatuses.CompletedWithFailures : RunStatuses.Succeeded;
                }
                else
                {
                    status = RunStatuses.Failed;
                    error = failure?.Message ?? result?.Error?.Message ?? $"Resultado inesperado: {result?.GetType().Name ?? "null"}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "La regeneración de {RunId} falló fuera del pipeline", run.RunId);
                status = run.Cancellation.IsCancellationRequested ? RunStatuses.Cancelled : RunStatuses.Failed;
                error = status == RunStatuses.Cancelled ? "Regeneración cancelada." : ex.Message;
            }
            finally
            {
                stopwatch.Stop();
                CompleteRun(run, status, failedReports, error, final?.ExecutiveReportPath, stopwatch.ElapsedMilliseconds, regeneration: true);
            }
        }
    }

    /// <summary>
    /// Ejecuta el workflow con un canal propio cuyos eventos pasan por el mapper al log de la ejecución (y a
    /// <paramref name="observe"/>). Devuelve el resultado o la excepción; la cancelación la decide el llamante por el token.
    /// </summary>
    private async Task<(IStepResult? Result, Exception? Failure)> RunWorkflowAsync(
        AnalysisRun run,
        Func<IEventChannel, IAnalysisReporter, CancellationToken, Task<IStepResult>> execute,
        Action<AnalysisEventDto>? observe = null)
    {
        var channel = new EventChannel(LogLevel.None, loggerFactory.CreateLogger<EventChannel>());
        var mapper = new AnalysisEventMapper(run.RunId);
        using var subscription = channel.Subscribe((evt, _) =>
        {
            var dto = mapper.Map(evt);
            if (dto != null)
            {
                run.EventLog.Append(dto);
                if (dto.Type == AnalysisEventDtoTypes.ReportWritten && dto.Report != null)
                    run.MarkReportWritten(dto.Report);
                observe?.Invoke(dto);
            }
            return Task.CompletedTask;
        });

        try
        {
            // Igual que la CLI: las tools se registran antes de construir el pipeline.
            AnalysisToolRegistration.Register(run.Input);
            FileToolsConfiguration.ClearAdditionalAllowedDirectories();

            return (await execute(channel, new LoggerAnalysisReporter(_logger), run.Cancellation.Token), null);
        }
        catch (Exception ex)
        {
            if (!run.Cancellation.IsCancellationRequested)
                _logger.LogError(ex, "El análisis {RunId} terminó con una excepción", run.RunId);
            return (null, ex);
        }
        finally
        {
            // El pipeline ya lo cierra (no es el canal del contenedor); cerrarlo aquí también cubre una
            // ejecución que falla antes y garantiza que todo evento ha pasado por el suscriptor.
            await channel.DisposeAsync();
        }
    }

    /// <summary>
    /// Cierra la ejecución en este orden: estado final, <c>run.completed</c> y cierre del log.Un cliente que
    /// recibe <c>run.completed</c> y pide <c>/api/analysis/current</c> ya no la ve en curso, y ni un fallo al
    /// añadir el evento deja la ejecución en <c>running</c> ni el SSE abierto.
    /// </summary>
    private void CompleteRun(AnalysisRun run, string status, IReadOnlyList<string> failedReports, string? error, string? executiveReportPath, long durationMs,
        bool regeneration = false)
    {
        try
        {
            run.Finish(status, failedReports, error, clock.GetUtcNow());

            var exitCode = status == RunStatuses.Succeeded ? 0 : 1;
            run.EventLog.Append(new AnalysisEventDto
            {
                RunId = run.RunId,
                Ts = clock.GetUtcNow(),
                Type = AnalysisEventDtoTypes.RunCompleted,
                Level = status switch
                {
                    RunStatuses.Succeeded => AnalysisEventLevels.Success,
                    RunStatuses.CompletedWithFailures or RunStatuses.Cancelled => AnalysisEventLevels.Warning,
                    _ => AnalysisEventLevels.Error
                },
                Message = (status, regeneration) switch
                {
                    (RunStatuses.Succeeded, false) => "Análisis completado.",
                    (RunStatuses.CompletedWithFailures, false) => $"Análisis completado con {failedReports.Count} informe(s) sin generar.",
                    (RunStatuses.Cancelled, false) => "Análisis cancelado.",
                    (_, false) => $"Análisis terminado con errores: {error}",
                    (RunStatuses.Succeeded, true) => "Regeneración completada.",
                    (RunStatuses.CompletedWithFailures, true) => $"Regeneración completada; quedan {failedReports.Count} informe(s) sin generar.",
                    (RunStatuses.Cancelled, true) => "Regeneración cancelada.",
                    (_, true) => $"Regeneración terminada con errores: {error}"
                },
                Data = new Dictionary<string, object?>
                {
                    ["status"] = status,
                    ["exitCode"] = exitCode,
                    ["durationMs"] = durationMs,
                    ["failedReports"] = failedReports,
                    ["executiveReportPath"] = string.IsNullOrEmpty(executiveReportPath) ? null : executiveReportPath,
                    ["error"] = error
                }
            });

            _logger.LogInformation("Análisis {RunId} terminado: {Status} en {Seconds:F1} s", run.RunId, status, durationMs / 1000.0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo registrar el final del análisis {RunId}", run.RunId);
            if (run.IsRunning)
                run.Finish(RunStatuses.Failed, failedReports, ex.Message, clock.GetUtcNow());
        }
        finally
        {
            run.EventLog.Complete();
        }
    }

    /// <summary>
    /// La cancelación se engancha a <see cref="IHostApplicationLifetime.ApplicationStopping"/>, que llega antes
    /// de que el servidor espere a las peticiones abiertas: al cancelar, el pipeline mata el indexador (cambio
    /// de F1), la ejecución emite <c>run.completed</c> y los SSE del análisis se cierran solos. En
    /// <see cref="StopAsync"/>, que llega después, el servidor ya habría esperado a esos SSE hasta agotar el
    /// plazo de apagado.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingRegistration = lifetime.ApplicationStopping.Register(CancelRunning);
        return Task.CompletedTask;
    }

    /// <summary>Cancela la ejecución en curso, si la hay.</summary>
    public void CancelRunning()
    {
        if (Last is { IsRunning: true } running)
        {
            _logger.LogInformation("El servidor se detiene: se cancela el análisis {RunId}", running.RunId);
            running.Cancellation.Cancel();
        }
    }

    /// <summary>Por si la parada no pasó por ApplicationStopping: cancela y espera mientras el host lo permita.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stoppingRegistration.DisposeAsync();

        var last = Last;
        if (last is not { IsRunning: true })
            return;

        last.Cancellation.Cancel();
        try
        {
            await last.Completion.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
