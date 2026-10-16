using System.Text.Json;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tests.Enrichment;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="TimeBudgetStep"/> (T17, criterio A4): calcula el presupuesto del plan de cobertura y lo escribe en el <c>Timeout</c> de
/// los pasos del registro y en la tabla de los envoltorios de modo B; escribe <c>_time_budget.json</c>; recorta y avisa si pasa del
/// techo; y el valor escrito es el que aplica <c>TimeoutMiddleware</c> en el motor real del framework.
/// </summary>
public sealed class TimeBudgetStepTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string ReportsPath => Path.Combine(_temp.Path, "repo", "reports");

    /// <summary>Auditor en modo A: un <c>StepBase</c> como los de la factoría, con el límite por defecto de 15 min.</summary>
    private sealed class SleepingStep(string name, TimeSpan sleep) : TypedStep<ReportFacetInput, ReportFacetInput>(name)
    {
        public int Runs { get; private set; }

        protected override async Task<ReportFacetInput> ExecuteAsync(
            ReportFacetInput input, PipelineContext context, int Attempt, ReportFacetInput? LastStepResult, CancellationToken cancellationToken)
        {
            Runs++;
            await Task.Delay(sleep, cancellationToken);
            return input;
        }
    }

    /// <summary>Envoltorio de modo B de mentira, con <c>Timeout { get; set; }</c> y la tabla de límites.</summary>
    private sealed class FakeWrapper(string name) : IStep, IWritableTimeout, IInvestigationTimeouts
    {
        public string Name { get; } = name;
        public IResume? Resume => null;
        public TimeSpan? Timeout { get; set; } = TimeSpan.FromMinutes(15);
        public Type InputType => typeof(ReportFacetInput);
        public Type OutputType => typeof(SingleReportResult);
        public IReadOnlyDictionary<string, TimeSpan> Table { get; private set; } = new Dictionary<string, TimeSpan>();

        public void SetInvestigationTimeouts(IReadOnlyDictionary<string, TimeSpan> bySubsystemId) => Table = bySubsystemId;

        public Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ReadOnlyStep : IStep
    {
        public string Name => "ReadOnly";
        public IResume? Resume => null;
        public Type InputType => typeof(ReportFacetInput);
        public Type OutputType => typeof(ReportFacetInput);

        public Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static Subsystem Sub(string id, long loc, int tables) =>
        new()
        {
            Id = id, Name = char.ToUpperInvariant(id[0]) + id[1..], Files = [$"src/{id}/A.java"],
            Metrics = new SubsystemMetrics { Files = 1, Loc = loc, Tables = tables }
        };

    private static readonly DomainMap Map = new()
    {
        GraphFingerprint = "fp-1",
        Subsystems = [Sub("alpha", 100_000, 9), Sub("beta", 40_000, 5), Sub("gamma", 10_000, 1)]
    };

    private static CoveragePlan PlanOf(DomainMap map, int k = 8) =>
        CoveragePlanner.Plan(map, new PreparationOptions { MaxSubsystemsPerReport = k }, AnalysisPipelineFactory.CreateDefaultTemplateProvider(), DateTimeOffset.UtcNow);

    private (PipelineContext Context, CapturingReporter Reporter) NewContext(
        CoveragePlan? plan, DomainMap? map = null, long? totalLoc = null, AnalysisPipelineInput? pipelineInput = null, params (string, string?)[] config)
    {
        var reporter = new CapturingReporter();
        var context = EnrichmentStepContext.Create(TestConfig.FromPairs(config), reporter, Path.Combine(_temp.Path, "repo"), Path.Combine(_temp.Path, "none.db"));
        if (pipelineInput != null)
        {
            pipelineInput.OutputReportsPath = ReportsPath;
            context.Set(AnalysisContextKeys.PipelineInput, pipelineInput);
        }
        if (plan != null)
            context.Set(AnalysisContextKeys.CoveragePlan, plan);
        if (map != null)
            context.Set(AnalysisContextKeys.DomainMap, map);
        if (totalLoc != null)
            context.Set(AnalysisContextKeys.RepositoryInventory, new RepositoryInventory { ProductionLoc = new ProductionLines { Total = totalLoc.Value } });
        return (context, reporter);
    }

    private static Task<ReportFacetInput> RunAsync(TimeBudgetStep step, PipelineContext context, ReportFacetInput? input = null, CancellationToken ct = default) =>
        step.ExecuteAsync(input ?? new ReportFacetInput(), context, 1, null, ct);

    private TimeBudgetPlan ReadBudget()
    {
        var budget = PreparationArtifacts.TryRead<TimeBudgetPlan>(ReportsPath, out var warning);
        Assert.Null(warning);
        return budget!;
    }

    private static AuditorStepRegistry FullRegistry(out Dictionary<ReportType, IStep> steps)
    {
        var registry = new AuditorStepRegistry();
        steps = [];
        foreach (var report in CoveragePlanner.ModeBReports)
            steps[report] = new FakeWrapper($"Auditor_{report}");
        foreach (var report in new[] { ReportType.CodeSmells, ReportType.CodigoMuerto, ReportType.InventarioApisSeguridad, ReportType.DeudaTecnicaHotspots, ReportType.InyeccionDependenciasExtensiones })
            steps[report] = new SleepingStep($"Auditor_{report}", TimeSpan.Zero) { Timeout = TimeSpan.FromMinutes(15) };
        foreach (var (report, step) in steps)
            registry.Register(report, step);
        return registry;
    }

    [Fact]
    public async Task It_writes_the_calculated_timeouts_into_every_registered_step_and_the_table_into_the_mode_b_wrappers()
    {
        var registry = FullRegistry(out var steps);
        var (context, reporter) = NewContext(PlanOf(Map), Map, totalLoc: 150_000);
        var input = new ReportFacetInput();

        var result = await RunAsync(new TimeBudgetStep(registry), context, input);

        Assert.Same(input, result);
        var budget = context.Get<TimeBudgetPlan>(AnalysisContextKeys.TimeBudgetPlan)!;
        // Modo A: 10 + 0,03 · 150 = 14,5 min.
        Assert.Equal(TimeSpan.FromMinutes(14.5), steps[ReportType.CodeSmells].Timeout);
        Assert.Equal(TimeSpan.FromMinutes(14.5), steps[ReportType.InyeccionDependenciasExtensiones].Timeout);
        // Modo B: investigaciones de 14, 8 y 5 min (100 000, 40 000 y 10 000 líneas) con 4 en paralelo: una tanda de 14 + consolidación de 5 + 3 = 8.
        foreach (var report in CoveragePlanner.ModeBReports)
        {
            var wrapper = (FakeWrapper)steps[report];
            Assert.Equal(TimeSpan.FromMinutes(22), wrapper.Timeout);
            Assert.Equal(TimeSpan.FromMinutes(14), wrapper.Table["alpha"]);
            Assert.Equal(TimeSpan.FromMinutes(8), wrapper.Table["beta"]);
            Assert.Equal(TimeSpan.FromMinutes(5), wrapper.Table["gamma"]);
        }
        Assert.Equal(9, budget.ReportTimeouts.Count);
        Assert.Equal(150_000, budget.TotalProductionLoc);
        Assert.Empty(budget.Trimmed);
        Assert.DoesNotContain(reporter.Entries, e => e.Level is AnalysisLogLevel.Warning or AnalysisLogLevel.Error);
    }

    /// <summary>El paralelo de informes como lo monta la factoría, con los pasos del registro como ramas y su límite configurado.</summary>
    private static TolerantReportsParallelStep ParallelOf(AuditorStepRegistry registry, Dictionary<ReportType, IStep> steps, TimeSpan? timeout, int maxParallel = 9)
    {
        var parallel = new TolerantReportsParallelStep(AnalysisPipelineFactory.ParallelReportsStepName, [.. steps.Values])
        {
            Timeout = timeout,
            MaxDegreeOfParallelism = maxParallel
        };
        registry.RegisterReportsParallel(parallel);
        return parallel;
    }

    [Fact]
    public async Task A_finite_parallel_limit_below_the_longest_report_is_raised_to_it_plus_a_margin_and_warns()
    {
        var registry = FullRegistry(out var steps);
        var parallel = ParallelOf(registry, steps, TimeSpan.FromMinutes(10));
        var (context, reporter) = NewContext(PlanOf(Map), Map, totalLoc: 150_000);

        await RunAsync(new TimeBudgetStep(registry), context);

        // Los envoltorios de modo B llevan 22 min (la tanda más larga más la consolidación): el paralelo de 10 los mataría antes que a ellos.
        Assert.Equal(TimeSpan.FromMinutes(22) + TimeBudgetStep.ParallelMargin, parallel.Timeout);
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning
            && e.Message.Contains("ReportsMinutes", StringComparison.Ordinal) && e.Message.Contains("27", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_parallel_limit_that_is_already_enough_is_never_lowered()
    {
        var registry = FullRegistry(out var steps);
        var parallel = ParallelOf(registry, steps, TimeSpan.FromMinutes(120));
        var (context, reporter) = NewContext(PlanOf(Map), Map, totalLoc: 150_000);

        await RunAsync(new TimeBudgetStep(registry), context);

        Assert.Equal(TimeSpan.FromMinutes(120), parallel.Timeout);
        Assert.DoesNotContain(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning);
    }

    [Fact]
    public async Task An_infinite_parallel_limit_stays_infinite()
    {
        var registry = FullRegistry(out var steps);
        var parallel = ParallelOf(registry, steps, System.Threading.Timeout.InfiniteTimeSpan);
        var (context, reporter) = NewContext(PlanOf(Map), Map, totalLoc: 150_000);

        await RunAsync(new TimeBudgetStep(registry), context);

        Assert.Equal(System.Threading.Timeout.InfiniteTimeSpan, parallel.Timeout);
        Assert.DoesNotContain(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning);
    }

    [Fact]
    public async Task With_fewer_reports_at_once_than_branches_the_parallel_limit_covers_every_wave()
    {
        var registry = FullRegistry(out var steps);
        var parallel = ParallelOf(registry, steps, TimeSpan.FromMinutes(10), maxParallel: 3);
        var (context, _) = NewContext(PlanOf(Map), Map, totalLoc: 150_000);

        await RunAsync(new TimeBudgetStep(registry), context);

        // 9 informes de 3 en 3 son tres tandas, cada una como mucho de 22 min.
        Assert.Equal(TimeSpan.FromMinutes(3 * 22) + TimeBudgetStep.ParallelMargin, parallel.Timeout);
    }

    [Fact]
    public async Task A_report_without_a_limit_leaves_the_finite_parallel_without_one_too_and_warns()
    {
        var registry = FullRegistry(out var steps);
        var parallel = ParallelOf(registry, steps, TimeSpan.FromMinutes(10));
        var (context, reporter) = NewContext(PlanOf(Map), Map, totalLoc: 150_000,
            config: [("CodebaseAnalysis:TimeBudget:Auditor:BaseMinutes", "1000000"), ("CodebaseAnalysis:TimeBudget:Auditor:MaxMinutes", "0")]);

        await RunAsync(new TimeBudgetStep(registry), context);

        Assert.Equal(System.Threading.Timeout.InfiniteTimeSpan, steps[ReportType.CodeSmells].Timeout);
        Assert.Equal(System.Threading.Timeout.InfiniteTimeSpan, parallel.Timeout);
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Message.Contains("ReportsMinutes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_registered_parallel_the_budget_only_writes_the_report_timeouts()
    {
        var registry = FullRegistry(out var steps);
        var (context, reporter) = NewContext(PlanOf(Map), Map, totalLoc: 150_000);

        await RunAsync(new TimeBudgetStep(registry), context);

        Assert.Equal(TimeSpan.FromMinutes(14.5), steps[ReportType.CodeSmells].Timeout);
        Assert.DoesNotContain(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning);
    }

    [Fact]
    public async Task With_mode_off_the_parallel_limit_is_not_touched()
    {
        var registry = FullRegistry(out var steps);
        var parallel = ParallelOf(registry, steps, TimeSpan.FromMinutes(10));
        var (context, _) = NewContext(PlanOf(Map), Map, 150_000, config: [("CodebaseAnalysis:Preparation:Mode", "Off")]);

        await RunAsync(new TimeBudgetStep(registry), context);

        Assert.Equal(TimeSpan.FromMinutes(10), parallel.Timeout);
    }

    [Fact]
    public async Task It_writes_the_budget_artifact_with_the_entries_the_coefficients_and_the_assigned_timeouts()
    {
        var registry = FullRegistry(out _);
        var (context, _) = NewContext(PlanOf(Map), Map, totalLoc: 150_000);

        await RunAsync(new TimeBudgetStep(registry), context);

        var budget = ReadBudget();
        Assert.Equal("fp-1", budget.GraphFingerprint);
        Assert.Equal(4 * 4 + 5, budget.Entries.Count); // por informe en modo B, tres investigaciones y la consolidación; en modo A, el auditor
        var defaults = new TimeBudgetOptions();
        Assert.Equal((defaults.Investigation, defaults.Consolidation, defaults.Auditor), (budget.Coefficients.Investigation, budget.Coefficients.Consolidation, budget.Coefficients.Auditor));
        Assert.Equal(240, budget.PipelineCeilingMinutes);
        Assert.Equal(16, budget.EffectiveConcurrency); // 4 investigaciones en paralelo por 4 informes en modo B
        Assert.Equal(budget.Entries.Sum(e => e.Minutes), budget.TotalMinutes, 3);
        Assert.Equal(22, budget.ReportTimeouts.Single(t => t.Report == ReportType.AnalisisFuncional).Minutes);
    }

    [Fact]
    public async Task The_effective_concurrency_is_the_parallel_investigations_times_the_mode_b_reports_that_run_together()
    {
        var plan = PlanOf(Map);
        var registry = FullRegistry(out _);

        var (limited, _) = NewContext(plan, Map, 1000, new AnalysisPipelineInput { MaxParallelAuditors = 2 });
        await RunAsync(new TimeBudgetStep(registry), limited);
        var (custom, _2) = NewContext(plan, Map, 1000, config: [("CodebaseAnalysis:Preparation:MaxParallelInvestigations", "3")]);
        await RunAsync(new TimeBudgetStep(registry), custom);
        var onlyA = new AuditorStepRegistry();
        onlyA.Register(ReportType.CodeSmells, new SleepingStep("a", TimeSpan.Zero));
        var (none, _3) = NewContext(plan, Map, 1000);
        await RunAsync(new TimeBudgetStep(onlyA), none);

        Assert.Equal(4 * 2, limited.Get<TimeBudgetPlan>(AnalysisContextKeys.TimeBudgetPlan)!.EffectiveConcurrency);
        Assert.Equal(3 * 4, custom.Get<TimeBudgetPlan>(AnalysisContextKeys.TimeBudgetPlan)!.EffectiveConcurrency);
        Assert.Equal(4, none.Get<TimeBudgetPlan>(AnalysisContextKeys.TimeBudgetPlan)!.EffectiveConcurrency);
    }

    [Fact]
    public async Task The_total_lines_come_from_the_inventory_and_without_it_from_the_domain_map()
    {
        var registry = FullRegistry(out var steps);
        var (context, _) = NewContext(PlanOf(Map), Map, totalLoc: null);

        await RunAsync(new TimeBudgetStep(registry), context);

        // Sin inventario: 100 000 + 40 000 + 10 000 del mapa → 10 + 0,03 · 150 = 14,5.
        Assert.Equal(150_000, context.Get<TimeBudgetPlan>(AnalysisContextKeys.TimeBudgetPlan)!.TotalProductionLoc);
        Assert.Equal(TimeSpan.FromMinutes(14.5), steps[ReportType.CodeSmells].Timeout);
    }

    [Fact]
    public async Task Over_the_pipeline_ceiling_it_trims_subsystems_warns_and_the_trimmed_plan_replaces_the_old_one_everywhere()
    {
        var registry = FullRegistry(out _);
        var many = new DomainMap
        {
            GraphFingerprint = "fp-1",
            Subsystems = [.. Enumerable.Range(0, 8).Select(i => Sub($"s{i}", 200_000, 8 - i))]
        };
        var (context, reporter) = NewContext(PlanOf(many), many, totalLoc: 1_600_000, config: [("CodebaseAnalysis:Timeouts:PipelineMinutes", "30")]);
        var input = new ReportFacetInput();

        await RunAsync(new TimeBudgetStep(registry), context, input);

        var budget = context.Get<TimeBudgetPlan>(AnalysisContextKeys.TimeBudgetPlan)!;
        Assert.NotEmpty(budget.Trimmed);
        Assert.Equal(30, budget.PipelineCeilingMinutes);
        Assert.True(budget.TotalMinutes / budget.EffectiveConcurrency <= 30 || budget.Trimmed.Count == 4 * 7);
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Message.Contains("techo", StringComparison.Ordinal) && e.Message.Contains("PipelineMinutes", StringComparison.Ordinal));

        // El plan de cobertura sin los recortados: en el contexto, en el disco y en el input de las plantillas.
        var trimmedIds = budget.Trimmed.Where(t => t.Report == ReportType.AnalisisFuncional).Select(t => t.SubsystemId).ToList();
        var inContext = context.Get<CoveragePlan>(AnalysisContextKeys.CoveragePlan)!;
        var onDisk = PreparationArtifacts.TryRead<CoveragePlan>(ReportsPath, out var warning)!;
        Assert.Null(warning);
        foreach (var plan in new[] { inContext, onDisk })
        {
            var funcional = plan.Reports.Single(r => r.Report == ReportType.AnalisisFuncional);
            Assert.Equal(8 - trimmedIds.Count, funcional.Chosen.Count);
            Assert.All(trimmedIds, id => Assert.Contains(funcional.Excluded, e => e.SubsystemId == id && e.Reason == ExclusionReasons.TrimmedByTimeBudget));
            Assert.DoesNotContain(funcional.Chosen.Select(c => c.SubsystemId), id => trimmedIds.Contains(id));
        }
        using var compact = JsonDocument.Parse(input.CoveragePlan);
        Assert.Equal(8 - trimmedIds.Count, compact.RootElement.GetProperty("reports")[0].GetProperty("chosen").GetArrayLength());
        // El presupuesto guardado es el del plan recortado: la tabla de límites de cada envoltorio solo tiene los que se investigan.
        Assert.Equal(8 - trimmedIds.Count, ((FakeWrapper)registry.StepOf(ReportType.AnalisisFuncional)!).Table.Count);
    }

    [Fact]
    public async Task A_report_keeps_at_least_one_subsystem_whatever_the_ceiling()
    {
        var registry = FullRegistry(out _);
        var (context, reporter) = NewContext(PlanOf(Map), Map, totalLoc: 150_000, config: [("CodebaseAnalysis:Timeouts:PipelineMinutes", "1")]);

        await RunAsync(new TimeBudgetStep(registry), context);

        var plan = context.Get<CoveragePlan>(AnalysisContextKeys.CoveragePlan)!;
        Assert.All(plan.Reports, r => Assert.Single(r.Chosen));
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Message.Contains("aun con", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task With_no_pipeline_ceiling_nothing_is_trimmed()
    {
        var registry = FullRegistry(out _);
        var many = new DomainMap { GraphFingerprint = "fp", Subsystems = [.. Enumerable.Range(0, 8).Select(i => Sub($"s{i}", 500_000, 8 - i))] };
        var (context, reporter) = NewContext(PlanOf(many), many, totalLoc: 4_000_000, config: [("CodebaseAnalysis:Timeouts:PipelineMinutes", "0")]);

        await RunAsync(new TimeBudgetStep(registry), context);

        var budget = context.Get<TimeBudgetPlan>(AnalysisContextKeys.TimeBudgetPlan)!;
        Assert.Empty(budget.Trimmed);
        Assert.Equal(0, budget.PipelineCeilingMinutes);
        Assert.DoesNotContain(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning);
    }

    [Fact]
    public async Task The_coefficients_are_read_from_the_configuration_and_a_bad_one_is_a_warning()
    {
        var registry = FullRegistry(out var steps);
        var (context, reporter) = NewContext(PlanOf(Map), Map, totalLoc: 100_000,
            config: [("CodebaseAnalysis:TimeBudget:Auditor:BaseMinutes", "20"), ("CodebaseAnalysis:TimeBudget:Auditor:MaxMinutes", "50"),
                ("CodebaseAnalysis:TimeBudget:Consolidation:BaseMinutes", "mucho")]);

        await RunAsync(new TimeBudgetStep(registry), context);

        Assert.Equal(TimeSpan.FromMinutes(23), steps[ReportType.CodigoMuerto].Timeout); // 20 + 0,03 · 100
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Message.Contains("Consolidation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_mode_off_it_does_nothing_and_the_steps_keep_their_timeouts()
    {
        var registry = FullRegistry(out var steps);
        var (context, reporter) = NewContext(PlanOf(Map), Map, 150_000, config: [("CodebaseAnalysis:Preparation:Mode", "Off")]);

        await RunAsync(new TimeBudgetStep(registry), context);

        Assert.Equal(TimeSpan.FromMinutes(15), steps[ReportType.CodeSmells].Timeout);
        Assert.Equal(TimeSpan.FromMinutes(15), steps[ReportType.AnalisisFuncional].Timeout);
        Assert.Null(context.Get<TimeBudgetPlan>(AnalysisContextKeys.TimeBudgetPlan));
        Assert.False(File.Exists(Path.Combine(ReportsPath, TimeBudgetPlan.FileName)));
        Assert.Empty(reporter.Entries);
    }

    [Fact]
    public async Task Without_a_coverage_plan_it_warns_and_the_steps_keep_their_timeouts()
    {
        var registry = FullRegistry(out var steps);
        var (context, reporter) = NewContext(plan: null, Map, 150_000);

        await RunAsync(new TimeBudgetStep(registry), context);

        Assert.Equal(TimeSpan.FromMinutes(15), steps[ReportType.CodeSmells].Timeout);
        Assert.Null(context.Get<TimeBudgetPlan>(AnalysisContextKeys.TimeBudgetPlan));
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Message.Contains("plan de cobertura", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_empty_plan_budgets_every_report_as_a_plain_auditor_with_the_total_size()
    {
        var registry = FullRegistry(out var steps);
        var empty = CoveragePlanner.Empty(new PreparationOptions(), "sin mapa", DateTimeOffset.UtcNow);
        var (context, _) = NewContext(empty, map: null, totalLoc: 300_000);

        await RunAsync(new TimeBudgetStep(registry), context);

        Assert.All(steps.Values, s => Assert.Equal(TimeSpan.FromMinutes(19), s.Timeout)); // 10 + 0,03 · 300
        Assert.All(((FakeWrapper)steps[ReportType.FlujosEndToEnd]).Table, _ => Assert.Fail("sin subsistemas no hay tabla"));
    }

    [Fact]
    public async Task A_step_that_does_not_let_its_timeout_be_written_is_a_warning_and_the_rest_are_still_written()
    {
        var registry = FullRegistry(out var steps);
        registry.Register(ReportType.CodigoMuerto, new ReadOnlyStep());
        var (context, reporter) = NewContext(PlanOf(Map), Map, 150_000);

        await RunAsync(new TimeBudgetStep(registry), context);

        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Message.Contains("CodigoMuerto", StringComparison.Ordinal));
        Assert.Equal(TimeSpan.FromMinutes(14.5), steps[ReportType.CodeSmells].Timeout);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var (context, _) = NewContext(PlanOf(Map), Map, 150_000);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(new TimeBudgetStep(FullRegistry(out _)), context, ct: cts.Token));
    }

    /// <summary>
    /// A4 en el motor real: un paso de modo A duerme 30 s y tenía 15 min; el presupuesto (una curva de 0,004 min = 240 ms) le asigna
    /// su límite y <c>TimeoutMiddleware</c> lo corta en menos de 10 s. El control es el mismo pipeline con un paso que tarda menos
    /// que el límite asignado, que termina bien: el fallo no es de otra causa.
    /// </summary>
    [Fact]
    public async Task A_step_that_sleeps_longer_than_the_timeout_assigned_by_the_budget_times_out_in_the_real_engine()
    {
        (string, string?)[] tiny =
        [
            ("CodebaseAnalysis:TimeBudget:Auditor:BaseMinutes", "0.004"), ("CodebaseAnalysis:TimeBudget:Auditor:MinutesPerUnit", "0"),
            ("CodebaseAnalysis:TimeBudget:Auditor:MinMinutes", "0.004"), ("CodebaseAnalysis:TimeBudget:Auditor:MaxMinutes", "0.004")
        ];
        var sleeper = new SleepingStep("Auditor_CodeSmells", TimeSpan.FromSeconds(30)) { Timeout = TimeSpan.FromMinutes(15), MaxRetries = 1 };
        var quick = new SleepingStep("Auditor_CodigoMuerto", TimeSpan.FromMilliseconds(50)) { Timeout = TimeSpan.FromMinutes(15), MaxRetries = 1 };

        async Task<(IStepResult Result, TimeSpan Elapsed)> RunPipelineAsync(SleepingStep audited, ReportType report)
        {
            var registry = new AuditorStepRegistry();
            registry.Register(report, audited);
            var (context, _) = NewContext(CoveragePlanner.Empty(new PreparationOptions(), "x", DateTimeOffset.UtcNow), map: null, totalLoc: 1000, config: tiny);
            var started = DateTime.UtcNow;
            var result = await new Pipeline().ExecuteAsync("run", [new TimeBudgetStep(registry), audited], new ReportFacetInput(), context);
            return (result, DateTime.UtcNow - started);
        }

        var (control, _) = await RunPipelineAsync(quick, ReportType.CodigoMuerto);
        var (timedOut, elapsed) = await RunPipelineAsync(sleeper, ReportType.CodeSmells);

        Assert.False(control.HasError, control.Error?.Message);
        Assert.Equal(1, quick.Runs);
        Assert.Equal(TimeSpan.FromMinutes(0.004), quick.Timeout);
        Assert.Equal(TimeSpan.FromMinutes(0.004), sleeper.Timeout);
        Assert.True(timedOut.HasError);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"tardó {elapsed}");
    }

    [Theory]
    [InlineData(9, 9, 16)]    // el limitador deja 9 de las 16 que pediría el pipeline
    [InlineData(null, 16, 16)] // sin límite
    [InlineData(0, 16, 16)]    // 0 = sin límite
    [InlineData(40, 16, 16)]   // un límite mayor que lo que se pide no cambia nada
    public async Task The_effective_concurrency_is_the_smaller_of_the_pipeline_s_and_the_call_limit_and_timeouts_only_go_up(int? limit, int expected, int wanted)
    {
        var plan = PlanOf(Map);
        var plainRegistry = FullRegistry(out var plainSteps);
        var (plainContext, _) = NewContext(plan, Map, 150_000);
        await RunAsync(new TimeBudgetStep(plainRegistry), plainContext);
        var registry = FullRegistry(out var steps);
        var (context, _2) = NewContext(plan, Map, 150_000);

        await RunAsync(new TimeBudgetStep(registry, limit), context);

        var budget = context.Get<TimeBudgetPlan>(AnalysisContextKeys.TimeBudgetPlan)!;
        Assert.Equal(expected, budget.EffectiveConcurrency);
        Assert.Equal((double)wanted / expected, budget.QueueFactor, 3);
        foreach (var (report, step) in steps)
            Assert.True(step.Timeout >= plainSteps[report].Timeout, $"{report}: {plainSteps[report].Timeout} -> {step.Timeout}");
        if (expected < wanted)
            Assert.All(steps, kv => Assert.True(kv.Value.Timeout > plainSteps[kv.Key].Timeout));
    }

    [Fact]
    public async Task With_a_call_limit_the_ceiling_still_trims_subsystems()
    {
        var registry = FullRegistry(out _);
        var many = new DomainMap
        {
            GraphFingerprint = "fp-1",
            Subsystems = [.. Enumerable.Range(1, 8).Select(i => Sub($"s{i}", 200_000, 3))]
        };
        var (context, _) = NewContext(PlanOf(many), many, 150_000, config: [("CodebaseAnalysis:Timeouts:PipelineMinutes", "40")]);

        await RunAsync(new TimeBudgetStep(registry, 9), context);

        var budget = context.Get<TimeBudgetPlan>(AnalysisContextKeys.TimeBudgetPlan)!;
        Assert.NotEmpty(budget.Trimmed);
        Assert.Equal(9, budget.EffectiveConcurrency);
    }
}
