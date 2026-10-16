using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Results;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Support.Template;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Notes;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using System.Globalization;

namespace CodebaseAnalysis.Core.Steps;

/// <summary>
/// El paso de un informe en modo B (spec 2026-10-04, §4.5): investiga por separado cada subsistema que el plan de cobertura eligió
/// para el informe y después llama al auditor de siempre —la «consolidación»— con las notas de todas las investigaciones, para que
/// escriba el informe. Se llama <c>Auditor_&lt;tipo&gt;</c>, igual que la consolidación, así que el motor, los eventos y el consumo lo
/// cuentan en el mismo carril del informe.
/// </summary>
/// <remarks>
/// <para><b>Qué hace</b>: por cada subsistema elegido lanza <c>Investigation_&lt;tipo&gt;_&lt;id&gt;</c> (un
/// <see cref="CustomStatelessTemplateLlmStep{TIn, TOut}"/> con el rol y las tools del auditor), hasta
/// <see cref="PreparationOptions.MaxParallelInvestigations"/> a la vez. Con las notas (<see cref="ReportFacetInput.InvestigationNotes"/>)
/// y el resumen de cobertura (<see cref="ReportFacetInput.CoverageSummary"/>) en una copia del input (<see cref="ReportFacetInput.CloneForReport"/>:
/// las ramas comparten el original), invoca la consolidación, que es la única que escribe el informe.</para>
/// <para><b>Degradación</b>: sin subsistemas elegidos (<c>Mode = Off</c>, sin plan, sin cobertura para este informe o un grafo sin nada
/// que investigar) llama directamente al auditor de siempre (<c>degradedAuditor</c>, con su tope de herramientas completo) con el input tal cual,
/// sin notas: el informe sale como antes de existir la preparación.</para>
/// <para><b>Consolidación acotada</b>: la consolidación se construye con un tope de herramientas propio y más corto
/// (<see cref="PreparationOptions.ConsolidationMaxToolIterations"/>): con las notas delante solo debe comprobar citas dudosas o huecos
/// concretos. Al agotarlo redacta con lo consultado (<see cref="AnalysisPipelineFactory.AuditorToolLimitPrompt"/>) en lugar de fallar.</para>
/// <para><b>No fallar duro</b>: una investigación que falla o agota su tiempo es un aviso y deja a su subsistema en «no cubierto»
/// en el resumen de cobertura; el informe se escribe igual. Solo si fallan todas el paso termina con error (como
/// <see cref="TolerantReportsParallelStep"/> cuando no queda ningún informe): sin ninguna nota la consolidación no tendría base.</para>
/// <para><b>Tiempos</b>: el <see cref="Timeout"/> del paso cubre investigaciones y consolidación y lo aplica el motor
/// (<c>TimeoutMiddleware</c>); <c>TimeBudgetStep</c> lo escribe junto con la tabla por subsistema
/// (<see cref="SetInvestigationTimeouts"/>). Cada investigación se invoca directamente (sin middleware, como el verificador en
/// <see cref="VerifyReportStep"/>), así que su límite lo aplica este paso con un token propio enlazado al del llamante: si salta el
/// propio, solo esa investigación se da por fallida; si cancela el llamante (o vence el <see cref="Timeout"/> del paso), la
/// cancelación se propaga.</para>
/// <para><b>Reintentos</b>: las investigaciones y la consolidación ya no pasan por <c>RetryMiddleware</c>, así que
/// <see cref="RunWithRetriesAsync"/> repite su bucle esencial: reintenta los errores sin excepción, y ante un error de proveedor o el tope de
/// herramientas (<see cref="IFallbackable"/>) cambia de estrategia como lo hace el motor. Sin él, una investigación que agota
/// <c>MaxToolIterations</c> fallaría sin llegar a su «respuesta final sin tools». Cada investigación corre en un contexto de rama
/// (<see cref="PipelineContext.CloneForBranch"/>): su estado y su conversación no se mezclan con los de las demás.</para>
/// </remarks>
public sealed class SubsystemInvestigationReportStep : IStep<ReportFacetInput, SingleReportResult>, IWritableTimeout, IInvestigationTimeouts
{
    /// <summary>
    /// Mensaje de la última llamada, sin tools, de una investigación que agota su tope de iteraciones: entrega ya las notas con lo
    /// consultado. No es el de los auditores (<see cref="AnalysisPipelineFactory.AuditorToolLimitPrompt"/>): una investigación no escribe el
    /// informe ni lleva la línea <c>analysis-meta</c>.
    /// </summary>
    public const string InvestigationToolLimitPrompt =
        "Has agotado las consultas a herramientas disponibles para este subsistema. No pidas más herramientas: entrega ya tus notas de " +
        "investigación con lo que has comprobado, en texto plano y con la cita ruta:línea de cada afirmación, y di expresamente qué preguntas " +
        "o evidencia mínima no has podido cubrir.";

    private readonly ReportType _reportType;
    private readonly CustomStatelessTemplateLlmStep<ReportFacetInput, SingleReportResult> _consolidation;
    private readonly CustomStatelessTemplateLlmStep<ReportFacetInput, SingleReportResult> _degradedAuditor;
    private readonly Func<string, CustomStatelessTemplateLlmStep<SubsystemInvestigationInput, LlmStringStepResult>> _investigationFactory;
    private readonly TimeSpan _defaultInvestigationTimeout;
    private readonly PreparationOptions? _options;
    private IReadOnlyDictionary<string, TimeSpan> _investigationTimeouts = new Dictionary<string, TimeSpan>();

    /// <param name="reportType">El informe que escribe (uno de los de modo B).</param>
    /// <param name="consolidation">El auditor de siempre: escribe el informe con las notas. Se invoca con el nombre <c>Auditor_&lt;tipo&gt;</c> que ya tiene.</param>
    /// <param name="investigationFactory">
    /// Crea el sub-step de investigación de un subsistema a partir de su identificador (con el nombre
    /// <see cref="AnalysisPipelineFactory.InvestigationStepName"/>); ver <see cref="CreateInvestigationStep"/>. Se llama una vez por investigación.
    /// </param>
    /// <param name="defaultInvestigationTimeout">
    /// Límite de una investigación a la que la tabla de <c>TimeBudgetStep</c> no da el suyo (o si el presupuesto no llegó a calcularse);
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> es sin límite.
    /// </param>
    /// <param name="options">Modo y paralelismo; null los lee de la configuración del contexto en cada ejecución.</param>
    /// <param name="degradedAuditor">
    /// El auditor que escribe el informe cuando no hay nada que investigar (sin plan, sin subsistemas elegidos o <c>Mode = Off</c>): sin
    /// notas, es el de siempre y no la consolidación, que puede llevar un tope de herramientas más corto
    /// (<see cref="PreparationOptions.ConsolidationMaxToolIterations"/>) porque las notas ya traen lo investigado. Null usa la propia consolidación.
    /// </param>
    public SubsystemInvestigationReportStep(
        ReportType reportType,
        CustomStatelessTemplateLlmStep<ReportFacetInput, SingleReportResult> consolidation,
        Func<string, CustomStatelessTemplateLlmStep<SubsystemInvestigationInput, LlmStringStepResult>> investigationFactory,
        TimeSpan defaultInvestigationTimeout,
        PreparationOptions? options = null,
        CustomStatelessTemplateLlmStep<ReportFacetInput, SingleReportResult>? degradedAuditor = null)
    {
        ArgumentNullException.ThrowIfNull(consolidation);
        ArgumentNullException.ThrowIfNull(investigationFactory);
        _reportType = reportType;
        _consolidation = consolidation;
        _degradedAuditor = degradedAuditor ?? consolidation;
        _investigationFactory = investigationFactory;
        _defaultInvestigationTimeout = defaultInvestigationTimeout;
        _options = options;
    }

    /// <summary>Sub-step de investigación de un subsistema para un informe, con el rol y las tools del auditor (<see cref="AnalysisPipelineFactory.AuditorToolNames"/>).</summary>
    /// <param name="templateProvider">De donde salen las plantillas (la carpeta <c>templates/</c> del análisis).</param>
    /// <param name="role">El rol LLM del informe: el mismo que el de su auditor.</param>
    /// <param name="templatePrefix">Prefijo de las plantillas del informe (<c>report_funcional</c>...): prompt <c>{prefijo}_investigacion_prompt</c> y, si el rol no fija otro, system <c>{prefijo}_system</c>.</param>
    /// <param name="type">El informe para el que investiga.</param>
    /// <param name="subsystemId">Identificador del subsistema (<c>Subsystem.Id</c>): va en el nombre del paso.</param>
    /// <param name="maxToolIterations">Tope de consultas a herramientas de la investigación (el del rol del informe).</param>
    /// <param name="timeout">Se guarda en el sub-step pero no corta nada: se invoca sin middleware y el límite real lo aplica este paso.</param>
    /// <param name="noteProtocol">Con el protocolo de notas (<see cref="NoteTagHandler"/>; <c>CodebaseAnalysis:NoteProtocol:Investigations</c>).</param>
    /// <param name="noteOptions">Umbrales de pendientes del protocolo de notas; null = los de por defecto.</param>
    public static CustomStatelessTemplateLlmStep<SubsystemInvestigationInput, LlmStringStepResult> CreateInvestigationStep(
        ITemplateProvider templateProvider, LlmRole role, string templatePrefix, ReportType type, string subsystemId, int maxToolIterations, TimeSpan timeout,
        bool noteProtocol = false, Notes.NoteProtocolOptions? noteOptions = null) =>
        new(
            name: AnalysisPipelineFactory.InvestigationStepName(type, subsystemId),
            role: role,
            templateProvider: templateProvider,
            promptTemplateName: $"{templatePrefix}_investigacion_prompt",
            systemPromptTemplateName: role.SystemPrompt ?? $"{templatePrefix}_system",
            tools: [.. AnalysisPipelineFactory.AuditorToolNames])
        {
            Timeout = timeout,
            MaxToolIterations = maxToolIterations,
            ToolLimitFinalAnswerPrompt = InvestigationToolLimitPrompt,
            TagHandlers = noteProtocol ? [NoteTagHandler.Instance] : [],
            NoteOptions = noteOptions
        };

    /// <summary>Tope de consultas a herramientas de la consolidación (la llamada que escribe el informe con las notas).</summary>
    public int ConsolidationMaxToolIterations => _consolidation.MaxToolIterations;

    /// <summary>Tope del auditor al que se degrada cuando no hay subsistemas que investigar (el del auditor de siempre).</summary>
    public int DegradedAuditorMaxToolIterations => _degradedAuditor.MaxToolIterations;

    public string Name => AnalysisPipelineFactory.AuditorStepName(_reportType);
    public IResume? Resume => null;

    /// <summary>Sin reintentos del paso entero: repetirlo volvería a lanzar todas las investigaciones. Los reintentos son los de cada sub-step.</summary>
    public int MaxRetries => 1;

    public int MiillisecondsBetweenRetries => 500;

    /// <summary>Tiempo del paso entero (investigaciones y consolidación); lo escribe <c>TimeBudgetStep</c> y lo aplica el motor.</summary>
    public TimeSpan? Timeout { get; set; }

    public Type InputType => typeof(ReportFacetInput);
    public Type OutputType => typeof(SingleReportResult);

    /// <summary>Los límites por subsistema que escribe <c>TimeBudgetStep</c>; se copia para que el llamante no pueda cambiarlos a mitad de ejecución.</summary>
    public void SetInvestigationTimeouts(IReadOnlyDictionary<string, TimeSpan> bySubsystemId)
    {
        ArgumentNullException.ThrowIfNull(bySubsystemId);
        _investigationTimeouts = new Dictionary<string, TimeSpan>(bySubsystemId, StringComparer.Ordinal);
    }

    public async Task<SingleReportResult> ExecuteAsync(
        ReportFacetInput input,
        PipelineContext context,
        int attempt,
        SingleReportResult? lastStepResult,
        CancellationToken cancellationToken)
    {
        var options = _options ?? PreparationOptions.FromServices(context.ServiceProvider);
        var coverage = options.Mode == PreparationMode.Off
            ? null
            : context.Get<CoveragePlan>(AnalysisContextKeys.CoveragePlan)?.Reports.FirstOrDefault(r => r.Report == _reportType);

        if (coverage is not { Chosen.Count: > 0 })
        {
            if (options.Mode != PreparationMode.Off)
            {
                context.Report(Name, AnalysisLogLevel.Info,
                    "ℹ️ No hay subsistemas elegidos para este informe: se escribe como un auditor normal, sin investigaciones por subsistema.");
            }
            return ToReport(await RunWithRetriesAsync(_degradedAuditor, input, context, cancellationToken));
        }

        var map = context.Get<DomainMap>(AnalysisContextKeys.DomainMap);
        var inventory = context.Get<RepositoryInventory>(AnalysisContextKeys.RepositoryInventory);
        var timeouts = _investigationTimeouts;
        var concurrency = Math.Max(options.MaxParallelInvestigations, 1);

        context.Report(Name, AnalysisLogLevel.Info,
            $"🔬 Investigando {coverage.Chosen.Count} subsistema(s) para este informe, hasta {concurrency} a la vez, y después consolidando...");

        using var gate = new SemaphoreSlim(concurrency);
        var tasks = coverage.Chosen
            .Select((selection, index) => InvestigateAsync(
                selection, map?.Subsystems.FirstOrDefault(s => s.Id == selection.SubsystemId), coverage, inventory,
                index + 1, coverage.Chosen.Count, timeouts, gate, context, cancellationToken))
            .ToArray();
        var outcomes = await Task.WhenAll(tasks);

        var covered = outcomes.Count(o => o.Covered);
        if (covered == 0)
        {
            var reasons = string.Join("; ", outcomes.Select(o => $"{o.Selection.Name}: {o.FailureReason ?? "sin respuesta utilizable"}"));
            var message = $"Fallaron las {outcomes.Length} investigaciones de subsistema de {Name}, sin notas no se puede consolidar el informe. {reasons}";
            context.Report(Name, AnalysisLogLevel.Error, $"❌ {message}");
            return new SingleReportResult(this, new StepError { Message = message });
        }

        if (covered < outcomes.Length)
        {
            context.Report(Name, AnalysisLogLevel.Warning,
                $"⚠️ {outcomes.Length - covered} de {outcomes.Length} subsistema(s) quedan «no cubiertos»; el informe se escribe con los {covered} investigados y lo declara en su apartado de cobertura.");
        }

        // Una copia: las ramas del paralelo comparten el input y estas notas son solo de este informe.
        var forReport = input.CloneForReport();
        forReport.InvestigationNotes = InvestigationTexts.Notes(outcomes);
        forReport.CoverageSummary = InvestigationTexts.Coverage(coverage, outcomes, inventory, map?.UnassignedFiles.Count ?? 0);

        context.Report(Name, AnalysisLogLevel.Info, $"🧩 Consolidando el informe con las notas de {covered} subsistema(s)...");
        return ToReport(await RunWithRetriesAsync(_consolidation, forReport, context, cancellationToken));
    }

    /// <summary>
    /// Una investigación, con su límite de tiempo y sin lanzar (salvo la cancelación del llamante): cualquier fallo es un
    /// <see cref="InvestigationOutcome"/> sin notas y con su motivo.
    /// </summary>
    private async Task<InvestigationOutcome> InvestigateAsync(
        CoverageSelection selection,
        Subsystem? subsystem,
        ReportCoverage coverage,
        RepositoryInventory? inventory,
        int position,
        int total,
        IReadOnlyDictionary<string, TimeSpan> timeouts,
        SemaphoreSlim gate,
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var limit = timeouts.TryGetValue(selection.SubsystemId, out var assigned) ? assigned : _defaultInvestigationTimeout;
            using var own = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (limit > TimeSpan.Zero && limit != System.Threading.Timeout.InfiniteTimeSpan)
                own.CancelAfter(limit);

            context.Report(Name, AnalysisLogLevel.Info, $"🔬 Investigando '{selection.Name}' ({position}/{total})...");
            var fail = (string reason) => Failed(context, selection, reason);
            try
            {
                var step = _investigationFactory(selection.SubsystemId);
                // Un contexto de rama por investigación: la clave del estado de un sub-step LLM es la ruta del paso, que sería la misma para todas.
                // Se guarda porque la sesión de notas de la investigación (si lleva el protocolo) es la de este contexto.
                var branch = context.CloneForBranch();
                var result = await RunWithRetriesAsync(
                    step, InvestigationTexts.BuildInput(selection, subsystem, coverage, inventory), branch, own.Token);

                if (result.HasError)
                    return fail($"la investigación falló: {result.Error?.Message}");

                var notes = WithSessionNotes(branch, (result as LlmStringStepResult)?.Content ?? result.Value?.ToString());
                if (string.IsNullOrWhiteSpace(notes))
                    return fail("la investigación respondió sin contenido");

                var truncated = result is ILlmStepResult { FinishReason: FinishReason.Length };
                context.Report(Name, truncated ? AnalysisLogLevel.Warning : AnalysisLogLevel.Success,
                    truncated
                        ? $"⚠️ La investigación de '{selection.Name}' se cortó al alcanzar el máximo de salida: sus notas están incompletas."
                        : $"✅ Investigado '{selection.Name}' ({position}/{total}).");
                await WriteNotesToGraphAsync(context, selection, notes, truncated, cancellationToken);
                return new InvestigationOutcome(selection, notes, null, truncated);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return fail(own.IsCancellationRequested
                    ? $"la investigación superó su límite de {limit.TotalMinutes.ToString("0.###", CultureInfo.InvariantCulture)} min"
                    : "la investigación se interrumpió");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return fail($"la investigación falló: {ex.Message}");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// E3: guarda las notas de la investigación en la copia del grafo (<see cref="Enrichment.AnalysisGraph.InvestigationNoteGraphWriter"/>),
    /// para que el chat las encuentre sin volver a investigar. Sin BD no hace nada; un fallo es un aviso y la investigación cuenta igual.
    /// </summary>
    private async Task WriteNotesToGraphAsync(PipelineContext context, CoverageSelection selection, string notes, bool truncated, CancellationToken ct)
    {
        var databasePath = context.Get<AnalysisPipelineInput>(AnalysisContextKeys.PipelineInput)?.DatabasePath is { Length: > 0 } db
            ? db
            : context.Get<string>(AnalysisContextKeys.CbmDbPath);
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            return;

        try
        {
            var stats = await Enrichment.AnalysisGraph.InvestigationNoteGraphWriter.WriteAsync(databasePath, new Enrichment.AnalysisGraph.InvestigationNoteRecord
            {
                Report = _reportType.ToString(),
                SubsystemId = selection.SubsystemId,
                SubsystemName = selection.Name,
                Text = notes,
                RunStartedAt = context.Get<DateTimeOffset?>(AnalysisContextKeys.PipelineStartedAt),
                Truncated = truncated
            }, ct);
            context.Report(Name, AnalysisLogLevel.Info,
                $"🗒️ Notas de '{selection.Name}' en el grafo: {stats.CitesEdges} nodo(s) citado(s); {stats.CitationsUnresolved} de {stats.Citations} cita(s) sin nodo.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Report(Name, AnalysisLogLevel.Warning, $"⚠️ No se pudieron guardar en el grafo las notas de '{selection.Name}': {ex.Message}.");
        }
    }

    /// <summary>
    /// Las notas de una investigación: con protocolo de notas, las de su sesión en orden de <c>Rn</c> (cada una tras su línea
    /// <c>[Rn]</c>, sin las «sin interés») y después la respuesta final; sin sesión, la respuesta tal cual. Sin los marcadores
    /// <c>[nota Rn guardada]</c>.
    /// </summary>
    internal static string? WithSessionNotes(PipelineContext branch, string? content)
    {
        if (!NoteSessions.TryGet(branch, out var session))
            return content;

        var sessionNotes = session.Notes
            .Where(n => !NoteTagHandler.IsEmptyNote(n.Value))
            .Select(n => $"[{n.Key}]\n{n.Value}")
            .ToList();
        var final = NoteTagHandler.RemovePlaceholders(content ?? "").Trim();
        if (final.Length > 0)
            sessionNotes.Add(final);
        return string.Join("\n\n", sessionNotes);
    }

    private InvestigationOutcome Failed(PipelineContext context, CoverageSelection selection, string reason)
    {
        context.Report(Name, AnalysisLogLevel.Warning, $"⚠️ No se pudo investigar '{selection.Name}': {reason}. Queda como «no cubierto».");
        return new InvestigationOutcome(selection, null, reason);
    }

    private SingleReportResult ToReport(IStepResult result) =>
        result as SingleReportResult
        ?? new SingleReportResult(this, result.Error ?? new StepError { Message = $"{Name}: el auditor no devolvió un informe." });

    /// <summary>
    /// Lo esencial de <c>RetryMiddleware</c> para un sub-step que se invoca directamente: reintenta mientras el error no traiga
    /// una excepción (el modelo puede corregirse con el mensaje de error), pide a <see cref="IFallbackable"/> otra estrategia ante un
    /// error de proveedor o el tope de herramientas, valida la forma del resultado y llama a <c>FinalizeAsync</c> al terminar. La
    /// cancelación se propaga (a diferencia del motor, que la trata como un fallo más) para que quien llama distinga su
    /// propio límite de la del llamante; el sub-step se finaliza igualmente.
    /// </summary>
    private static async Task<IStepResult> RunWithRetriesAsync(IStep step, IStepResult input, PipelineContext context, CancellationToken cancellationToken)
    {
        IStepResult? result = null;
        var attempt = 1;
        while (attempt <= step.MaxRetries)
        {
            try
            {
                result = await step.ExecuteAsync(input, context, attempt, result, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await FinalizeQuietlyAsync(step, ErrorStepResult.FromMessage(step, "cancelado"), context);
                throw;
            }
            catch (Exception ex)
            {
                var error = ErrorStepResult.FromException(step, ex);
                if (step is IFallbackable fallbackable && fallbackable.SwitchToNextFallback(error, context, attempt))
                {
                    attempt = 1;
                    result = null;
                    continue;
                }
                await FinalizeQuietlyAsync(step, error, context);
                return error;
            }

            if (result.HasError)
            {
                // Con excepción es un fallo del sistema (proveedor, tope de tools): no se reintenta, se cambia de estrategia si la hay.
                if (result.Error?.OriginalException != null)
                {
                    if (step is IFallbackable fallbackable && fallbackable.SwitchToNextFallback(result, context, attempt))
                    {
                        attempt = 1;
                        result = null;
                        continue;
                    }
                    await FinalizeQuietlyAsync(step, result, context);
                    return result;
                }

                attempt++;
                continue;
            }

            var (structuralValid, structuralError) = await result.ValidateAsync(context);
            if (!structuralValid)
            {
                result.Error = new StepError { Message = structuralError ?? "Structural validation failed" };
                attempt++;
                continue;
            }

            await FinalizeQuietlyAsync(step, result, context);
            return result;
        }

        var final = result ?? ErrorStepResult.FromMessage(step, $"Validation failed after {step.MaxRetries} attempts");
        await FinalizeQuietlyAsync(step, final, context);
        return final;
    }

    /// <summary>Finaliza el sub-step sin que un fallo de la limpieza (o la cancelación ya pedida) tape el resultado que se iba a devolver.</summary>
    private static async Task FinalizeQuietlyAsync(IStep step, IStepResult result, PipelineContext context)
    {
        try
        {
            await step.FinalizeAsync(result, context, CancellationToken.None);
        }
        catch (Exception)
        {
            // La limpieza de la conversación no debe cambiar el desenlace del informe.
        }
    }
}
