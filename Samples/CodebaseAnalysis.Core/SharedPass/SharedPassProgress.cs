using System.Globalization;
using AITaskAgent.Core.Models;
using AITaskAgent.Observability.Events;
using CodebaseAnalysis.Core.Progress;

namespace CodebaseAnalysis.Core.SharedPass;

/// <summary>
/// El estado de la pasada compartida para la interfaz: fase, lotes con su estado y consumo, totales frente a los topes y, al terminar,
/// los hallazgos por severidad. Cada cambio emite el estado entero como <see cref="StepProgressEvent"/> de tipo
/// <see cref="AnalysisEventTypes.SharedPassProgress"/> (el SSE <c>sharedpass.progress</c>), por el canal de eventos del contexto como los
/// demás mensajes del pipeline; sin canal no hace nada. Es seguro entre hilos: los lotes se leen en paralelo y el juez revisa tandas en
/// paralelo, y cada cambio y su emisión van bajo el mismo cerrojo, así que los eventos salen en orden y el último es el estado final.
/// </summary>
/// <remarks>Los valores de <c>phase</c> y del <c>status</c> de un lote son los de <see cref="Phases"/> y <see cref="BatchStates"/>.</remarks>
public sealed class SharedPassProgress
{
    /// <summary>Valores de <c>phase</c>.</summary>
    public static class Phases
    {
        public const string Reading = "reading";
        public const string Consolidating = "consolidating";
        public const string Judging = "judging";
        public const string Saving = "saving";
        public const string Done = "done";

        /// <summary>Terminada, pero un tope o el plazo cortó la lectura o el juez (<c>stopReason</c>).</summary>
        public const string Cut = "cut";

        /// <summary>La pasada falló por dentro (<c>error</c>); el análisis sigue sin registro.</summary>
        public const string Failed = "failed";
    }

    /// <summary>Valores del <c>status</c> de un lote.</summary>
    public static class BatchStates
    {
        public const string Pending = "pending";
        public const string Reading = "reading";

        /// <summary>Un reintento de lo que falla o la lectura de una de sus mitades.</summary>
        public const string Retrying = "retrying";

        public const string Complete = "complete";

        /// <summary>Con huecos de cobertura, o cortado por un tope a medias.</summary>
        public const string Partial = "partial";

        public const string Failed = "failed";
        public const string NotRead = "notRead";
    }

    private sealed class Batch
    {
        public required string Id { get; init; }
        public int Files { get; init; }
        public int EstimatedTokens { get; init; }
        public string Status { get; set; } = BatchStates.Pending;
        public int Calls { get; set; }
        public decimal CostUsd { get; set; }
        public long Tokens { get; set; }
        public int? Findings { get; set; }
        public int? Gaps { get; set; }
    }

    private readonly Lock _lock = new();
    private readonly PipelineContext _context;
    private readonly string _source;
    private readonly SharedPassOptions _options;
    private readonly SharedPassCostBudget _budget;
    private readonly DateTimeOffset _startedAt;
    private readonly List<Batch> _batches = [];
    private readonly Dictionary<string, Batch> _byId = new(StringComparer.Ordinal);
    private string _phase = Phases.Reading;
    private int? _judgeCandidates;
    private int? _judgeTandas;
    private int _judgeTandasDone;
    private string? _stopReason;
    private string? _error;
    private Dictionary<string, object?>? _result;

    /// <param name="context">El contexto del paso: su canal recibe los eventos.</param>
    /// <param name="source">El <c>StepName</c> de los eventos (el paso de la pasada).</param>
    /// <param name="options">Los topes que se enseñan junto al consumo.</param>
    /// <param name="budget">El consumo de la ejecución (coste, llamadas, tokens), compartido por la lectura y el juez.</param>
    /// <param name="startedAt">Inicio de la pasada: el tiempo se cuenta desde aquí frente a <see cref="SharedPassOptions.MaxMinutes"/>.</param>
    public SharedPassProgress(PipelineContext context, string source, SharedPassOptions options, SharedPassCostBudget budget, DateTimeOffset startedAt)
    {
        _context = context;
        _source = source;
        _options = options;
        _budget = budget;
        _startedAt = startedAt;
    }

    /// <summary>Número de eventos emitidos (para los tests).</summary>
    public int Emitted { get; private set; }

    /// <summary>Empieza la lectura con los lotes del plan, todos pendientes.</summary>
    public void BeginReading(IEnumerable<(string Id, int Files, int EstimatedTokens)> batches)
    {
        lock (_lock)
        {
            _phase = Phases.Reading;
            _batches.Clear();
            _byId.Clear();
            foreach (var (id, files, tokens) in batches)
            {
                var batch = new Batch { Id = id, Files = files, EstimatedTokens = tokens };
                _batches.Add(batch);
                _byId[id] = batch;
            }
            EmitLocked();
        }
    }

    /// <summary>El lote pasa a <paramref name="status"/> (uno de <see cref="BatchStates"/>); si ya lo estaba, no se emite nada.</summary>
    public void BatchStatus(string batchId, string status)
    {
        lock (_lock)
        {
            if (!_byId.TryGetValue(batchId, out var batch) || batch.Status == status)
                return;
            batch.Status = status;
            EmitLocked();
        }
    }

    /// <summary>Lo que lleva consumido el lote tras una llamada.</summary>
    public void BatchUsage(string batchId, int calls, decimal costUsd, long tokens)
    {
        lock (_lock)
        {
            if (!_byId.TryGetValue(batchId, out var batch))
                return;
            batch.Calls = calls;
            batch.CostUsd = costUsd;
            batch.Tokens = tokens;
            EmitLocked();
        }
    }

    /// <summary>El lote terminó (o no se leyó): su estado final, su consumo y sus hallazgos y huecos.</summary>
    public void BatchDone(BatchReadResult result)
    {
        lock (_lock)
        {
            if (!_byId.TryGetValue(result.BatchId, out var batch))
                return;
            batch.Status = result.Status switch
            {
                BatchReadStatus.Complete => BatchStates.Complete,
                BatchReadStatus.Partial or BatchReadStatus.StoppedByCost => BatchStates.Partial,
                BatchReadStatus.Failed => BatchStates.Failed,
                _ => BatchStates.NotRead
            };
            batch.Calls = result.Calls;
            batch.CostUsd = result.CostUsd;
            batch.Tokens = result.TokensUsed;
            batch.Findings = result.Reading.Findings.Count;
            batch.Gaps = result.Gaps.Count;
            _stopReason ??= result.StopReason;
            EmitLocked();
        }
    }

    /// <summary>Cambia de fase (una de <see cref="Phases"/>).</summary>
    public void Phase(string phase)
    {
        lock (_lock)
        {
            _phase = phase;
            EmitLocked();
        }
    }

    /// <summary>Empieza el juez con sus candidatos y sus tandas.</summary>
    public void BeginJudge(int candidates, int tandas)
    {
        lock (_lock)
        {
            _phase = Phases.Judging;
            _judgeCandidates = candidates;
            _judgeTandas = tandas;
            _judgeTandasDone = 0;
            EmitLocked();
        }
    }

    /// <summary>Una tanda del juez terminó, revisada o saltada por un tope (<paramref name="stopReason"/>).</summary>
    public void JudgeTandaDone(string? stopReason = null)
    {
        lock (_lock)
        {
            _judgeTandasDone++;
            _stopReason ??= stopReason;
            EmitLocked();
        }
    }

    /// <summary>
    /// Fin: <see cref="Phases.Done"/> o, con <paramref name="stopReason"/>, <see cref="Phases.Cut"/>; con los hallazgos confirmados por
    /// severidad, el total de registros y los rechazados por el juez.
    /// </summary>
    public void Finish(string? stopReason, IReadOnlyDictionary<string, int> severities, int entries, int rejected)
    {
        lock (_lock)
        {
            _stopReason = stopReason ?? _stopReason;
            _phase = _stopReason != null ? Phases.Cut : Phases.Done;
            _result = new Dictionary<string, object?>
            {
                ["entries"] = entries,
                ["rejected"] = rejected,
                ["severities"] = severities.ToDictionary(kv => kv.Key, kv => (object?)kv.Value)
            };
            EmitLocked();
        }
    }

    /// <summary>La pasada falló por dentro: fase <see cref="Phases.Failed"/> con el error.</summary>
    public void Fail(string error)
    {
        lock (_lock)
        {
            _phase = Phases.Failed;
            _error = error;
            EmitLocked();
        }
    }

    private void EmitLocked()
    {
        var elapsedMs = (long)Math.Max(0, (DateTimeOffset.UtcNow - _startedAt).TotalMilliseconds);
        var cost = _budget.RunSpentUsd;
        var done = _batches.Count(b => b.Status is BatchStates.Complete or BatchStates.Partial or BatchStates.Failed or BatchStates.NotRead);
        var metadata = new Dictionary<string, object?>
        {
            [AnalysisEventTypes.MetadataKeys.Level] = _phase is Phases.Cut or Phases.Failed ? "warning" : _phase == Phases.Done ? "success" : "info",
            ["phase"] = _phase,
            ["startedAt"] = _startedAt,
            ["batches"] = _batches.Select(b => (object?)new Dictionary<string, object?>
            {
                ["id"] = b.Id,
                ["files"] = b.Files,
                ["estimatedTokens"] = b.EstimatedTokens,
                ["status"] = b.Status,
                ["calls"] = b.Calls,
                ["costUsd"] = b.CostUsd,
                ["tokens"] = b.Tokens,
                ["findings"] = b.Findings,
                ["gaps"] = b.Gaps
            }).ToList(),
            ["totals"] = new Dictionary<string, object?>
            {
                ["costUsd"] = cost,
                ["maxCostUsd"] = Cap(_options.MaxCostPerRunUsd),
                ["calls"] = _budget.Calls,
                ["maxCalls"] = _options.MaxCallsPerRun > 0 ? _options.MaxCallsPerRun : null,
                ["tokens"] = _budget.Tokens,
                ["maxTokens"] = _options.MaxTokensPerRun > 0 ? _options.MaxTokensPerRun : null,
                ["unpricedCalls"] = _budget.UnpricedCalls,
                ["elapsedMs"] = elapsedMs,
                ["maxMinutes"] = Cap(_options.MaxMinutes),
                ["batchesDone"] = done,
                ["batchesTotal"] = _batches.Count
            },
            ["judge"] = _judgeCandidates is int candidates
                ? new Dictionary<string, object?> { ["candidates"] = candidates, ["tandas"] = _judgeTandas, ["tandasDone"] = _judgeTandasDone }
                : null,
            ["stopReason"] = _stopReason,
            ["error"] = _error,
            ["result"] = _result
        };

        Emitted++;
        _ = _context.SendEventAsync(new StepProgressEvent
        {
            StepName = _source,
            EventType = AnalysisEventTypes.SharedPassProgress,
            Message = string.Format(CultureInfo.InvariantCulture, "Pasada compartida: {0} ({1}/{2} lotes, {3:0.###} USD)", _phase, done, _batches.Count, cost),
            CorrelationId = _context.CorrelationId,
            Metadata = metadata
        });
    }

    private static double? Cap(double value) => value > 0 && double.IsFinite(value) ? value : null;
}
