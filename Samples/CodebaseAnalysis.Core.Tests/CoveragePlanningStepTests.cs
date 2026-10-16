using System.Text.Json;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tests.Enrichment;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="CoveragePlanningStep"/> (T14): lee el mapa de dominios del contexto, escribe <c>_coverage_plan.json</c>, deja el plan
/// en el contexto y su versión compacta en <see cref="ReportFacetInput.CoveragePlan"/>; con <c>Mode = Off</c> no hace nada y sin
/// mapa deja un plan vacío con aviso. Sin LLM.
/// </summary>
public sealed class CoveragePlanningStepTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string ReportsPath => Path.Combine(_temp.Path, "repo", "reports");

    private static Subsystem Sub(string id, int tables = 0, string sourceSet = SourceSets.Production) =>
        new()
        {
            Id = id, Name = char.ToUpperInvariant(id[0]) + id[1..], SourceSet = sourceSet,
            Files = [$"src/{id}/A.java", $"src/{id}/B.java"],
            Metrics = new SubsystemMetrics { Files = 2, Loc = 500, Tables = tables }
        };

    private static DomainMap MapOf(params Subsystem[] subsystems) =>
        new() { GeneratedAt = DateTimeOffset.UtcNow, GraphFingerprint = "fp-1", Subsystems = [.. subsystems.OrderBy(s => s.Id, StringComparer.Ordinal)] };

    private (PipelineContext Context, CapturingReporter Reporter) NewContext(DomainMap? map, RepositoryInventory? inventory = null, params (string, string?)[] config)
    {
        var reporter = new CapturingReporter();
        var context = EnrichmentStepContext.Create(TestConfig.FromPairs(config), reporter, Path.Combine(_temp.Path, "repo"), Path.Combine(_temp.Path, "none.db"));
        if (map != null)
            context.Set(AnalysisContextKeys.DomainMap, map);
        if (inventory != null)
            context.Set(AnalysisContextKeys.RepositoryInventory, inventory);
        return (context, reporter);
    }

    private static CoveragePlanningStep NewStep() => new(AnalysisPipelineFactory.CreateDefaultTemplateProvider());

    private static Task<ReportFacetInput> RunAsync(CoveragePlanningStep step, PipelineContext context, ReportFacetInput? input = null, CancellationToken ct = default) =>
        step.ExecuteAsync(input ?? new ReportFacetInput(), context, 1, null, ct);

    private CoveragePlan ReadArtifact()
    {
        var plan = PreparationArtifacts.TryRead<CoveragePlan>(ReportsPath, out var warning);
        Assert.Null(warning);
        return plan!;
    }

    [Fact]
    public async Task It_plans_the_coverage_and_leaves_it_in_the_artifact_the_context_and_the_input()
    {
        var (context, reporter) = NewContext(MapOf(Sub("billing", 4), Sub("reminders", 1), Sub("catalog", 2)));
        var input = new ReportFacetInput();

        var result = await RunAsync(NewStep(), context, input);

        Assert.Same(input, result);
        var plan = ReadArtifact();
        Assert.Equal(4, plan.Reports.Count);
        Assert.Equal(["billing", "catalog", "reminders"], plan.Reports[0].Chosen.Select(c => c.SubsystemId));
        Assert.Equal("fp-1", plan.GraphFingerprint);
        Assert.All(plan.Reports, r => Assert.NotEmpty(r.Questions));
        var inContext = context.Get<CoveragePlan>(AnalysisContextKeys.CoveragePlan);
        Assert.NotNull(inContext);
        Assert.Equal(PreparationArtifacts.Serialize(plan with { GeneratedAt = default }), PreparationArtifacts.Serialize(inContext with { GeneratedAt = default }));
        Assert.NotEqual("{}", input.CoveragePlan);
        Assert.DoesNotContain(reporter.Entries, e => e.Level is AnalysisLogLevel.Warning or AnalysisLogLevel.Error);
    }

    [Fact]
    public async Task The_compact_plan_for_the_templates_lists_what_each_report_investigates_and_leaves_out_but_not_the_questions()
    {
        var subsystems = Enumerable.Range(0, 12).Select(i => Sub($"s{i:00}", tables: i)).Append(Sub("noise", sourceSet: SourceSets.Test)).ToArray();
        var (context, _) = NewContext(MapOf(subsystems), config: ("CodebaseAnalysis:Preparation:MaxSubsystemsPerReport", "5"));
        var input = new ReportFacetInput();

        await RunAsync(NewStep(), context, input);

        using var json = JsonDocument.Parse(input.CoveragePlan);
        var root = json.RootElement;
        Assert.Equal(5, root.GetProperty("max_subsystems_per_report").GetInt32());
        var reports = root.GetProperty("reports").EnumerateArray().ToList();
        Assert.Equal(["AnalisisFuncional", "FlujosEndToEnd", "ErroresYBugs", "ArquitecturaAcoplamiento"], reports.Select(r => r.GetProperty("report").GetString()!));
        var first = reports[0];
        Assert.Equal(5, first.GetProperty("chosen").GetArrayLength());
        Assert.Equal("s11", first.GetProperty("chosen")[0].GetProperty("id").GetString());
        Assert.Equal(8, first.GetProperty("excluded_total").GetInt32());
        Assert.Contains(first.GetProperty("excluded").EnumerateArray(), e => e.GetProperty("id").GetString() == "noise" && e.GetProperty("reason").GetString() == ExclusionReasons.TestSource);
        // Las preguntas son para la investigación y no para el contexto de un auditor en modo A.
        Assert.False(first.TryGetProperty("questions", out _));
    }

    [Fact]
    public async Task Two_runs_over_the_same_map_give_the_same_plan()
    {
        var map = MapOf(Sub("a", 3), Sub("b", 1), Sub("c", 2));

        var (context1, _) = NewContext(map);
        await RunAsync(NewStep(), context1);
        var first = ReadArtifact() with { GeneratedAt = default };
        var (context2, _2) = NewContext(map);
        await RunAsync(NewStep(), context2);
        var second = ReadArtifact() with { GeneratedAt = default };

        Assert.Equal(PreparationArtifacts.Serialize(first), PreparationArtifacts.Serialize(second));
    }

    [Fact]
    public async Task With_mode_off_it_does_nothing()
    {
        var (context, reporter) = NewContext(MapOf(Sub("a")), config: ("CodebaseAnalysis:Preparation:Mode", "Off"));
        var input = new ReportFacetInput();

        var result = await RunAsync(NewStep(), context, input);

        Assert.Same(input, result);
        Assert.Equal("{}", input.CoveragePlan);
        Assert.Null(context.Get<CoveragePlan>(AnalysisContextKeys.CoveragePlan));
        Assert.False(File.Exists(Path.Combine(ReportsPath, CoveragePlan.FileName)));
        Assert.Empty(reporter.Entries);
    }

    [Fact]
    public async Task Without_a_domain_map_the_plan_is_empty_with_a_warning_and_it_is_written_so_an_old_one_does_not_survive()
    {
        Directory.CreateDirectory(ReportsPath);
        File.WriteAllText(Path.Combine(ReportsPath, CoveragePlan.FileName), "stale");
        var (context, reporter) = NewContext(map: null, inventory: new RepositoryInventory { GraphFingerprint = "fp-1" });
        var input = new ReportFacetInput();

        var result = await RunAsync(NewStep(), context, input);

        Assert.Same(input, result);
        var plan = ReadArtifact();
        Assert.Empty(plan.Reports);
        Assert.Empty(plan.Scores);
        // Sin mapa no se sabe sobre qué grafo se calcularía: la regeneración no debe dar por bueno este plan.
        Assert.Null(plan.GraphFingerprint);
        Assert.Single(plan.Warnings);
        Assert.Equal(plan.Warnings, context.Get<CoveragePlan>(AnalysisContextKeys.CoveragePlan)!.Warnings);
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Message.Contains("mapa de dominios", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(input.CoveragePlan);
        Assert.Equal(0, json.RootElement.GetProperty("reports").GetArrayLength());
    }

    [Fact]
    public async Task A_missing_question_template_is_a_warning_and_the_plan_is_still_produced()
    {
        var (context, reporter) = NewContext(MapOf(Sub("a")));
        var empty = Path.Combine(_temp.Path, "templates");
        Directory.CreateDirectory(empty);
        var step = new CoveragePlanningStep(new AnalysisTemplateProvider(empty));

        await RunAsync(step, context);

        var plan = ReadArtifact();
        Assert.All(plan.Reports, r => Assert.Empty(r.Questions));
        Assert.Equal(4, reporter.Entries.Count(e => e.Level == AnalysisLogLevel.Warning && e.Message.Contains("_investigation_questions_", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_failure_inside_is_a_warning_and_the_input_comes_out_untouched()
    {
        var (context, reporter) = NewContext(MapOf(Sub("a")));
        // Una carpeta de informes que es un fichero: no se puede escribir, y no debe romper el paso.
        var blocked = Path.Combine(_temp.Path, "repo", "reports");
        Directory.CreateDirectory(Path.GetDirectoryName(blocked)!);
        File.WriteAllText(blocked, "no soy una carpeta");
        var input = new ReportFacetInput();

        var result = await RunAsync(NewStep(), context, input);

        Assert.Same(input, result);
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning);
        // El plan sale igual en el contexto y en el input: perder el fichero no deja a los informes sin plan.
        Assert.NotNull(context.Get<CoveragePlan>(AnalysisContextKeys.CoveragePlan));
        Assert.NotEqual("{}", input.CoveragePlan);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var (context, _) = NewContext(MapOf(Sub("a")));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(NewStep(), context, ct: cts.Token));
    }

    // ───────────────────────────── Solo los informes que corren ─────────────────────────────

    /// <summary>Un envoltorio de modo B de mentira: lo único que mira el plan es que implemente <see cref="IInvestigationTimeouts"/>.</summary>
    private sealed class FakeModeBStep(string name) : IStep, IInvestigationTimeouts
    {
        public string Name { get; } = name;
        public IResume? Resume => null;
        public Type InputType => typeof(ReportFacetInput);
        public Type OutputType => typeof(SingleReportResult);

        public void SetInvestigationTimeouts(IReadOnlyDictionary<string, TimeSpan> bySubsystemId)
        {
        }

        public Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeModeAStep(string name) : IStep
    {
        public string Name { get; } = name;
        public IResume? Resume => null;
        public Type InputType => typeof(ReportFacetInput);
        public Type OutputType => typeof(SingleReportResult);

        public Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static AuditorStepRegistry RegistryOf(params ReportType[] modeB)
    {
        var registry = new AuditorStepRegistry();
        foreach (var type in Enum.GetValues<ReportType>())
            registry.Register(type, modeB.Contains(type) ? new FakeModeBStep($"B_{type}") : new FakeModeAStep($"A_{type}"));
        return registry;
    }

    [Fact]
    public async Task With_only_mode_a_reports_requested_there_is_no_mode_b_plan_and_the_log_does_not_count_four_reports()
    {
        var (context, reporter) = NewContext(MapOf(Sub("billing", 4), Sub("catalog", 2)));
        var registry = new AuditorStepRegistry();
        registry.Register(ReportType.CodigoMuerto, new FakeModeAStep("Auditor_CodigoMuerto"));
        var input = new ReportFacetInput();

        await RunAsync(new CoveragePlanningStep(AnalysisPipelineFactory.CreateDefaultTemplateProvider(), registry), context, input);

        Assert.Empty(ReadArtifact().Reports);
        Assert.Empty(context.Get<CoveragePlan>(AnalysisContextKeys.CoveragePlan)!.Reports);
        using var json = JsonDocument.Parse(input.CoveragePlan);
        Assert.Equal(0, json.RootElement.GetProperty("reports").GetArrayLength());
        Assert.DoesNotContain(reporter.Entries, e => e.Message.Contains("informe(s) en modo B", StringComparison.Ordinal) && !e.Message.Contains("ningún informe", StringComparison.Ordinal));
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Info && e.Message.Contains("ningún informe de los pedidos", StringComparison.Ordinal));
        Assert.DoesNotContain(reporter.Entries, e => e.Level is AnalysisLogLevel.Warning or AnalysisLogLevel.Error);
    }

    [Fact]
    public async Task The_plan_and_its_log_cover_the_mode_b_reports_that_run()
    {
        var (context, reporter) = NewContext(MapOf(Sub("billing", 4), Sub("catalog", 2)));
        var registry = RegistryOf(ReportType.AnalisisFuncional, ReportType.ErroresYBugs);

        await RunAsync(new CoveragePlanningStep(AnalysisPipelineFactory.CreateDefaultTemplateProvider(), registry), context);

        Assert.Equal([ReportType.AnalisisFuncional, ReportType.ErroresYBugs], ReadArtifact().Reports.Select(r => r.Report));
        Assert.Contains(reporter.Entries, e => e.Message.Contains("2 informe(s) en modo B", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Regenerating_one_report_keeps_in_the_artifact_what_was_chosen_for_the_others_of_the_same_graph()
    {
        var map = MapOf(Sub("billing", 4), Sub("catalog", 2));
        var (first, _) = NewContext(map);
        await RunAsync(NewStep(), first);
        var before = ReadArtifact();

        var (second, _2) = NewContext(map);
        var step = new CoveragePlanningStep(AnalysisPipelineFactory.CreateDefaultTemplateProvider(), RegistryOf(ReportType.FlujosEndToEnd));
        await RunAsync(step, second);

        // El contexto (lo que usan los informes y el presupuesto) solo trae el informe que corre; el disco, los cuatro.
        Assert.Equal([ReportType.FlujosEndToEnd], second.Get<CoveragePlan>(AnalysisContextKeys.CoveragePlan)!.Reports.Select(r => r.Report));
        var after = ReadArtifact();
        Assert.Equal(CoveragePlanner.ModeBReports, after.Reports.Select(r => r.Report));
        Assert.Equal(
            PreparationArtifacts.Serialize(new CoveragePlan { Reports = [before.Reports.Single(r => r.Report == ReportType.ErroresYBugs)] }),
            PreparationArtifacts.Serialize(new CoveragePlan { Reports = [after.Reports.Single(r => r.Report == ReportType.ErroresYBugs)] }));
    }

    [Fact]
    public async Task What_was_chosen_for_other_reports_on_another_graph_is_not_carried_over()
    {
        var (first, _) = NewContext(MapOf(Sub("billing", 4), Sub("catalog", 2)));
        await RunAsync(NewStep(), first);

        var other = MapOf(Sub("billing", 4), Sub("catalog", 2)) with { GraphFingerprint = "fp-2" };
        var (second, _2) = NewContext(other);
        await RunAsync(new CoveragePlanningStep(AnalysisPipelineFactory.CreateDefaultTemplateProvider(), RegistryOf(ReportType.FlujosEndToEnd)), second);

        Assert.Equal([ReportType.FlujosEndToEnd], ReadArtifact().Reports.Select(r => r.Report));
    }
}
