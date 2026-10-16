using System.Globalization;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.Support.Template;
using CodebaseAnalysis.Core.Enrichment.AnalysisGraph;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.SharedPass;
using CodebaseAnalysis.Core.Support;

namespace CodebaseAnalysis.Core.Steps;

/// <summary>
/// La pasada compartida del plan 2026-10-16 (paquete D), entre la preparación y los informes y solo con
/// <c>CodebaseAnalysis:SharedPass:Enabled = true</c>: lotes y paquetes por subsistema del mapa de dominios (D1), lectura de cada lote
/// con una llamada al LLM (D2, <see cref="BatchReader"/>), consolidación determinista con severidad por rúbrica
/// (<see cref="FindingConsolidator"/>) y revisión del juez (<see cref="FindingJudge"/>), y persistencia
/// (<see cref="SharedPassRegistry"/>: el grafo y <see cref="SharedPassRegistry.FileName"/> en la carpeta de informes). Deja el registro en
/// el contexto (<see cref="AnalysisContextKeys.SharedPassRegistry"/>).
/// </summary>
/// <remarks>
/// Devuelve el mismo <see cref="ReportFacetInput"/> que recibe: los informes no cambian en esta tanda. No falla duro: sin mapa de
/// dominios (preparación apagada o fallida) es un aviso; con un rol cuyo modelo no tiene precio, también (sin precio no hay tope de
/// coste) y la pasada no se ejecuta; un error por dentro también, y el análisis sigue sin registro. Solo la cancelación del llamante
/// se propaga. Los topes de <see cref="SharedPassOptions"/> (coste, llamadas, tokens y el plazo <see cref="SharedPassOptions.MaxMinutes"/>)
/// son uno para la ejecución entera (lectura y juez); al vencer el plazo se guarda lo que haya, con aviso. El paso no tiene
/// <see cref="Timeout"/> del motor: uno que venciera pararía el pipeline y perdería lo gastado.
/// </remarks>
public sealed class SharedPassStep : IStep<ReportFacetInput, ReportFacetInput>
{
    public const string StepName = "SharedPassStep";

    private readonly LlmRole _readerRole;
    private readonly LlmRole _judgeRole;
    private readonly ITemplateProvider _templates;
    private readonly SharedPassOptions _options;

    public SharedPassStep(LlmRole readerRole, LlmRole judgeRole, ITemplateProvider templates, SharedPassOptions options)
    {
        _readerRole = readerRole;
        _judgeRole = judgeRole;
        _templates = templates;
        _options = options;
    }

    public string Name => StepName;
    public IResume? Resume => null;

    /// <summary>Sin reintentos del motor: repetir la pasada repetiría su gasto.</summary>
    public int MaxRetries => 1;

    public int MiillisecondsBetweenRetries => 500;

    /// <summary>
    /// Sin límite del motor (infinito, no null: null sería el de por defecto del pipeline). El plazo es
    /// <see cref="SharedPassOptions.MaxMinutes"/>, dentro del paso, que guarda lo parcial en vez de parar el análisis.
    /// </summary>
    public TimeSpan? Timeout { get; init; } = System.Threading.Timeout.InfiniteTimeSpan;

    public Type InputType => typeof(ReportFacetInput);
    public Type OutputType => typeof(ReportFacetInput);

    public SharedPassOptions Options => _options;

    public async Task<ReportFacetInput> ExecuteAsync(
        ReportFacetInput input, PipelineContext context, int attempt, ReportFacetInput? lastStepResult, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled)
            return input;

        var map = context.Get<DomainMap>(AnalysisContextKeys.DomainMap);
        if (map == null || map.Subsystems.Count == 0)
        {
            context.Report(StepName, AnalysisLogLevel.Warning,
                "⚠️ Pasada compartida activada pero no hay mapa de dominios (CodebaseAnalysis:Preparation:Mode = Subsystems lo calcula): no se ejecuta.");
            return input;
        }

        foreach (var (what, role) in new[] { ("lector", _readerRole), ("juez", _judgeRole) })
        {
            if (!SharedPassCostBudget.IsPriced(role))
            {
                context.Report(StepName, AnalysisLogLevel.Warning,
                    $"⚠️ Pasada compartida activada pero el modelo '{role.Model}' del {what} no tiene precio (InputPricePerMillion y " +
                    "OutputPricePerMillion en los Models de su proveedor): sin precio no hay tope de coste, así que no se ejecuta.");
                return input;
            }
        }

        var pipelineInput = context.Get<AnalysisPipelineInput>(AnalysisContextKeys.PipelineInput);
        var repository = input.RepositoryPath is { Length: > 0 } repo ? repo : pipelineInput?.RepositoryPath ?? "";
        var database = input.DatabasePath is { Length: > 0 } db ? db : pipelineInput?.DatabasePath ?? "";
        var reports = input.OutputReportsPath is { Length: > 0 } rp ? rp : pipelineInput?.OutputReportsPath ?? "";

        try
        {
            var registry = await RunAsync(repository, database, reports, map, context, cancellationToken);
            context.Set(AnalysisContextKeys.SharedPassRegistry, registry);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Una cancelación que no es del llamante (un límite interno que se escapa) tampoco para el análisis.
            context.Report(StepName, AnalysisLogLevel.Warning, $"⚠️ La pasada compartida falló: {ex.Message}. Los informes siguen sin su registro.");
        }
        return input;
    }

    /// <summary>D1 → D2 → D3 sobre <paramref name="map"/>; escribe el grafo y el fichero del registro y devuelve el registro.</summary>
    /// <remarks>Cada cambio de estado sale además como <c>sharedpass.progress</c> (<see cref="SharedPassProgress"/>); si la pasada falla
    /// por dentro, el último dice <c>failed</c> con el error.</remarks>
    public async Task<ConsolidatedRegistry> RunAsync(
        string repositoryPath, string databasePath, string reportsPath, DomainMap map, PipelineContext context, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        using var budget = new SharedPassCostBudget(_options);
        var progress = new SharedPassProgress(context, StepName, _options, budget, startedAt);
        try
        {
            return await RunCoreAsync(repositoryPath, databasePath, reportsPath, map, context, startedAt, budget, progress, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            progress.Fail(ex.Message);
            throw;
        }
    }

    private async Task<ConsolidatedRegistry> RunCoreAsync(
        string repositoryPath, string databasePath, string reportsPath, DomainMap map, PipelineContext context,
        DateTimeOffset startedAt, SharedPassCostBudget budget, SharedPassProgress progress, CancellationToken ct)
    {
        context.Report(StepName, AnalysisLogLevel.Info, string.Format(CultureInfo.InvariantCulture,
            "🔁 Pasada compartida: {0} subsistema(s); topes {1} USD por lote, {2} por etapa y {3} por ejecución, {4} llamadas, {5} tokens y {6} min.",
            map.Subsystems.Count, _options.MaxCostPerBatchUsd, _options.MaxCostPerStageUsd, _options.MaxCostPerRunUsd,
            _options.MaxCallsPerRun, _options.MaxTokensPerRun, _options.MaxMinutes));

        var read = await BatchReader.RunAsync(databasePath, repositoryPath, map, _options, _readerRole, _templates, context, ct, budget, progress);
        foreach (var warning in read.Warnings)
            context.Report(StepName, AnalysisLogLevel.Warning, warning.StartsWith("⚠️", StringComparison.Ordinal) ? warning : $"⚠️ {warning}");

        progress.Phase(SharedPassProgress.Phases.Consolidating);
        var graph = await SharedPassGraph.LoadAsync(databasePath, ct);
        var batches = ConsolidationBatch.From(read);
        var consolidated = FindingConsolidator.Consolidate(batches, graph);
        context.Report(StepName, AnalysisLogLevel.Info,
            $"🧮 Consolidación: {consolidated.MergedFrom} hallazgos de lote en {consolidated.Entries.Count} registros; severidad de la rúbrica " +
            Severities(consolidated.Entries.Select(e => e.Rubric.Severity)) + ".");

        var judge = await new FindingJudge(_judgeRole, _templates, repositoryPath, _options).JudgeAsync(consolidated.Entries, context, ct, budget, progress);
        foreach (var warning in judge.Warnings)
            context.Report(FindingJudge.StepName, AnalysisLogLevel.Warning, $"⚠️ {warning}");
        var registry = consolidated with { Entries = FindingConsolidator.AssignIds(judge.Entries) };
        if (judge.StopReason != null)
            registry = registry with { Notes = [.. registry.Notes, $"El juez se cortó ({judge.StopReason}): lo no revisado queda con la rúbrica y reverified = false."] };
        if (read.StopReason != null)
            registry = registry with { Notes = [.. registry.Notes, $"La lectura se cortó ({read.StopReason}): lo no leído queda en los huecos de cobertura."] };
        if (budget.DeadlineExpired)
            context.Report(StepName, AnalysisLogLevel.Warning,
                $"⚠️ Pasada compartida: {budget.DeadlineReason}; se guarda lo leído y juzgado hasta ahora y el análisis sigue con los informes.");
        if (budget.UnpricedCalls > 0)
            context.Report(StepName, AnalysisLogLevel.Warning,
                $"⚠️ Pasada compartida: {budget.UnpricedCalls} llamada(s) sin coste conocido; el gasto es un mínimo (los topes de llamadas y tokens sí las cuentan).");

        progress.Phase(SharedPassProgress.Phases.Saving);
        var run = new LlmRunRecord
        {
            RunId = SharedPassRegistry.NewRunId(startedAt),
            StartedAt = startedAt,
            Scope = SharedPassRegistry.Scope,
            Producer = SharedPassRegistry.Producer,
            Models = [.. new[] { _readerRole.Model, _judgeRole.Model }.Where(m => !string.IsNullOrEmpty(m)).Distinct(StringComparer.Ordinal)],
            PromptVersions = SharedPassRegistry.PromptVersions(_templates,
            [
                _readerRole.SystemPrompt ?? BatchReader.SystemTemplate, BatchReader.PromptTemplate, BatchReader.RetryPromptTemplate,
                _judgeRole.SystemPrompt ?? FindingJudge.SystemTemplate, FindingJudge.PromptTemplate
            ])
        };

        if (!string.IsNullOrWhiteSpace(databasePath) && File.Exists(databasePath))
        {
            try
            {
                var record = SharedPassRegistry.BuildRecord(registry, batches, run, map.Subsystems.ToDictionary(s => s.Id, s => s.Name, StringComparer.Ordinal));
                var stats = await LlmRegistryGraphWriter.WriteAsync(databasePath, record, ct);
                context.Report(StepName, AnalysisLogLevel.Info,
                    $"🧩 Registro en el grafo (Run {run.RunId}): {stats.Findings} hallazgos, {stats.Anchors} anclas, {stats.Summaries} resúmenes, " +
                    $"{stats.ContractsWritten} contratos ({stats.ContractsUnresolved} sin nodo), {stats.StaleMarked} nodos de pasadas anteriores marcados stale.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                context.Report(StepName, AnalysisLogLevel.Warning, $"⚠️ No se pudo escribir el registro en el grafo: {ex.Message}. Queda solo en disco.");
            }
        }

        var json = SharedPassRegistry.ToJson(registry, new SharedPassRegistry.RunSummary
        {
            RunId = run.RunId,
            Batches = read.Batches.Count,
            ReaderCalls = read.Calls,
            ReaderCostUsd = read.CostUsd,
            CoverageGaps = read.Gaps.Count(),
            RejectedByAnchor = read.Rejected.Count(),
            Judge = judge,
            StopReason = read.StopReason ?? judge.StopReason
        });
        if (SharedPassRegistry.TryWrite(reportsPath, json) is { } writeWarning)
            context.Report(StepName, AnalysisLogLevel.Warning, writeWarning);

        var confirmed = registry.Entries.Where(e => e.Status != RegistryEntry.StatusRejected).ToList();
        context.Report(StepName, read.StoppedByCostLimit || judge.StopReason != null ? AnalysisLogLevel.Warning : AnalysisLogLevel.Success, string.Format(CultureInfo.InvariantCulture,
            "✅ Pasada compartida: {0} registros ({1} rechazados por el juez), {2}; {3} revisados por el juez; {4:0.###} USD en total{5}.",
            registry.Entries.Count, registry.Entries.Count - confirmed.Count, Severities(confirmed.Select(e => e.Severity)), judge.Reviewed,
            budget.RunSpentUsd, (read.StopReason ?? judge.StopReason) is { } stop ? $" (cortada: {stop})" : ""));
        progress.Finish(read.StopReason ?? judge.StopReason ?? (budget.DeadlineExpired ? budget.DeadlineReason : null),
            SeverityRubric.Severities.ToDictionary(s => s, s => confirmed.Count(e => e.Severity == s), StringComparer.Ordinal),
            registry.Entries.Count, registry.Entries.Count - confirmed.Count);
        return registry;
    }

    private static string Severities(IEnumerable<string?> severities)
    {
        var list = severities.ToList();
        return string.Join(", ", SeverityRubric.Severities.Select(s => $"{s} {list.Count(x => x == s)}"));
    }
}
