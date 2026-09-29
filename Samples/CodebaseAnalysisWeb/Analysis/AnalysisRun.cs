using CodebaseAnalysis.Core.Models;

namespace CodebaseAnalysisWeb.Analysis;

/// <summary>Valores de <see cref="RunStatusDto.Status"/> (plan §4.3).</summary>
public static class RunStatuses
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string CompletedWithFailures = "completed_with_failures";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// Una ejecución de análisis: la última se conserva hasta que empieza otra. Una ejecución terminada puede volver a
/// <c>running</c> para regenerar informes (<see cref="BeginRegeneration"/>), con el mismo <c>runId</c> y el mismo log.
/// </summary>
public sealed class AnalysisRun
{
    private readonly Lock _lock = new();
    private string _status = RunStatuses.Running;
    private DateTimeOffset? _finishedAt;
    private IReadOnlyList<string> _failedReports = [];
    private readonly HashSet<string> _writtenReports = new(StringComparer.Ordinal);
    private string? _error;
    private CancellationTokenSource _cancellation = new();

    public AnalysisRun(string runId, AnalysisPipelineInput input, string logsPath, DateTimeOffset startedAt, int maxEvents)
    {
        RunId = runId;
        Input = input;
        LogsPath = logsPath;
        StartedAt = startedAt;
        EventLog = new AnalysisEventLog(runId, maxEvents);
    }

    public string RunId { get; }
    public AnalysisPipelineInput Input { get; }
    public string LogsPath { get; }
    public DateTimeOffset StartedAt { get; }
    public AnalysisEventLog EventLog { get; }
    /// <summary>Cancelación de la ejecución en curso; la regeneración estrena una, porque la anterior puede estar cancelada.</summary>
    public CancellationTokenSource Cancellation => Volatile.Read(ref _cancellation);

    /// <summary>La tarea de fondo de la ejecución (para esperarla al parar el servidor y en los tests).</summary>
    public Task Completion { get; internal set; } = Task.CompletedTask;

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _status == RunStatuses.Running;
            }
        }
    }

    /// <summary>Ficheros de los informes fallidos según el último <c>run.completed</c>.</summary>
    public IReadOnlyList<string> FailedReports
    {
        get
        {
            lock (_lock)
            {
                return _failedReports;
            }
        }
    }

    /// <summary>
    /// Tipos de los informes con <c>report.written</c> en toda la ejecución, la original y sus regeneraciones. Sin el resumen
    /// ejecutivo, que no tiene tipo.
    /// </summary>
    public IReadOnlySet<string> WrittenReports
    {
        get
        {
            lock (_lock)
            {
                return _writtenReports.ToHashSet(StringComparer.Ordinal);
            }
        }
    }

    public void MarkReportWritten(string reportType)
    {
        lock (_lock)
        {
            _writtenReports.Add(reportType);
        }
    }

    /// <summary>
    /// Vuelve a poner en curso la ejecución terminada para regenerar informes: reabre el log antes de cambiar el estado
    /// (quien vea <c>running</c> y se suscriba recibe los eventos nuevos) y estrena la cancelación. Conserva
    /// <see cref="FailedReports"/>, que la regeneración recalcula al terminar. False si la ejecución sigue en curso.
    /// </summary>
    public bool BeginRegeneration()
    {
        lock (_lock)
        {
            if (_status == RunStatuses.Running)
                return false;

            EventLog.Reopen();
            Volatile.Write(ref _cancellation, new CancellationTokenSource());
            _status = RunStatuses.Running;
            _error = null;
            _finishedAt = null;
            return true;
        }
    }

    public void Finish(string status, IReadOnlyList<string> failedReports, string? error, DateTimeOffset finishedAt)
    {
        lock (_lock)
        {
            _status = status;
            _failedReports = failedReports;
            _error = error;
            _finishedAt = finishedAt;
        }
    }

    public RunStatusDto ToDto()
    {
        lock (_lock)
        {
            return new RunStatusDto(
                RunId,
                Input.RepositoryPath,
                _status,
                StartedAt,
                _finishedAt,
                Input.ForceReindex,
                Input.RequestedReports.Select(r => r.ToString()).ToList(),
                EventLog.LastSeq,
                _failedReports,
                _error);
        }
    }
}

/// <summary>Estado de una ejecución (plan §4.1, <c>RunStatusDto</c>).</summary>
public sealed record RunStatusDto(
    string RunId,
    string RepositoryPath,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    bool ForceReindex,
    IReadOnlyList<string> RequestedReports,
    long LastSeq,
    IReadOnlyList<string> FailedReports,
    string? Error);
