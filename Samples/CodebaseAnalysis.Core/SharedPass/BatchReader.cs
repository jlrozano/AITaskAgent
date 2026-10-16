using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.Support.Template;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Steps;

namespace CodebaseAnalysis.Core.SharedPass;

/// <summary>Lo que se pinta en las plantillas de la lectura: el paquete y, en un reintento, lo ya válido y lo que falla.</summary>
public sealed record SharedPassReaderMessage(string Packet, string Valid = "", string Errors = "");

/// <summary>Entrada del paso de la lectura (<c>{{ input.packet }}</c>, <c>{{ input.valid }}</c>, <c>{{ input.errors }}</c>).</summary>
public sealed class SharedPassReaderInput : StepResult<SharedPassReaderMessage>
{
    public SharedPassReaderInput(SharedPassReaderMessage message)
        : base(new EmptyStep(nameof(SharedPassReaderInput)), message)
    {
    }
}

/// <summary>
/// El paso LLM de una lectura: sin herramientas y sin conversación (cada llamada en su propio contexto de rama). El paquete, lo
/// válido y los errores no pasan por el motor de plantillas: se pintan marcadores y se sustituyen después, porque el código del
/// paquete puede traer <c>{{ … }}</c> o <c>@[ruta]</c>, que el motor interpretaría.
/// </summary>
public sealed class SharedPassReaderStep(string name, LlmRole role, ITemplateProvider templateProvider, string promptTemplateName, string systemPromptTemplateName)
    : CustomStatelessTemplateLlmStep<SharedPassReaderInput, LlmStringStepResult>(name, role, templateProvider, promptTemplateName, systemPromptTemplateName)
{
    private const string PacketMark = "\u0001SHARED-PASS-PACKET\u0001";
    private const string ValidMark = "\u0001SHARED-PASS-VALID\u0001";
    private const string ErrorsMark = "\u0001SHARED-PASS-ERRORS\u0001";

    protected override async Task<string?> BuildUserMessageAsync(IStepResult input, PipelineContext context)
    {
        if (input is not SharedPassReaderInput { Value: { } message })
            return await base.BuildUserMessageAsync(input, context);
        var rendered = await base.BuildUserMessageAsync(new SharedPassReaderInput(new SharedPassReaderMessage(PacketMark, ValidMark, ErrorsMark)), context);
        return rendered?.Replace(PacketMark, message.Packet, StringComparison.Ordinal)
            .Replace(ValidMark, message.Valid, StringComparison.Ordinal)
            .Replace(ErrorsMark, message.Errors, StringComparison.Ordinal);
    }
}

/// <summary>
/// Los topes de la pasada compartida con corte duro: los de coste (<see cref="SharedPassOptions.MaxCostPerBatchUsd"/>,
/// <see cref="SharedPassOptions.MaxCostPerStageUsd"/>, <see cref="SharedPassOptions.MaxCostPerRunUsd"/>), los de llamadas y tokens
/// de la ejecución (<see cref="SharedPassOptions.MaxCallsPerRun"/>, <see cref="SharedPassOptions.MaxTokensPerRun"/>), que cuentan
/// aunque no haya precio, y el plazo (<see cref="SharedPassOptions.MaxMinutes"/>, <see cref="Deadline"/>). Una vez alcanzado uno, no
/// se hace ninguna llamada más en su ámbito. El coste es el que traen los resultados del LLM (o el estimado de una llamada cortada);
/// una llamada sin precio no suma (se cuenta en <see cref="UnpricedCalls"/>, y entonces el gasto es un mínimo). Es seguro entre
/// hilos. Lo comparten las etapas de una ejecución (lectura y consolidación): <see cref="BeginStage"/> reinicia solo el gasto de la
/// etapa.
/// </summary>
/// <remarks>Los topes se comprueban antes de cada llamada: las que ya están en curso terminan, así que los de coste y tokens se
/// pueden pasar en lo que cueste una llamada por lote en paralelo. El de llamadas es exacto aunque haya lotes en paralelo: cada
/// llamada se reserva antes de salir (<see cref="TryBeginCall"/>) y las reservadas cuentan. El plazo, en cambio, corta también las
/// que están en curso.</remarks>
public sealed class SharedPassCostBudget : IDisposable
{
    /// <summary>El plazo más largo que admite el temporizador; uno mayor se recorta a este.</summary>
    private static readonly TimeSpan MaxDeadline = TimeSpan.FromDays(30);

    private readonly Lock _lock = new();
    private readonly decimal? _batchCap;
    private readonly decimal? _stageCap;
    private readonly decimal? _runCap;
    private readonly int? _callsCap;
    private readonly long? _tokensCap;
    private readonly CancellationTokenSource? _deadline;
    private readonly double _maxMinutes;
    private decimal _run;
    private decimal _stage;
    private int _unpriced;
    private int _calls;
    private int _inFlight;
    private long _tokens;

    /// <param name="options">Los topes y el plazo (que empieza a contar aquí).</param>
    /// <param name="spentInRunUsd">Lo que la ejecución ya gastó en la pasada antes de esta etapa.</param>
    public SharedPassCostBudget(SharedPassOptions options, decimal spentInRunUsd = 0)
    {
        _batchCap = Cap(options.MaxCostPerBatchUsd);
        _stageCap = Cap(options.MaxCostPerStageUsd);
        _runCap = Cap(options.MaxCostPerRunUsd);
        _callsCap = options.MaxCallsPerRun > 0 ? options.MaxCallsPerRun : null;
        _tokensCap = options.MaxTokensPerRun > 0 ? options.MaxTokensPerRun : null;
        _maxMinutes = options.MaxMinutes;
        if (options.MaxMinutes > 0 && double.IsFinite(options.MaxMinutes))
        {
            var span = options.MaxMinutes >= MaxDeadline.TotalMinutes ? MaxDeadline : TimeSpan.FromMinutes(options.MaxMinutes);
            _deadline = new CancellationTokenSource(span);
        }
        _run = spentInRunUsd;
    }

    public string Stage { get; private set; } = "";

    public decimal RunSpentUsd { get { lock (_lock) { return _run; } } }
    public decimal StageSpentUsd { get { lock (_lock) { return _stage; } } }
    public int UnpricedCalls { get { lock (_lock) { return _unpriced; } } }
    public int Calls { get { lock (_lock) { return _calls; } } }
    public long Tokens { get { lock (_lock) { return _tokens; } } }

    /// <summary>Se cancela al vencer el plazo de la pasada; las llamadas lo enlazan con el token del llamante.</summary>
    public CancellationToken Deadline => _deadline?.Token ?? CancellationToken.None;

    /// <summary>El plazo venció.</summary>
    public bool DeadlineExpired => _deadline?.IsCancellationRequested == true;

    /// <summary>El motivo de corte por el plazo, para avisos y huecos.</summary>
    public string DeadlineReason => string.Format(CultureInfo.InvariantCulture, "plazo de la pasada agotado (MaxMinutes = {0:0.###})", _maxMinutes);

    /// <summary>Tokens estimados de un texto (caracteres / 4 hacia arriba), como los paquetes.</summary>
    public static int EstimateTokens(long chars) => (int)Math.Min(int.MaxValue, (Math.Max(0, chars) + 3) / 4);

    /// <summary>Lo que cuesta enviar <paramref name="promptTokens"/> tokens de entrada a <paramref name="role"/>; null si su modelo no tiene precio.</summary>
    public static decimal? EstimateInputCostUsd(LlmRole role, int promptTokens) => role.Provider.CalculateCost(role.Model, promptTokens, 0);

    /// <summary>El modelo de <paramref name="role"/> tiene precio de entrada y de salida: su coste se puede topar.</summary>
    public static bool IsPriced(LlmRole role) => role.Provider.CalculateCost(role.Model, 1, 1) != null;

    public void Dispose() => _deadline?.Dispose();

    public void BeginStage(string stage)
    {
        lock (_lock)
        {
            Stage = stage;
            _stage = 0;
        }
    }

    /// <summary>Una llamada hecha, con su coste (null si no tiene precio) y sus tokens.</summary>
    public void Add(decimal? costUsd, long tokens = 0)
    {
        lock (_lock)
        {
            AddLocked(costUsd, tokens);
        }
    }

    private void AddLocked(decimal? costUsd, long tokens)
    {
        _calls++;
        _tokens += Math.Max(0, tokens);
        if (costUsd is decimal cost)
        {
            _run += cost;
            _stage += cost;
        }
        else
        {
            _unpriced++;
        }
    }

    /// <summary>
    /// Reserva una llamada si ningún tope lo impide (<see cref="StopReason"/> con <paramref name="batchSpentUsd"/>) y devuelve null;
    /// si no, el motivo. La comprobación y la reserva son atómicas: con lotes en paralelo, el tope de llamadas no se pasa. Cada
    /// reserva se cierra con <see cref="EndCall"/> (hecha) o <see cref="CancelCall"/> (no llegó a contar).
    /// </summary>
    public string? TryBeginCall(decimal batchSpentUsd = 0)
    {
        if (DeadlineExpired)
            return DeadlineReason;
        lock (_lock)
        {
            if ((StopReasonLocked() ?? BatchStopReason(batchSpentUsd)) is { } reason)
                return reason;
            _inFlight++;
            return null;
        }
    }

    /// <summary>Cierra una reserva de <see cref="TryBeginCall"/> con lo que costó la llamada (como <see cref="Add"/>).</summary>
    public void EndCall(decimal? costUsd, long tokens = 0)
    {
        lock (_lock)
        {
            _inFlight = Math.Max(0, _inFlight - 1);
            AddLocked(costUsd, tokens);
        }
    }

    /// <summary>Suelta una reserva de <see cref="TryBeginCall"/> sin contarla (la llamada no llegó a hacerse).</summary>
    public void CancelCall()
    {
        lock (_lock)
        {
            _inFlight = Math.Max(0, _inFlight - 1);
        }
    }

    /// <summary>
    /// Por qué no se puede hacer otra llamada en la etapa o la ejecución; null si se puede. Las llamadas reservadas y aún en curso
    /// cuentan para el tope de llamadas.
    /// </summary>
    public string? StageStopReason()
    {
        if (DeadlineExpired)
            return DeadlineReason;
        lock (_lock)
        {
            return StopReasonLocked();
        }
    }

    private string? StopReasonLocked()
    {
        if (_callsCap is int calls && _calls + _inFlight >= calls)
            return string.Format(CultureInfo.InvariantCulture, "tope de llamadas de la ejecución alcanzado ({0} de {1})", _calls + _inFlight, calls);
        if (_tokensCap is long tokens && _tokens >= tokens)
            return string.Format(CultureInfo.InvariantCulture, "tope de tokens de la ejecución alcanzado ({0} de {1})", _tokens, tokens);
        if (_runCap is decimal run && _run >= run)
            return string.Format(CultureInfo.InvariantCulture, "tope de coste de la ejecución alcanzado ({0:0.###} de {1:0.###} USD)", _run, run);
        if (_stageCap is decimal stage && _stage >= stage)
            return string.Format(CultureInfo.InvariantCulture, "tope de coste de la etapa '{0}' alcanzado ({1:0.###} de {2:0.###} USD)", Stage, _stage, stage);
        return null;
    }

    /// <summary>
    /// Como <see cref="StageStopReason"/>, y además el tope del lote con lo que el lote lleva gastado (o, en una tanda del juez, que es
    /// una sola llamada, lo que costaría enviarla).
    /// </summary>
    public string? StopReason(decimal batchSpentUsd) => StageStopReason() ?? BatchStopReason(batchSpentUsd);

    private string? BatchStopReason(decimal batchSpentUsd) =>
        _batchCap is decimal batch && batchSpentUsd >= batch
            ? string.Format(CultureInfo.InvariantCulture, "tope de coste del lote alcanzado ({0:0.###} de {1:0.###} USD)", batchSpentUsd, batch)
            : null;

    private static decimal? Cap(double value) => value > 0 && double.IsFinite(value) ? (decimal)value : null;
}

/// <summary>Cómo acabó la lectura de un lote.</summary>
public enum BatchReadStatus
{
    /// <summary>Todo cubierto y validado (puede haber hallazgos rechazados por su ancla).</summary>
    Complete,

    /// <summary>Con huecos de cobertura tras los reintentos y la partición.</summary>
    Partial,

    /// <summary>Un tope de coste la cortó; lo que faltaba queda en los huecos.</summary>
    StoppedByCost,

    /// <summary>Sin ninguna lectura válida (el LLM falló o nunca devolvió JSON legible).</summary>
    Failed,

    /// <summary>No se empezó: la etapa ya estaba cortada por coste.</summary>
    NotRead
}

/// <summary>Lo que quedó sin cubrir de un lote, con el motivo. Nunca se descarta en silencio.</summary>
public sealed record CoverageGap(string Batch, ReaderIssueKind Kind, string? File, string Target, string Reason);

/// <summary>El resultado de leer un lote: la lectura válida, lo rechazado, los huecos y el consumo.</summary>
public sealed record BatchReadResult
{
    public required string BatchId { get; init; }
    public BatchReadStatus Status { get; init; }
    public required BatchReading Reading { get; init; }
    public IReadOnlyList<CoverageGap> Gaps { get; init; } = [];
    public IReadOnlyList<RejectedFinding> Rejected { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Llamadas al LLM del lote, reintentos y mitades incluidos.</summary>
    public int Calls { get; init; }

    public decimal CostUsd { get; init; }
    public int UnpricedCalls { get; init; }
    public long TokensUsed { get; init; }

    /// <summary>sha256 del paquete (el <c>input_hash</c> de lo que se escriba con esta lectura).</summary>
    public string InputHash { get; init; } = "";

    /// <summary>Veces que el lote, o una de sus mitades, se partió en dos.</summary>
    public int Splits { get; init; }

    public string? StopReason { get; init; }
}

/// <summary>El resultado de la etapa de lectura: un <see cref="BatchReadResult"/> por lote, en el orden del plan.</summary>
public sealed record SharedPassReadResult
{
    public IReadOnlyList<BatchReadResult> Batches { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Algún tope de coste cortó la etapa o un lote.</summary>
    public bool StoppedByCostLimit => Batches.Any(b => b.Status is BatchReadStatus.StoppedByCost or BatchReadStatus.NotRead);

    /// <summary>El primer motivo de corte, si lo hubo.</summary>
    public string? StopReason => Batches.Select(b => b.StopReason).FirstOrDefault(r => r != null);

    public decimal CostUsd => Batches.Sum(b => b.CostUsd);
    public int Calls => Batches.Sum(b => b.Calls);
    public IEnumerable<CoverageGap> Gaps => Batches.SelectMany(b => b.Gaps);
    public IEnumerable<RejectedFinding> Rejected => Batches.SelectMany(b => b.Rejected);
}

/// <summary>
/// D2 de la pasada compartida: la lectura de cada lote con una llamada al LLM, sin herramientas y sin conversación
/// (<see cref="SharedPassReaderStep"/>, fuente <see cref="StepName"/> en el consumo por fuente), y su validación en cascada
/// (<see cref="BatchReadingJson"/> y <see cref="ReadingValidator"/>).
/// </summary>
/// <remarks>
/// <para><b>Reintento solo de lo que falla</b>: hasta <see cref="SharedPassOptions.MaxReaderRetries"/> llamadas más con el paquete,
/// un resumen de lo ya válido y la lista de errores concretos; la respuesta se suma a lo anterior
/// (<see cref="BatchReading.Merge"/>) y se vuelve a validar el conjunto.</para>
/// <para><b>Partición</b>: si la respuesta se corta (<see cref="FinishReason.Length"/>) o tras los reintentos sigue faltando
/// cobertura, los ficheros con huecos (todos si se cortó) se parten en dos mitades por orden de fichero, cada una con su paquete,
/// y se leen igual (hasta <see cref="MaxSplitDepth"/> niveles); lo ya válido de los demás ficheros se conserva. Los <c>localId</c>
/// de una mitad llevan su prefijo (<c>s1.F3</c>) para no chocar.</para>
/// <para><b>Lo irrecuperable</b> queda en <see cref="BatchReadResult.Gaps"/> (cobertura) o <see cref="BatchReadResult.Rejected"/>
/// (hallazgos sin ancla literal); lo que se quita sin ser un hallazgo (instancias, anclas de relaciones, elementos sin esquema) va
/// a <see cref="BatchReadResult.Warnings"/>.</para>
/// <para><b>Coste</b>: <see cref="SharedPassCostBudget"/>, comprobado (y la llamada reservada) antes de cada llamada.</para>
/// <para><b>Paralelismo</b>: todos los lotes a la vez, como mucho <see cref="SharedPassOptions.MaxParallelBatches"/>, sin esperar
/// por niveles (un paquete no lleva nada de la lectura de otro lote: se construyen todos antes, en D1). Arrancan de mayor a menor
/// tamaño estimado, para que el más largo no quede para el final; el resultado va siempre en el orden del plan.</para>
/// <para><b>Errores reintentables del proveedor</b> (<see cref="LlmProviderException.IsRetriable"/>, p. ej. un 429 cuando el
/// servicio ya agotó sus propios reintentos con <c>Retry-After</c>): el siguiente intento del lote espera
/// <see cref="RetriableErrorDelays"/>; cada llamada cuenta en el presupuesto como cualquier otra.</para>
/// <para>El resultado se queda en memoria: escribirlo en el grafo es de la consolidación (D3).</para>
/// </remarks>
public sealed class BatchReader
{
    /// <summary>Nombre del paso LLM: la fuente de su consumo en <c>usage.bySource</c>.</summary>
    public const string StepName = "SharedPassReader";

    public const string SystemTemplate = "shared_pass_reader_system";
    public const string PromptTemplate = "shared_pass_reader_prompt";
    public const string RetryPromptTemplate = "shared_pass_reader_retry_prompt";

    /// <summary>Niveles de partición de un lote como mucho (un lote de 16 ficheros llega a uno por mitad).</summary>
    public const int MaxSplitDepth = 4;

    /// <summary>Errores que se listan en un reintento; el resto se resume con su número.</summary>
    public const int MaxIssuesInRetry = 200;

    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Espera antes del siguiente intento de un lote tras un error reintentable del proveedor: la primera, la segunda (y la última
    /// para las siguientes, si <see cref="SharedPassOptions.MaxReaderRetries"/> da más de dos intentos).
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> RetriableErrorDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];

    private readonly LlmRole _role;
    private readonly ITemplateProvider _templates;
    private readonly SharedPassGraph _graph;
    private readonly SharedPassOptions _options;
    private readonly PacketBuilder _packets;
    private readonly ReadingValidator _validator;
    private readonly TimeSpan _callTimeout;

    /// <summary>La espera entre intentos tras un error reintentable; los tests la sustituyen para no dormir.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>Estado para la interfaz (<c>sharedpass.progress</c>): lotes, su estado y su consumo. Null = no se informa.</summary>
    public SharedPassProgress? Progress { get; init; }

    /// <param name="role">Rol de la lectura (<see cref="SharedPassOptions.ReaderRole"/>); su <c>SystemPrompt</c>, si lo tiene, sustituye a <see cref="SystemTemplate"/>.</param>
    /// <param name="templates">Proveedor de plantillas del análisis.</param>
    /// <param name="graph">Los mismos hechos del grafo que los paquetes: dicen qué cubrir y con ellos se rehacen los paquetes al partir.</param>
    /// <param name="repositoryRoot">Raíz del repositorio: las anclas se comprueban contra sus ficheros.</param>
    /// <param name="options">Reintentos, topes y paralelismo.</param>
    /// <param name="defaultEncoding">Para los ficheros que no son UTF-8 válido; null = el del repositorio, como en <see cref="PacketBuilder"/>.</param>
    /// <param name="callTimeout">Límite de cada llamada; <see cref="DefaultCallTimeout"/> si no se indica.</param>
    public BatchReader(LlmRole role, ITemplateProvider templates, SharedPassGraph graph, string repositoryRoot, SharedPassOptions options,
        Encoding? defaultEncoding = null, TimeSpan? callTimeout = null)
    {
        _role = role;
        _templates = templates;
        _graph = graph;
        _options = options;
        _packets = new PacketBuilder(graph, repositoryRoot, options, defaultEncoding);
        _validator = new ReadingValidator(repositoryRoot, graph, defaultEncoding);
        _callTimeout = callTimeout ?? DefaultCallTimeout;
    }

    /// <summary>D1 y D2 de una vez: los paquetes de <paramref name="map"/> (<see cref="PacketBuilder.BuildAllAsync"/>) y su lectura.</summary>
    public static async Task<SharedPassReadResult> RunAsync(
        string databasePath, string repositoryRoot, DomainMap map, SharedPassOptions options, LlmRole role, ITemplateProvider templates,
        PipelineContext context, CancellationToken ct, SharedPassCostBudget? budget = null, SharedPassProgress? progress = null)
    {
        var set = await PacketBuilder.BuildAllAsync(databasePath, repositoryRoot, map, options, ct);
        var graph = await SharedPassGraph.LoadAsync(databasePath, ct);
        return await new BatchReader(role, templates, graph, repositoryRoot, options) { Progress = progress }.ReadAllAsync(set, context, ct, budget);
    }

    /// <summary>
    /// Lee todos los lotes de <paramref name="set"/> en paralelo limitado (<see cref="SharedPassOptions.MaxParallelBatches"/>),
    /// arrancando por los más grandes; el resultado, en el orden del plan.
    /// </summary>
    /// <param name="set">Plan y paquetes de D1.</param>
    /// <param name="context">Contexto del pipeline: cada llamada usa una rama suya.</param>
    /// <param name="ct">Cancelación.</param>
    /// <param name="budget">Topes compartidos con el resto de la ejecución; null = unos nuevos con <see cref="SharedPassOptions"/>.
    /// Esta etapa se llama <c>lectura</c> (<see cref="SharedPassCostBudget.BeginStage"/>).</param>
    public async Task<SharedPassReadResult> ReadAllAsync(SharedPassPacketSet set, PipelineContext context, CancellationToken ct, SharedPassCostBudget? budget = null)
    {
        using var owned = budget == null ? new SharedPassCostBudget(_options) : null;
        budget ??= owned!;
        budget.BeginStage("lectura");
        var packets = set.Packets.ToDictionary(p => p.BatchId, StringComparer.Ordinal);
        var results = new BatchReadResult[set.Plan.Batches.Count];
        var warnings = new List<string>(set.Warnings);
        Progress?.BeginReading(set.Plan.Batches.Select(b => (b.Id, b.Files.Count, packets.TryGetValue(b.Id, out var p) ? p.EstimatedTokens : b.EstimatedTokens)));

        // Cada resultado va a su posición del plan, acabe cuando acabe: el orden de arranque no se ve fuera.
        var queue = StartOrder(set.Plan.Batches, packets);
        var next = -1;
        var workers = Enumerable.Range(0, Math.Min(Math.Max(1, _options.MaxParallelBatches), Math.Max(1, queue.Count))).Select(async _ =>
        {
            await Task.Yield();
            for (var i = Interlocked.Increment(ref next); i < queue.Count; i = Interlocked.Increment(ref next))
            {
                ct.ThrowIfCancellationRequested();
                var (batch, index) = queue[i];
                results[index] = budget.StageStopReason() is { } reason
                    ? NotRead(batch, reason)
                    : packets.TryGetValue(batch.Id, out var packet)
                        ? await ReadBatchAsync(batch, packet, set.Plan.Batches, context, budget, ct)
                        : NotRead(batch, "el lote no tiene paquete") with { Status = BatchReadStatus.Failed, StopReason = null };
                Progress?.BatchDone(results[index]);
            }
        }).ToList();
        await Task.WhenAll(workers);

        var result = new SharedPassReadResult { Batches = results, Warnings = warnings };
        context.Report(StepName, result.StoppedByCostLimit ? AnalysisLogLevel.Warning : AnalysisLogLevel.Info, string.Format(CultureInfo.InvariantCulture,
            "📚 Lectura por lote: {0} lotes, {1} llamadas, {2:0.###} USD{3}; {4} huecos de cobertura y {5} hallazgos rechazados.",
            results.Length, result.Calls, result.CostUsd, result.StopReason is { } stop ? $" (cortada: {stop})" : "", result.Gaps.Count(), result.Rejected.Count()));
        return result;
    }

    /// <summary>Lee un lote. <paramref name="plan"/> son todos los lotes (para rehacer las relaciones de las mitades si se parte).</summary>
    public async Task<BatchReadResult> ReadBatchAsync(
        SharedPassBatch batch, SharedPassPacket packet, IReadOnlyList<SharedPassBatch> plan, PipelineContext context, SharedPassCostBudget budget, CancellationToken ct)
    {
        var account = new Account(batch.Id, Progress);
        Progress?.BatchStatus(batch.Id, SharedPassProgress.BatchStates.Reading);
        context.Report(StepName, AnalysisLogLevel.Info, $"📖 Leyendo el lote '{batch.Id}' ({batch.Files.Count} ficheros, ~{packet.EstimatedTokens} tokens)...");
        var unit = await ReadUnitAsync(batch, packet, plan, 0, context, budget, account, ct);

        var status = unit.StopReason != null ? BatchReadStatus.StoppedByCost
            : unit.Failed ? BatchReadStatus.Failed
            : unit.Gaps.Count > 0 ? BatchReadStatus.Partial
            : BatchReadStatus.Complete;
        var result = new BatchReadResult
        {
            BatchId = batch.Id,
            Status = status,
            Reading = unit.Reading with { Batch = batch.Id },
            Gaps = unit.Gaps,
            Rejected = unit.Rejected,
            Warnings = unit.Warnings,
            Calls = account.Calls,
            CostUsd = account.Cost,
            UnpricedCalls = account.Unpriced,
            TokensUsed = account.Tokens,
            InputHash = Hash(packet.Markdown),
            Splits = unit.Splits,
            StopReason = unit.StopReason
        };
        context.Report(StepName, status == BatchReadStatus.Complete ? AnalysisLogLevel.Success : AnalysisLogLevel.Warning, string.Format(CultureInfo.InvariantCulture,
            "{0} Lote '{1}': {2} ({3} llamadas, {4:0.###} USD; {5} hallazgos, {6} rechazados, {7} huecos{8}).",
            status == BatchReadStatus.Complete ? "✅" : "⚠️", batch.Id, status, account.Calls, account.Cost, result.Reading.Findings.Count,
            result.Rejected.Count, result.Gaps.Count, unit.StopReason is { } stop ? "; " + stop : ""));
        return result;
    }

    /// <summary>
    /// El orden en que arrancan los lotes: de mayor a menor tamaño estimado (el del paquete; si no lo hay, el del plan), con empate
    /// por la posición en el plan. Con un tope de paralelismo, empezar por el más largo acorta el total.
    /// </summary>
    internal static List<(SharedPassBatch Batch, int Index)> StartOrder(IReadOnlyList<SharedPassBatch> plan, IReadOnlyDictionary<string, SharedPassPacket> packets) =>
        [.. plan.Select((b, i) => (Batch: b, Index: i))
            .OrderByDescending(x => packets.TryGetValue(x.Batch.Id, out var p) ? p.EstimatedTokens : x.Batch.EstimatedTokens)
            .ThenBy(x => x.Index)];

    private static BatchReadResult NotRead(SharedPassBatch batch, string reason) => new()
    {
        BatchId = batch.Id,
        Status = BatchReadStatus.NotRead,
        Reading = BatchReading.Empty(batch.Id),
        Gaps = [.. batch.Files.Select(f => new CoverageGap(batch.Id, ReaderIssueKind.MissingFile, f, f, $"no se leyó: {reason}"))],
        StopReason = reason
    };

    // ───────────────────────────── Un lote o una de sus mitades ─────────────────────────────

    /// <summary>El consumo de un lote (con sus reintentos y mitades), que se informa a <paramref name="progress"/> tras cada llamada.</summary>
    private sealed class Account(string batchId, SharedPassProgress? progress)
    {
        private readonly Lock _lock = new();
        public int Calls;
        public decimal Cost;
        public int Unpriced;
        public long Tokens;

        public string BatchId => batchId;

        public void Add(ReaderCall call)
        {
            int calls;
            decimal spent;
            long tokens;
            lock (_lock)
            {
                Calls++;
                if (call.CostUsd is decimal cost)
                    Cost += cost;
                else
                    Unpriced++;
                Tokens += call.Tokens ?? 0;
                (calls, spent, tokens) = (Calls, Cost, Tokens);
            }
            progress?.BatchUsage(batchId, calls, spent, tokens);
        }
    }

    private sealed record ReaderCall(string? Text, FinishReason? Finish, decimal? CostUsd, int? Tokens, string? Error, bool Retriable = false);

    private sealed record UnitResult(
        BatchReading Reading, List<CoverageGap> Gaps, List<RejectedFinding> Rejected, List<string> Warnings, string? StopReason, bool Failed, int Splits);

    private async Task<UnitResult> ReadUnitAsync(
        SharedPassBatch batch, SharedPassPacket packet, IReadOnlyList<SharedPassBatch> plan, int depth,
        PipelineContext context, SharedPassCostBudget budget, Account account, CancellationToken ct)
    {
        var expected = BatchExpectations.From(_graph, batch.Files, packet.ShownLines);
        var warnings = new List<string>();
        BatchReading? merged = null;
        ReadingValidation? validation = null;
        var pending = new List<ReaderIssue>();
        // Los hallazgos descartados por esquema siguen pendientes hasta que un reintento los trae bien.
        var droppedFindings = new Dictionary<string, ReaderIssue>(StringComparer.Ordinal);
        var truncated = false;
        string? stop = null;
        string? lastError = null;
        var retriableErrors = 0;

        for (var attempt = 0; attempt <= _options.MaxReaderRetries; attempt++)
        {
            if ((stop = budget.TryBeginCall(account.Cost)) != null)
                break;
            if (attempt > 0 || depth > 0)
                Progress?.BatchStatus(account.BatchId, SharedPassProgress.BatchStates.Retrying);

            var retry = attempt > 0 && pending.Count > 0;
            var message = retry
                ? new SharedPassReaderMessage(packet.Markdown, DescribeValid(validation?.Valid), DescribeIssues(pending))
                : new SharedPassReaderMessage(packet.Markdown);
            ReaderCall call;
            try
            {
                call = await CallAsync(message, retry, context, budget, ct);
            }
            catch
            {
                budget.CancelCall();
                throw;
            }
            account.Add(call);
            budget.EndCall(call.CostUsd, call.Tokens ?? 0);

            if (call.Error != null)
            {
                lastError = call.Error;
                warnings.Add($"Lote '{batch.Id}', intento {attempt + 1}: el LLM falló ({call.Error}).");
                if (call.Retriable && attempt < _options.MaxReaderRetries)
                    await WaitBeforeRetryAsync(RetriableErrorDelays[Math.Min(retriableErrors++, RetriableErrorDelays.Count - 1)], budget, ct);
                continue;
            }
            lastError = null;
            if (call.Finish == FinishReason.Length)
            {
                truncated = true;
                warnings.Add($"Lote '{batch.Id}', intento {attempt + 1}: la respuesta se cortó al alcanzar el máximo de salida.");
                break;
            }

            var parseIssues = new List<ReaderIssue>();
            if (BatchReadingJson.Parse(call.Text, batch.Id, parseIssues) is { } parsed)
                merged = merged?.Merge(parsed) ?? parsed;
            foreach (var issue in parseIssues.Where(i => i.Kind == ReaderIssueKind.Schema && i.Target != null))
                droppedFindings[issue.Target!] = issue;
            if (merged != null)
            {
                foreach (var finding in merged.Findings)
                    droppedFindings.Remove(finding.LocalId);
                validation = _validator.Validate(merged, expected);
            }

            pending = [.. parseIssues.Where(i => i.Target == null), .. droppedFindings.Values, .. validation?.Issues ?? []];
            if (pending.Count == 0)
                break;
        }

        var valid = validation?.Valid ?? BatchReading.Empty(batch.Id);
        var rejected = new List<RejectedFinding>(validation?.Rejected ?? []);
        warnings.AddRange(validation?.Warnings ?? []);
        warnings.AddRange(droppedFindings.Values.Select(i => $"Lote '{batch.Id}': se descarta un hallazgo sin esquema válido tras los reintentos: {i.Message}"));
        var coverage = pending.Where(i => i.IsCoverage).ToList();

        if (stop != null)
        {
            var gaps = merged == null ? AllFilesGap(batch, $"no se leyó: {stop}") : Gaps(batch, coverage, stop);
            return new UnitResult(valid, gaps, rejected, warnings, stop, false, 0);
        }

        var failedFiles = truncated || merged == null
            ? batch.Files.ToList()
            : coverage.Select(i => i.File).OfType<string>().Where(f => batch.Files.Contains(f, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var splittable = (truncated || coverage.Count > 0) && failedFiles.Count > 1 && depth < MaxSplitDepth;

        if (!splittable)
        {
            if (merged == null)
            {
                var reason = truncated ? "la respuesta se cortó y el lote no se puede partir más"
                    : lastError != null ? $"el LLM falló en todos los intentos ({lastError})"
                    : "ninguna respuesta tuvo un JSON legible";
                return new UnitResult(valid, AllFilesGap(batch, reason), rejected, warnings, null, !truncated, 0);
            }
            var why = truncated ? "la respuesta se cortó y el lote no se puede partir más" : $"sin cubrir tras {_options.MaxReaderRetries} reintento(s)";
            return new UnitResult(valid, Gaps(batch, coverage, why), rejected, warnings, null, false, 0);
        }

        // Partir: lo válido de los ficheros sin huecos se queda; los ficheros con huecos se releen en dos mitades.
        var failedSet = new HashSet<string>(failedFiles, StringComparer.OrdinalIgnoreCase);
        var kept = Without(valid, failedSet, expected);
        rejected.RemoveAll(r => failedSet.Contains(r.Finding.Anchor.Path));
        var cut = (failedFiles.Count + 1) / 2;
        List<string>[] halves = [failedFiles[..cut], failedFiles[cut..]];
        var rest = batch.Files.Where(f => !failedSet.Contains(f)).ToList();
        var halfBatches = halves.Select((files, i) => batch with
        {
            Id = $"{batch.Id}-s{i + 1}",
            Files = files,
            EstimatedTokens = 0
        }).ToList();
        var subPlan = plan.Where(b => b.Id != batch.Id).Concat(halfBatches)
            .Concat(rest.Count > 0 ? [batch with { Id = $"{batch.Id}-r", Files = rest }] : []).ToList();

        warnings.Add($"Lote '{batch.Id}': {(truncated ? "respuesta cortada" : $"{coverage.Count} hueco(s) de cobertura")}; se releen {failedFiles.Count} fichero(s) en dos mitades.");
        context.Report(StepName, AnalysisLogLevel.Warning, $"✂️ {warnings[^1]}");

        var gapsAll = new List<CoverageGap>();
        string? subStop = null;
        var splits = 1;
        for (var i = 0; i < halfBatches.Count; i++)
        {
            var half = halfBatches[i];
            var halfPacket = _packets.Build(half, subPlan);
            var sub = await ReadUnitAsync(half, halfPacket, subPlan, depth + 1, context, budget, account, ct);
            var prefix = $"s{i + 1}.";
            kept = kept.Merge(Prefixed(sub.Reading, prefix) with { Batch = kept.Batch });
            rejected.AddRange(sub.Rejected.Select(r => r with { Finding = r.Finding with { LocalId = prefix + r.Finding.LocalId } }));
            gapsAll.AddRange(sub.Gaps.Select(g => g with { Batch = batch.Id }));
            warnings.AddRange(sub.Warnings);
            subStop ??= sub.StopReason;
            splits += sub.Splits;
        }
        return new UnitResult(kept, gapsAll, rejected, warnings, subStop, false, splits);
    }

    /// <summary>
    /// Una llamada. Cortada por su límite o por el plazo de la pasada (<see cref="SharedPassCostBudget.Deadline"/>) devuelve un error
    /// con los tokens enviados estimados y su coste de entrada (lo consumido, aunque no haya respuesta); solo la cancelación del
    /// llamante (<paramref name="ct"/>) se propaga.
    /// </summary>
    private async Task<ReaderCall> CallAsync(SharedPassReaderMessage message, bool retry, PipelineContext context, SharedPassCostBudget budget, CancellationToken ct)
    {
        var step = new SharedPassReaderStep(StepName, _role, _templates, retry ? RetryPromptTemplate : PromptTemplate, _role.SystemPrompt ?? SystemTemplate)
        {
            Timeout = _callTimeout
        };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Deadline);
        if (_callTimeout > TimeSpan.Zero && _callTimeout != Timeout.InfiniteTimeSpan)
            limit.CancelAfter(_callTimeout);

        IStepResult result;
        try
        {
            // Un contexto de rama por llamada: el estado de un paso LLM va por contexto y los lotes se leen en paralelo.
            result = await step.ExecuteStepAsync(new SharedPassReaderInput(message), context.CloneForBranch(), 1, limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Cut();
        }
        ct.ThrowIfCancellationRequested();

        var llm = result as ILlmStepResult;
        if (result.HasError && limit.IsCancellationRequested && llm?.CostUsd is null)
            return Cut();
        if (result.HasError)
            return new ReaderCall(null, llm?.FinishReason, llm?.CostUsd, llm?.TokensUsed, result.Error?.Message ?? "error sin mensaje",
                result.Error?.OriginalException is LlmProviderException { IsRetriable: true });
        return new ReaderCall((result as LlmStringStepResult)?.Content ?? result.Value?.ToString(), llm?.FinishReason, llm?.CostUsd, llm?.TokensUsed, null);

        ReaderCall Cut()
        {
            var sent = SharedPassCostBudget.EstimateTokens((long)message.Packet.Length + message.Valid.Length + message.Errors.Length);
            var why = budget.DeadlineExpired ? $"cortada: {budget.DeadlineReason}"
                : $"superó el límite de {_callTimeout.TotalMinutes.ToString("0.###", CultureInfo.InvariantCulture)} min";
            return new ReaderCall(null, null, SharedPassCostBudget.EstimateInputCostUsd(_role, sent), sent, why);
        }
    }

    /// <summary>La espera antes de reintentar tras un error reintentable; el plazo de la pasada la acorta (el intento siguiente lo ve).</summary>
    private async Task WaitBeforeRetryAsync(TimeSpan wait, SharedPassCostBudget budget, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Deadline);
        try
        {
            await Delay(wait, limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        ct.ThrowIfCancellationRequested();
    }

    // ───────────────────────────── Apoyo ─────────────────────────────

    private static List<CoverageGap> AllFilesGap(SharedPassBatch batch, string reason) =>
        [.. batch.Files.Select(f => new CoverageGap(batch.Id, ReaderIssueKind.MissingFile, f, f, reason))];

    private static List<CoverageGap> Gaps(SharedPassBatch batch, IEnumerable<ReaderIssue> coverage, string reason) =>
        [.. coverage.Select(i => new CoverageGap(batch.Id, i.Kind, i.File, i.Target ?? i.File ?? "", $"{reason}: {i.Message}"))];

    /// <summary>La lectura sin lo de <paramref name="files"/>: sus ficheros, tipos, hallazgos anclados en ellos y veredictos de sus semillas.</summary>
    private static BatchReading Without(BatchReading reading, HashSet<string> files, BatchExpectations expected)
    {
        var seeds = expected.Seeds.Where(s => files.Contains(s.File)).ToList();
        return reading with
        {
            Files = [.. reading.Files.Where(f => !files.Contains(f.Path))],
            Types = [.. reading.Types.Where(t => !files.Contains(t.Path))],
            Findings = [.. reading.Findings.Where(f => !files.Contains(f.Anchor.Path))],
            SeedVerdicts = [.. reading.SeedVerdicts.Where(v => !seeds.Any(s => ReadingValidator.Covers(v.Seed, s)))]
        };
    }

    private static BatchReading Prefixed(BatchReading reading, string prefix) =>
        reading with { Findings = [.. reading.Findings.Select(f => f with { LocalId = prefix + f.LocalId })] };

    /// <summary>Lo ya válido, en corto: lo que el reintento no debe repetir.</summary>
    internal static string DescribeValid(BatchReading? valid)
    {
        if (valid == null || (valid.Files.Count == 0 && valid.Types.Count == 0 && valid.Findings.Count == 0 && valid.SeedVerdicts.Count == 0))
            return "(nada todavía: devuelve la lectura completa)";
        var sb = new StringBuilder();
        sb.Append("- Ficheros: ").AppendLine(valid.Files.Count == 0 ? "ninguno" : string.Join(", ", valid.Files.Select(f => f.Path)));
        sb.AppendLine("- Tipos y miembros:");
        foreach (var type in valid.Types)
            sb.Append("  - ").Append(type.Name).Append(" (").Append(type.Path).Append("): ")
                .AppendLine(type.Members.Count == 0 ? "sin miembros" : string.Join(", ", type.Members.Select(m => ReadingValidator.MemberName(m.Name)).Distinct()));
        sb.Append("- Hallazgos: ").AppendLine(valid.Findings.Count == 0 ? "ninguno" : string.Join(", ", valid.Findings.Select(f => f.LocalId)));
        sb.Append("- Veredictos de semillas: ").Append(valid.SeedVerdicts.Count.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    internal static string DescribeIssues(IReadOnlyList<ReaderIssue> issues)
    {
        var lines = issues.Take(MaxIssuesInRetry).Select(i => "- " + i.Message).ToList();
        if (issues.Count > MaxIssuesInRetry)
            lines.Add($"- (… y {issues.Count - MaxIssuesInRetry} más del mismo tipo)");
        return string.Join("\n", lines);
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
