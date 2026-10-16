using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;

namespace CodebaseAnalysis.Core.Steps;

/// <summary>
/// Paso 8 del pipeline de preparación (spec §4.4, D6): el presupuesto de tiempo dinámico. Lee el plan de cobertura, el inventario y el
/// mapa de dominios del contexto, calcula con <see cref="TimeBudgetCalculator"/> el tiempo de cada pieza según el tamaño del código y
/// lo <b>escribe directamente en la propiedad <c>Timeout</c></b> de cada paso del <see cref="AuditorStepRegistry"/> (el auditor en modo A
/// o el envoltorio de modo B), más la tabla de límites de las investigaciones en los envoltorios. Escribe <c>reports/_time_budget.json</c>
/// y deja el presupuesto en el contexto (<see cref="AnalysisContextKeys.TimeBudgetPlan"/>).
/// </summary>
/// <remarks>
/// <para>Si la suma de límites, dividida por la concurrencia efectiva, supera el techo del pipeline
/// (<c>CodebaseAnalysis:Timeouts:PipelineMinutes</c>; 0 = sin techo), recorta subsistemas de los informes en modo B y avisa. Entonces el
/// plan resultante sustituye al del contexto, se reescribe <c>_coverage_plan.json</c> y se rehace <see cref="ReportFacetInput.CoveragePlan"/>,
/// para que los informes y el validador de cobertura vean lo que de verdad se investiga.</para>
/// <para>Concurrencia efectiva: las investigaciones en paralelo de un informe (<c>MaxParallelInvestigations</c>) por los informes en
/// modo B que corren a la vez (todos los registrados, con el tope <c>CodebaseAnalysis:Reports:MaxParallelAuditors</c> si lo hay), y, si hay
/// límite de llamadas LLM simultáneas (<c>MaxConcurrentRequests</c>, el que dio la factoría), el menor de los dos: no se pueden hacer
/// más llamadas a la vez de las que el limitador deja. Las que no caben esperan turno, y como el <c>Timeout</c> de un paso es de reloj,
/// todos los límites suben por <see cref="TimeBudgetInput.QueueFactor"/> (concurrencia deseada entre efectiva); nunca bajan.</para>
/// <para>Devuelve el mismo <see cref="ReportFacetInput"/> que recibe. No falla duro: sin plan avisa y los pasos conservan el límite que
/// les puso la factoría; un paso que no deja asignar su <c>Timeout</c> es un aviso. Solo la cancelación se propaga. Con
/// <c>CodebaseAnalysis:Preparation:Mode = Off</c> no hace nada. Los pasos de la factoría se construyen en cada ejecución, así que
/// escribir su <c>Timeout</c> no se comparte entre ejecuciones.</para>
/// </remarks>
public sealed class TimeBudgetStep : IStep<ReportFacetInput, ReportFacetInput>
{
    public const string StepName = "TimeBudgetStep";

    /// <summary>Holgura que se suma al tiempo presupuestado del paralelo de informes: arranque, escritura de informes y cierre de cada rama.</summary>
    public static readonly TimeSpan ParallelMargin = TimeSpan.FromMinutes(5);

    private readonly AuditorStepRegistry _registry;

    /// <param name="registry">Los pasos de los informes de este pipeline, en los que se escriben los límites.</param>
    /// <param name="callLimit">
    /// Llamadas LLM simultáneas que deja el limitador del proveedor (<c>MaxConcurrentRequests</c>); null o 0 o menos = sin límite, y la
    /// concurrencia efectiva es la que pide el pipeline.
    /// </param>
    public TimeBudgetStep(AuditorStepRegistry registry, int? callLimit = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        CallLimit = callLimit is > 0 ? callLimit : null;
    }

    /// <summary>El límite de llamadas simultáneas con que se calcula la concurrencia efectiva; null si no hay.</summary>
    public int? CallLimit { get; }

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

        var coefficients = TimeBudgetOptions.FromServices(context.ServiceProvider);
        foreach (var warning in coefficients.Warnings)
            context.Report(StepName, AnalysisLogLevel.Warning, $"⚠️ {warning}");

        var plan = context.Get<CoveragePlan>(AnalysisContextKeys.CoveragePlan);
        if (plan == null)
        {
            context.Report(StepName, AnalysisLogLevel.Warning,
                "⚠️ No hay plan de cobertura en el contexto: no se calcula el presupuesto de tiempo y los informes conservan sus límites por defecto.");
            return Task.FromResult(input);
        }

        context.Report(StepName, AnalysisLogLevel.Info, "⏱️ Calculando el presupuesto de tiempo de los informes según el tamaño del código...");
        try
        {
            Apply(input, context, options, coefficients, plan);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Report(StepName, AnalysisLogLevel.Warning,
                $"⚠️ No se pudo calcular el presupuesto de tiempo: {ex.Message}. Los informes conservan sus límites por defecto.");
        }

        return Task.FromResult(input);
    }

    private void Apply(ReportFacetInput input, PipelineContext context, PreparationOptions options, TimeBudgetOptions coefficients, CoveragePlan plan)
    {
        var pipelineInput = context.Get<AnalysisPipelineInput>(AnalysisContextKeys.PipelineInput);
        var reportsPath = input.OutputReportsPath is { Length: > 0 } fromInput ? fromInput : pipelineInput?.OutputReportsPath ?? "";

        var totalLoc = TotalProductionLoc(context, plan);
        var modeB = _registry.ModeBReports;
        var timeouts = AnalysisTimeouts.FromServices(context.ServiceProvider);
        var ceiling = timeouts.PipelineMinutes > 0 && timeouts.PipelineMinutes <= AnalysisTimeouts.MaxMinutes ? timeouts.PipelineMinutes : 0;

        var parallelReports = modeB.Count;
        if (pipelineInput is { MaxParallelAuditors: > 0 } && pipelineInput.MaxParallelAuditors < parallelReports)
            parallelReports = pipelineInput.MaxParallelAuditors;
        var wanted = Math.Max(options.MaxParallelInvestigations, 1) * Math.Max(parallelReports, 1);
        var concurrency = CallLimit is { } limit ? Math.Min(wanted, limit) : wanted;

        var result = TimeBudgetCalculator.Calculate(new TimeBudgetInput
        {
            Plan = plan,
            TotalProductionLoc = totalLoc,
            ModeBReports = modeB,
            ModeAReports = _registry.ModeAReports,
            Coefficients = coefficients,
            PipelineCeilingMinutes = ceiling,
            EffectiveConcurrency = concurrency,
            QueueFactor = (double)wanted / concurrency,
            MaxParallelInvestigations = options.MaxParallelInvestigations,
            GeneratedAt = DateTimeOffset.UtcNow
        });
        var budget = result.Budget;

        // El Timeout directamente en los pasos, y la tabla de límites de las investigaciones en los envoltorios de modo B.
        foreach (var report in budget.ReportTimeouts)
        {
            if (!_registry.TrySetTimeout(report.Report, TimeBudgetCalculator.ToTimeSpan(report.Minutes)))
            {
                context.Report(StepName, AnalysisLogLevel.Warning,
                    $"⚠️ El paso del informe {report.Report} no deja asignar su Timeout: conserva el que tenía.");
            }
            if (_registry.IsModeB(report.Report))
                _registry.TrySetInvestigationTimeouts(report.Report, TimeBudgetCalculator.InvestigationTimeouts(budget, report.Report));
        }

        RaiseReportsParallelTimeout(context, budget);

        if (PreparationArtifacts.TryWrite(reportsPath, budget) is { } budgetWarning)
            context.Report(StepName, AnalysisLogLevel.Warning, budgetWarning);
        context.Set(AnalysisContextKeys.TimeBudgetPlan, budget);

        // Un recorte cambia lo que los informes investigan: el plan de cobertura del contexto, del disco y del input pasa a ser el recortado.
        if (budget.Trimmed.Count > 0)
        {
            context.Set(AnalysisContextKeys.CoveragePlan, result.EffectivePlan);
            input.CoveragePlan = CoveragePlanSummary.ToJson(result.EffectivePlan);
            // En disco conserva, además, lo elegido para los informes que esta ejecución no escribe (el paso de plan ya lo dejó ahí).
            var onDisk = reportsPath.Length > 0
                ? CoveragePlanner.WithCarriedOverReports(result.EffectivePlan, PreparationArtifacts.TryRead<CoveragePlan>(reportsPath, out _))
                : result.EffectivePlan;
            if (PreparationArtifacts.TryWrite(reportsPath, onDisk) is { } planWarning)
                context.Report(StepName, AnalysisLogLevel.Warning, planWarning);
        }

        foreach (var warning in budget.Warnings)
            context.Report(StepName, AnalysisLogLevel.Warning, warning);
        context.Report(StepName, AnalysisLogLevel.Info,
            $"⏱️ Presupuesto de tiempo: {budget.Entries.Count} pieza(s), {budget.TotalMinutes:0.#} min en total " +
            $"({budget.TotalMinutes / (budget.EffectiveConcurrency * budget.QueueFactor):0.#} min con concurrencia {budget.EffectiveConcurrency}" +
            $"{(budget.QueueFactor > 1 ? $" (límite de llamadas LLM; los plazos suben ×{budget.QueueFactor:0.##} por la cola)" : "")}; techo " +
            $"{(ceiling > 0 ? $"{ceiling} min" : "sin límite")}), {budget.Trimmed.Count} subsistema(s) recortado(s).");
    }

    /// <summary>
    /// El paralelo de informes tiene su propio <c>Timeout</c> (<c>CodebaseAnalysis:Timeouts:ReportsMinutes</c>, 60 min por defecto) y
    /// corta a todas sus ramas cuando vence: con K subsistemas por informe, el envoltorio de modo B puede necesitar bastante más que
    /// eso y el paralelo lo mataría antes que a su propio límite. Si el del paralelo es finito, se sube a lo presupuestado
    /// (las tandas de informes que caben a la vez por el más largo, más <see cref="ParallelMargin"/>) y se avisa; nunca se baja. Sin
    /// límite se queda sin límite, y si alguna rama queda sin límite el paralelo también (no puede acabar antes que ella). El límite
    /// global del pipeline no se toca aquí.
    /// </summary>
    private void RaiseReportsParallelTimeout(PipelineContext context, TimeBudgetPlan budget)
    {
        if (_registry.ReportsParallel is not { } parallel)
            return;
        if (parallel.Timeout is not { } current || current <= TimeSpan.Zero || current == System.Threading.Timeout.InfiniteTimeSpan)
            return;
        if (budget.ReportTimeouts.Count == 0)
            return;

        var perReport = budget.ReportTimeouts.Select(t => TimeBudgetCalculator.ToTimeSpan(t.Minutes)).ToList();
        TimeSpan needed;
        if (perReport.Contains(System.Threading.Timeout.InfiniteTimeSpan))
        {
            needed = System.Threading.Timeout.InfiniteTimeSpan;
        }
        else
        {
            var atOnce = parallel.MaxDegreeOfParallelism > 0 ? parallel.MaxDegreeOfParallelism : Environment.ProcessorCount;
            var waves = (Math.Max(parallel.Branches.Count, 1) + atOnce - 1) / atOnce;
            needed = TimeBudgetCalculator.ToTimeSpan((perReport.Max() * waves + ParallelMargin).TotalMinutes);
        }

        if (needed != System.Threading.Timeout.InfiniteTimeSpan && needed <= current)
            return;
        if (!_registry.TrySetReportsParallelTimeout(needed))
            return;

        var limitKey = $"{AnalysisTimeouts.SectionName}:{nameof(AnalysisTimeouts.ReportsMinutes)}";
        context.Report(StepName, AnalysisLogLevel.Warning,
            needed == System.Threading.Timeout.InfiniteTimeSpan
                ? $"⚠️ Algún informe quedó sin límite de tiempo: el paralelo de informes ({current.TotalMinutes:0.###} min, {limitKey}) pasa a no tener límite para no cortarlo antes."
                : $"⚠️ El paralelo de informes tenía {current.TotalMinutes:0.###} min ({limitKey}) y lo presupuestado para sus informes es mayor: sube a {needed.TotalMinutes:0.###} min para no cortarlos antes que sus propios límites. Sube {limitKey} para que el ajuste no haga falta.");
    }

    /// <summary>Líneas de producción del repositorio: las del inventario; sin él, la suma de las del mapa; sin ninguno, la de lo elegido en el plan.</summary>
    private static long TotalProductionLoc(PipelineContext context, CoveragePlan plan)
    {
        if (context.Get<RepositoryInventory>(AnalysisContextKeys.RepositoryInventory) is { } inventory)
            return inventory.ProductionLoc.Total;
        if (context.Get<DomainMap>(AnalysisContextKeys.DomainMap) is { } map)
            return map.Subsystems.Sum(s => s.Metrics.Loc);
        return plan.Reports.Select(r => r.Chosen.Sum(c => c.Loc)).DefaultIfEmpty(0).Max();
    }
}
