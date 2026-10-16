using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Support.Template;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;

namespace CodebaseAnalysis.Core.Steps;

/// <summary>
/// Paso 7 del pipeline de preparación (spec §4.3): el plan de cobertura. Lee el mapa de dominios del contexto
/// (<see cref="AnalysisContextKeys.DomainMap"/>), lo puntúa y reparte con <see cref="CoveragePlanner"/> (sin LLM), escribe
/// <c>reports/_coverage_plan.json</c>, deja el plan en el contexto (<see cref="AnalysisContextKeys.CoveragePlan"/>) y su versión
/// compacta en <see cref="ReportFacetInput.CoveragePlan"/>.
/// </summary>
/// <remarks>
/// Devuelve el mismo <see cref="ReportFacetInput"/> que recibe, con los campos nuevos rellenos. No falla duro: sin mapa deja un plan
/// vacío con un aviso (los informes en modo B degradan a un auditor normal), y si algo se rompe por dentro es un aviso; solo la
/// cancelación se propaga. El plan vacío también se escribe, para que no sobreviva el de una ejecución anterior, y sin la huella del
/// grafo, para que la regeneración no lo dé por bueno. Con <c>CodebaseAnalysis:Preparation:Mode = Off</c> no hace nada.
/// </remarks>
public sealed class CoveragePlanningStep : IStep<ReportFacetInput, ReportFacetInput>
{
    public const string StepName = "CoveragePlanningStep";

    private readonly ITemplateProvider? _templates;
    private readonly AuditorStepRegistry? _registry;

    /// <param name="templates">De donde se leen las preguntas de investigación de cada informe; null las deja vacías (con aviso).</param>
    /// <param name="registry">
    /// Los pasos de los informes de este pipeline: el plan es solo de los de modo B que se van a escribir (con <c>--reports deadcode</c>,
    /// de ninguno). Null planifica los cuatro informes de modo B.
    /// </param>
    public CoveragePlanningStep(ITemplateProvider? templates = null, AuditorStepRegistry? registry = null)
    {
        _templates = templates;
        _registry = registry;
    }

    public string Name => StepName;
    public IResume? Resume => null;
    public int MaxRetries => 1;
    public int MiillisecondsBetweenRetries => 500;
    public TimeSpan? Timeout => TimeSpan.FromMinutes(5);
    public Type InputType => typeof(ReportFacetInput);
    public Type OutputType => typeof(ReportFacetInput);

    public Task<ReportFacetInput> ExecuteAsync(
        ReportFacetInput input,
        PipelineContext context,
        int attempt,
        ReportFacetInput? lastStepResult,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var options = PreparationOptions.FromServices(context.ServiceProvider);
        if (options.Mode == PreparationMode.Off)
            return Task.FromResult(input);

        context.Report(StepName, AnalysisLogLevel.Info, "🎯 Planificando la cobertura de los informes por subsistema...");
        try
        {
            var map = context.Get<DomainMap>(AnalysisContextKeys.DomainMap);
            var plan = map == null
                ? CoveragePlanner.Empty(options,
                    "⚠️ No hay mapa de dominios en el contexto: el plan de cobertura queda vacío y los informes en modo B trabajan como un auditor normal.",
                    DateTimeOffset.UtcNow)
                : CoveragePlanner.Plan(map, options, _templates, DateTimeOffset.UtcNow, _registry?.ModeBReports);

            var reportsPath = input.OutputReportsPath is { Length: > 0 } fromInput
                ? fromInput
                : context.Get<AnalysisPipelineInput>(AnalysisContextKeys.PipelineInput)?.OutputReportsPath ?? "";
            // En disco, además, lo elegido antes para los informes que esta ejecución no escribe (una regeneración de uno solo).
            var onDisk = reportsPath.Length > 0
                ? CoveragePlanner.WithCarriedOverReports(plan, PreparationArtifacts.TryRead<CoveragePlan>(reportsPath, out _))
                : plan;
            if (PreparationArtifacts.TryWrite(reportsPath, onDisk) is { } writeWarning)
                context.Report(StepName, AnalysisLogLevel.Warning, writeWarning);

            context.Set(AnalysisContextKeys.CoveragePlan, plan);
            input.CoveragePlan = CoveragePlanSummary.ToJson(plan);

            foreach (var warning in plan.Warnings)
                context.Report(StepName, AnalysisLogLevel.Warning, warning);
            if (plan.Reports.Count > 0)
            {
                var first = plan.Reports[0];
                context.Report(StepName, AnalysisLogLevel.Info,
                    $"🎯 Plan de cobertura: {plan.Reports.Count} informe(s) en modo B, {first.Chosen.Count} subsistema(s) elegido(s) de {plan.Scores.Count} " +
                    $"(K = {plan.MaxSubsystemsPerReport}), {first.Excluded.Count} excluido(s), cola larga de {first.LongTail.Count}.");
            }
            else if (map != null)
            {
                context.Report(StepName, AnalysisLogLevel.Info, "🎯 Plan de cobertura: ningún informe de los pedidos investiga por subsistema (modo B).");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Report(StepName, AnalysisLogLevel.Warning,
                $"⚠️ No se pudo construir el plan de cobertura: {ex.Message}. Los informes siguen sin él.");
        }

        return Task.FromResult(input);
    }
}
