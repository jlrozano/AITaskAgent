using System.Text;
using System.Text.RegularExpressions;
using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.Observability;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tests.Enrichment;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// El cableado de la preparación por subsistema en <see cref="AnalysisPipelineFactory"/> (T23) de extremo a extremo y sin red: con
/// <c>Mode = Subsystems</c> los cuatro pasos de preparación van entre las facetas y los informes, dejan los cuatro <c>_*.json</c>,
/// los informes en modo B investigan por subsistema y llevan el apartado «Cobertura» (si el LLM falso lo escribe), el límite del
/// paralelo de informes sube a lo presupuestado y la regeneración reutiliza el mapa de dominios mientras coincida con el grafo; con
/// <c>Mode = Off</c> el pipeline queda como antes y no se crea ningún artefacto. El LLM es un guion que distingue el nombrado, las
/// investigaciones, las consolidaciones y la síntesis por el texto de sus prompts (las plantillas son las reales).
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class PreparationPipelineTests : IDisposable
{
    private static readonly string[] Capabilities = ["parcel", "ledger", "catalog"];
    private static readonly string[] Kinds = ["Service", "Policy", "Store", "Entry", "Record", "Action"];
    private static readonly ReportType[] ModeB = [.. CoveragePlanner.ModeBReports];

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ───────────────────────────── LLM de mentira ─────────────────────────────

    private enum CallKind { Naming, Investigation, ConsolidationB, Auditor, Synthesis }

    /// <summary>
    /// Contesta según lo que se le pide: nombra cada <c>### id</c> como «Capacidad id», entrega notas con una cita por
    /// investigación, y en la consolidación de un informe en modo B (la que trae el resumen de cobertura) escribe un informe con su
    /// apartado «Cobertura» con los subsistemas que le llegan.
    /// </summary>
    private sealed class ScriptedLlm : ILlmService
    {
        private readonly Lock _lock = new();
        private readonly List<(CallKind Kind, string User)> _calls = [];

        public string Name => "prep-fake";

        public IReadOnlyList<(CallKind Kind, string User)> Calls
        {
            get
            {
                lock (_lock)
                {
                    return [.. _calls];
                }
            }
        }

        public int Count(CallKind kind) => Calls.Count(c => c.Kind == kind);

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            var user = request.Conversation.History.Messages.LastOrDefault(m => m.Role == LlmConstants.MessageRoles.User)?.Content ?? "";
            var system = request.SystemPrompt ?? request.Conversation.History.Messages.FirstOrDefault(m => m.Role == LlmConstants.MessageRoles.System)?.Content ?? "";
            var kind = Classify(system, user);
            lock (_lock)
            {
                _calls.Add((kind, user));
            }

            var answer = kind switch
            {
                CallKind.Naming => Naming(user),
                CallKind.Investigation => Investigation(user),
                CallKind.ConsolidationB => Report(user),
                _ => "# Informe\n\n## Resumen\n\nContenido del servicio falso.\n\n<!-- analysis-meta: health=Green -->"
            };
            return Task.FromResult(LlmResult.Success([new LlmResponse { Content = answer, FinishReason = FinishReason.Stop }],
                tokensUsed: 10, promptTokens: 5, completionTokens: 5, model: "fake-model"));
        }

        private static CallKind Classify(string system, string user) =>
            system.Contains("Analista de Dominios", StringComparison.Ordinal) ? CallKind.Naming
            : user.StartsWith("Investiga un subsistema", StringComparison.Ordinal) ? CallKind.Investigation
            : user.StartsWith("Genera el documento consolidado", StringComparison.Ordinal) ? CallKind.Synthesis
            : user.Contains("Subsistemas investigados (", StringComparison.Ordinal) ? CallKind.ConsolidationB
            : CallKind.Auditor;

        private static string Naming(string user) => string.Concat(
            user.Replace("\r\n", "\n").Split('\n').Where(l => l.StartsWith("### ", StringComparison.Ordinal)).Select(l => l[4..].Trim())
                .Where(id => id.Length > 0 && !id.Contains('<') && !id.Contains(' '))
                .Select(id => $"### {id}\nNombre: Capacidad {id}\nDescripción: Descripción de {id}.\nPrioridad: alta\n\n"));

        private static string Investigation(string user)
        {
            var name = Regex.Match(user, "- \\*\\*Nombre\\*\\*: `(?<n>[^`]+)`").Groups["n"].Value;
            return $"Subsistema: {name}\nQué hace: flujo completo en `src/main/csharp/app/{name.Replace("Capacidad ", "")}/{name.Replace("Capacidad ", "")}ServiceFile.cs:3`.";
        }

        private static string Report(string user)
        {
            var text = new StringBuilder("# Informe\n\n## Resumen\n\nContenido consolidado.\n\n## Cobertura\n\n");
            foreach (Match match in Regex.Matches(user, @"^- (?<n>.+?) \(id: [^)]+\), \d+ líneas", RegexOptions.Multiline))
                text.Append("- ").AppendLine(match.Groups["n"].Value);
            return text.Append("\n<!-- analysis-meta: health=Green -->").ToString();
        }

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int EstimateTokenCount(string text) => text.Length / 4;

        public int GetMaxContextTokens(string? model = null) => 1_000_000;
    }

    private sealed class Host : IAsyncDisposable
    {
        private Host(IConfiguration configuration, ServiceProvider services, ScriptedLlm llm)
        {
            Configuration = configuration;
            Services = services;
            Llm = llm;
            Roles = LlmRoleFactory.BuildRoles(services, _ => null, _ => { });
        }

        public IConfiguration Configuration { get; }
        public ServiceProvider Services { get; }
        public ScriptedLlm Llm { get; }
        public Dictionary<string, LlmRole> Roles { get; }
        public AITaskAgent.Support.Template.ITemplateProvider Templates { get; } = AnalysisPipelineFactory.CreateDefaultTemplateProvider();

        public static Host Create(params (string, string?)[] pairs)
        {
            var configuration = TestConfig.FromPairs([.. FakeLlmConfig.Pairs("prep-fake"), ("CodebaseAnalysis:Timeouts:PipelineMinutes", "0"), .. pairs]);
            var llm = new ScriptedLlm();
            var services = new ServiceCollection();
            services.AddSingleton(configuration);
            services.AddLogging();
            services.AddSingleton<ILlmService>(llm);
            services.AddAITaskAgent();
            var provider = services.BuildServiceProvider();
            provider.UseAITaskAgent();
            return new Host(configuration, provider, llm);
        }

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    // ───────────────────────────── Repositorio y grafo sintéticos ─────────────────────────────

    private string Repo => Path.Combine(_temp.Path, "repo");

    /// <summary>
    /// Tres capacidades de seis tipos cada una (ficheros C# reales en disco y su grafo ya enriquecido), con sus llamadas
    /// internas: cada una es un subsistema. No hay ficheros Java, así que el enriquecedor de Java no arranca.
    /// </summary>
    private async Task<CbmTestGraph> CreateRepoAsync(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var graph = await CbmTestGraph.CreateAsync(dbPath, "demo");
        var enrichedMark = true;
        foreach (var capability in Capabilities)
        {
            var methods = new List<long>();
            foreach (var kind in Kinds)
            {
                var name = capability + kind;
                var file = $"src/main/csharp/app/{capability}/{name}.cs";
                var path = Path.Combine(Repo, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllLinesAsync(path, Enumerable.Range(1, 30).Select(i => $"// {name} {i}"));
                var type = await graph.NodeAsync("Class", name, $"Demo.{name}", file, 1, 30, enrichedMark ? "{\"cyclomatic\":3}" : "{}");
                enrichedMark = false;
                var method = await graph.NodeAsync("Method", "Run", $"Demo.{name}.Run", file, 3, 9);
                await graph.EdgeAsync(type, method, "DEFINES_METHOD");
                methods.Add(method);
            }
            for (var i = 0; i < methods.Count; i++)
            {
                for (var j = i + 1; j < methods.Count; j++)
                    await graph.EdgeAsync(methods[i], methods[j], "CALLS");
            }
        }
        return graph;
    }

    private static readonly string[] PreparationArtifactNames =
        [RepositoryInventory.FileName, DomainMap.FileName, CoveragePlan.FileName, TimeBudgetPlan.FileName];

    private async Task<(AnalysisPipelineInput Input, CbmTestGraph Graph)> NewRunAsync(Host host)
    {
        var input = AnalysisRunOptions.BuildInput(host.Configuration, Repo, _ => { });
        var graph = await CreateRepoAsync(input.DatabasePath);
        AnalysisToolRegistration.Register(input);
        return (input, graph);
    }

    private static async Task<AnalysisPipelineFinalResult> RunAsync(Host host, AnalysisPipelineInput input, CapturingReporter reporter)
    {
        var result = await AnalysisRunner.RunAsync(input, host.Roles, host.Services, null, reporter, CancellationToken.None);
        var final = Assert.IsType<AnalysisPipelineFinalResult>(result);
        Assert.False(final.HasError, final.Error?.Message);
        return final;
    }

    private string Report(AnalysisPipelineInput input, ReportType type) =>
        File.ReadAllText(Path.Combine(input.OutputReportsPath, AnalysisPipelineInput.GetReportFileName(type)));

    private T Read<T>(AnalysisPipelineInput input) where T : class, IPreparationArtifact
    {
        var artifact = PreparationArtifacts.TryRead<T>(input.OutputReportsPath, out var warning);
        Assert.Null(warning);
        return artifact!;
    }

    // ───────────────────────────── Estructura del pipeline ─────────────────────────────

    [Fact]
    public async Task With_subsystems_the_four_preparation_steps_go_between_the_facets_and_the_reports_and_mode_b_reports_are_wrapped()
    {
        await using var host = Host.Create();
        var input = new AnalysisPipelineInput();

        var pipeline = AnalysisPipelineFactory.CreatePipeline(host.Templates, host.Roles, input, host.Services);

        Assert.Equal(
            [DetectAndIndexGraphStep.StepName, LanguageEnricherStep.StepName, DotNetEnricherStep.StepName, SqlFacetExtractorStep.StepName,
             RepositoryInventoryStep.StepName, DomainDiscoveryStep.StepName, CoveragePlanningStep.StepName, TimeBudgetStep.StepName,
             AnalysisPipelineFactory.ParallelReportsStepName, ExecutiveSynthesizerStep.StepName],
            pipeline.Steps.Select(s => s.Name));
        var branches = pipeline.Steps.OfType<TolerantReportsParallelStep>().Single().Branches;
        Assert.Equal(9, branches.Count);
        Assert.Equal(ModeB, branches.OfType<SubsystemInvestigationReportStep>().Select(b => Enum.Parse<ReportType>(b.Name["Auditor_".Length..])).Order());
        Assert.All(branches, b => Assert.StartsWith("Auditor_", b.Name, StringComparison.Ordinal));
        Assert.Equal(5, branches.Count(b => b is CustomStatelessTemplateLlmStep<ReportFacetInput, SingleReportResult>));
    }

    [Fact]
    public async Task The_mode_b_consolidation_is_capped_and_the_auditors_of_mode_a_and_the_degradation_keep_the_auditor_cap()
    {
        await using var host = Host.Create();
        var input = new AnalysisPipelineInput();

        var pipeline = AnalysisPipelineFactory.CreatePipeline(host.Templates, host.Roles, input, host.Services);

        var branches = pipeline.Steps.OfType<TolerantReportsParallelStep>().Single().Branches;
        var wrappers = branches.OfType<SubsystemInvestigationReportStep>().ToList();
        Assert.Equal(4, wrappers.Count);
        // La consolidación, sin acotar de más por defecto (0 = el tope del auditor); el auditor al que se degrada, con el tope de siempre.
        Assert.All(wrappers, w => Assert.Equal(AnalysisPipelineInput.DefaultAuditorMaxToolIterations, w.ConsolidationMaxToolIterations));
        Assert.All(wrappers, w => Assert.Equal(AnalysisPipelineInput.DefaultAuditorMaxToolIterations, w.DegradedAuditorMaxToolIterations));
        var modeA = branches.OfType<CustomStatelessTemplateLlmStep<ReportFacetInput, SingleReportResult>>().ToList();
        Assert.Equal(5, modeA.Count);
        Assert.All(modeA, a => Assert.Equal(AnalysisPipelineInput.DefaultAuditorMaxToolIterations, a.MaxToolIterations));
    }

    [Theory]
    [InlineData("2", 2)]
    [InlineData("0", 12)]
    [InlineData("30", 12)]
    public async Task The_consolidation_cap_comes_from_the_configuration_zero_is_the_auditor_cap_and_it_never_raises_it(string configured, int expected)
    {
        await using var host = Host.Create(("CodebaseAnalysis:Preparation:ConsolidationMaxToolIterations", configured));

        var pipeline = AnalysisPipelineFactory.CreatePipeline(host.Templates, host.Roles, new AnalysisPipelineInput(), host.Services);

        var wrappers = pipeline.Steps.OfType<TolerantReportsParallelStep>().Single().Branches.OfType<SubsystemInvestigationReportStep>().ToList();
        Assert.Equal(4, wrappers.Count);
        Assert.All(wrappers, w => Assert.Equal(expected, w.ConsolidationMaxToolIterations));
        Assert.All(wrappers, w => Assert.Equal(12, w.DegradedAuditorMaxToolIterations));
    }

    [Fact]
    public async Task The_consolidation_cap_does_not_go_above_the_per_role_cap_of_the_auditor()
    {
        await using var host = Host.Create();
        var input = new AnalysisPipelineInput { AuditorMaxToolIterationsByRole = new(StringComparer.OrdinalIgnoreCase) { ["FunctionalAuditor"] = 3 } };

        var pipeline = AnalysisPipelineFactory.CreatePipeline(host.Templates, host.Roles, input, host.Services);

        var functional = pipeline.Steps.OfType<TolerantReportsParallelStep>().Single().Branches.OfType<SubsystemInvestigationReportStep>()
            .Single(w => w.Name == "Auditor_AnalisisFuncional");
        Assert.Equal(3, functional.ConsolidationMaxToolIterations);
        Assert.Equal(3, functional.DegradedAuditorMaxToolIterations);
    }

    [Fact]
    public async Task With_mode_off_the_pipeline_is_exactly_what_it_was_before_the_preparation()
    {
        await using var host = Host.Create(("CodebaseAnalysis:Preparation:Mode", "Off"));

        var pipeline = AnalysisPipelineFactory.CreatePipeline(host.Templates, host.Roles, new AnalysisPipelineInput(), host.Services);

        Assert.Equal(
            [DetectAndIndexGraphStep.StepName, LanguageEnricherStep.StepName, DotNetEnricherStep.StepName, SqlFacetExtractorStep.StepName,
             AnalysisPipelineFactory.ParallelReportsStepName, ExecutiveSynthesizerStep.StepName],
            pipeline.Steps.Select(s => s.Name));
        var branches = pipeline.Steps.OfType<TolerantReportsParallelStep>().Single().Branches;
        Assert.Equal(9, branches.Count);
        Assert.All(branches, b => Assert.IsType<CustomStatelessTemplateLlmStep<ReportFacetInput, SingleReportResult>>(b));
        // Con Off ningún auditor se acota: todos llevan el tope de siempre.
        Assert.All(branches.OfType<CustomStatelessTemplateLlmStep<ReportFacetInput, SingleReportResult>>(),
            a => Assert.Equal(AnalysisPipelineInput.DefaultAuditorMaxToolIterations, a.MaxToolIterations));
    }

    [Fact]
    public async Task The_regeneration_pipeline_gets_the_same_steps_after_the_facets_and_with_mode_off_it_does_not()
    {
        await using var host = Host.Create();
        await using var off = Host.Create(("CodebaseAnalysis:Preparation:Mode", "Off"));
        var input = new AnalysisPipelineInput { RequestedReports = [ReportType.AnalisisFuncional, ReportType.CodeSmells] };

        var on = AnalysisPipelineFactory.CreateRegenerationPipeline(host.Templates, host.Roles, input, synthesis: true, host.Services);
        var without = AnalysisPipelineFactory.CreateRegenerationPipeline(off.Templates, off.Roles, input, synthesis: true, off.Services);

        Assert.Equal(
            [PrepareRegenerationStep.StepName, SqlFacetExtractorStep.StepName, RepositoryInventoryStep.StepName, DomainDiscoveryStep.StepName,
             CoveragePlanningStep.StepName, TimeBudgetStep.StepName, AnalysisPipelineFactory.ParallelReportsStepName, ExecutiveSynthesizerStep.StepName],
            on.Steps.Select(s => s.Name));
        var branches = on.Steps.OfType<TolerantReportsParallelStep>().Single().Branches;
        Assert.IsType<SubsystemInvestigationReportStep>(branches.Single(b => b.Name == "Auditor_AnalisisFuncional"));
        Assert.IsType<CustomStatelessTemplateLlmStep<ReportFacetInput, SingleReportResult>>(branches.Single(b => b.Name == "Auditor_CodeSmells"));
        Assert.Equal(
            [PrepareRegenerationStep.StepName, SqlFacetExtractorStep.StepName, AnalysisPipelineFactory.ParallelReportsStepName, ExecutiveSynthesizerStep.StepName],
            without.Steps.Select(s => s.Name));
    }

    // ───────────────────────────── Ejecución completa ─────────────────────────────

    [Fact]
    public async Task A_full_run_leaves_the_four_artifacts_investigates_each_subsystem_and_the_mode_b_reports_carry_the_coverage_section()
    {
        await using var host = Host.Create();
        var (input, graph) = await NewRunAsync(host);
        using var _ = graph;
        var reporter = new CapturingReporter();

        var final = await RunAsync(host, input, reporter);

        Assert.Empty(final.FailedReports);
        Assert.Equal(9, final.IndividualReports.Count);
        foreach (var name in PreparationArtifactNames)
            Assert.True(File.Exists(Path.Combine(input.OutputReportsPath, name)), $"falta {name}");

        var map = Read<DomainMap>(input);
        Assert.Equal(["catalog", "ledger", "parcel"], map.Subsystems.Select(s => s.Id));
        Assert.All(map.Subsystems, s => Assert.Equal(SubsystemNameOrigins.Llm, s.NameOrigin));
        var plan = Read<CoveragePlan>(input);
        Assert.Equal(ModeB, plan.Reports.Select(r => r.Report));
        Assert.All(plan.Reports, r => Assert.Equal(3, r.Chosen.Count));
        Assert.Equal(map.GraphFingerprint, plan.GraphFingerprint);
        Assert.Equal(map.GraphFingerprint, Read<RepositoryInventory>(input).GraphFingerprint);
        Assert.Equal(9, Read<TimeBudgetPlan>(input).ReportTimeouts.Count);

        // Una llamada de nombrado, tres investigaciones por cada informe en modo B, cuatro consolidaciones, cinco auditores y la síntesis.
        Assert.Equal(1, host.Llm.Count(CallKind.Naming));
        Assert.Equal(4 * 3, host.Llm.Count(CallKind.Investigation));
        Assert.Equal(4, host.Llm.Count(CallKind.ConsolidationB));
        Assert.Equal(5, host.Llm.Count(CallKind.Auditor));
        Assert.Equal(1, host.Llm.Count(CallKind.Synthesis));

        foreach (var type in ModeB)
        {
            var text = Report(input, type);
            Assert.Contains("## Cobertura", text);
            Assert.All(["Capacidad parcel", "Capacidad ledger", "Capacidad catalog"], name => Assert.Contains(name, text));
        }
        foreach (var type in Enum.GetValues<ReportType>().Except(ModeB))
            Assert.DoesNotContain("## Cobertura", Report(input, type));

        // Los auditores en modo A reciben el inventario, el mapa y el plan ya rellenos en su prompt.
        Assert.Contains(host.Llm.Calls, c => c.Kind == CallKind.Auditor && c.User.Contains("Capacidad parcel", StringComparison.Ordinal));
        Assert.DoesNotContain(reporter.Entries, e => e.Level == AnalysisLogLevel.Error);
    }

    [Fact]
    public async Task Asking_only_for_a_mode_a_report_plans_no_mode_b_report_and_budgets_only_that_one()
    {
        // `--reports deadcode`: el log decía «4 informes en modo B» y el plan los llevaba, con un presupuesto de una sola pieza.
        await using var host = Host.Create();
        var (input, graph) = await NewRunAsync(host);
        using var _ = graph;
        input.RequestedReports = [ReportType.CodigoMuerto];
        var reporter = new CapturingReporter();

        var final = await RunAsync(host, input, reporter);

        Assert.Empty(final.FailedReports);
        Assert.Empty(Read<CoveragePlan>(input).Reports);
        var budget = Read<TimeBudgetPlan>(input);
        Assert.Equal([ReportType.CodigoMuerto], budget.ReportTimeouts.Select(t => t.Report));
        Assert.Single(budget.Entries);
        Assert.DoesNotContain(reporter.Entries, e => e.Message.Contains("informe(s) en modo B", StringComparison.Ordinal) && e.Message.Contains("4 informe(s)", StringComparison.Ordinal));
        Assert.Equal(0, host.Llm.Count(CallKind.Investigation));
        Assert.Equal(1, host.Llm.Count(CallKind.Auditor));
    }

    [Fact]
    public async Task The_parallel_reports_limit_is_raised_by_the_time_budget_so_it_does_not_cut_the_wrappers_first()
    {
        // 5 min por informe no caben en el envoltorio de modo B (una tanda de 4 min más 8 de consolidación con los coeficientes por defecto).
        await using var host = Host.Create(("CodebaseAnalysis:Timeouts:ReportsMinutes", "5"));
        var (input, graph) = await NewRunAsync(host);
        using var _ = graph;
        var reporter = new CapturingReporter();
        var pipeline = AnalysisPipelineFactory.CreatePipeline(host.Templates, host.Roles, input, host.Services);
        var parallel = pipeline.Steps.OfType<TolerantReportsParallelStep>().Single();
        Assert.Equal(TimeSpan.FromMinutes(5), parallel.Timeout);
        var context = AnalysisRunner.CreateContext(input, host.Services, null, reporter);

        var result = await pipeline.ExecuteAsync(input, context, CancellationToken.None);

        Assert.False(result.HasError, result.Error?.Message);
        var longest = parallel.Branches.Max(b => b.Timeout!.Value);
        Assert.True(longest > TimeSpan.FromMinutes(5), $"el envoltorio más largo es de {longest}");
        Assert.True(parallel.Timeout >= longest + TimeBudgetStep.ParallelMargin, $"el paralelo es de {parallel.Timeout} y el envoltorio más largo de {longest}");
        Assert.Contains(reporter.Entries, e => e.Source == TimeBudgetStep.StepName && e.Level == AnalysisLogLevel.Warning
            && e.Message.Contains("ReportsMinutes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_mode_off_no_artifact_is_created_and_no_investigation_or_naming_is_asked()
    {
        await using var host = Host.Create(("CodebaseAnalysis:Preparation:Mode", "Off"));
        var (input, graph) = await NewRunAsync(host);
        using var _ = graph;

        var final = await RunAsync(host, input, new CapturingReporter());

        Assert.Equal(9, final.IndividualReports.Count);
        foreach (var name in PreparationArtifactNames)
            Assert.False(File.Exists(Path.Combine(input.OutputReportsPath, name)), $"no debería existir {name}");
        Assert.Equal(9 + 1, host.Llm.Calls.Count);
        Assert.Equal(0, host.Llm.Count(CallKind.Naming));
        Assert.Equal(0, host.Llm.Count(CallKind.Investigation));
        Assert.Equal(0, host.Llm.Count(CallKind.ConsolidationB));
        Assert.All(ModeB, type => Assert.DoesNotContain("## Cobertura", Report(input, type)));
    }

    // ───────────────────────────── Regeneración ─────────────────────────────

    [Fact]
    public async Task Regenerating_reuses_the_domain_map_from_disk_when_it_matches_the_graph_and_recalculates_the_rest()
    {
        await using var host = Host.Create();
        var (input, graph) = await NewRunAsync(host);
        using var _ = graph;
        await RunAsync(host, input, new CapturingReporter());
        var mapPath = Path.Combine(input.OutputReportsPath, DomainMap.FileName);
        var mapBefore = await File.ReadAllTextAsync(mapPath);
        var budgetStamp = Read<TimeBudgetPlan>(input).GeneratedAt;
        var inventoryStamp = Read<RepositoryInventory>(input).GeneratedAt;
        Assert.Equal(1, host.Llm.Count(CallKind.Naming));
        var investigationsBefore = host.Llm.Count(CallKind.Investigation);
        var reporter = new CapturingReporter();

        var result = await AnalysisRunner.RegenerateAsync(input, [ReportType.AnalisisFuncional], synthesis: false, host.Roles, host.Services, null, reporter, CancellationToken.None);

        Assert.False(result.HasError, result.Error?.Message);
        // Sin volver a nombrar: el mapa es el del disco, intacto, y los subsistemas se investigan igual con sus nombres.
        Assert.Equal(1, host.Llm.Count(CallKind.Naming));
        Assert.Equal(mapBefore, await File.ReadAllTextAsync(mapPath));
        Assert.Equal(investigationsBefore + 3, host.Llm.Count(CallKind.Investigation));
        Assert.All(["Capacidad parcel", "Capacidad ledger", "Capacidad catalog"], name => Assert.Contains(name, Report(input, ReportType.AnalisisFuncional)));
        Assert.Contains(reporter.Entries, e => e.Source == DomainDiscoveryStep.StepName && e.Message.Contains("reutiliza", StringComparison.OrdinalIgnoreCase));
        // El inventario, el plan y el presupuesto son deterministas y baratos: se recalculan.
        Assert.True(Read<RepositoryInventory>(input).GeneratedAt > inventoryStamp);
        Assert.True(Read<TimeBudgetPlan>(input).GeneratedAt > budgetStamp);
        Assert.Equal(ModeB, Read<CoveragePlan>(input).Reports.Select(r => r.Report));
        Assert.DoesNotContain(reporter.Entries, e => e.Level == AnalysisLogLevel.Error);
    }

    [Fact]
    public async Task Regenerating_recalculates_the_map_when_the_graph_changed_or_the_file_is_missing()
    {
        await using var host = Host.Create();
        var (input, graph) = await NewRunAsync(host);
        using var _ = graph;
        await RunAsync(host, input, new CapturingReporter());
        Assert.Equal(1, host.Llm.Count(CallKind.Naming));

        // Otra indexación del grafo: la huella cambia y el mapa guardado ya no vale.
        using (var update = graph.Connection.CreateCommand())
        {
            update.CommandText = "UPDATE projects SET indexed_at = '2026-10-05T08:00:00Z'";
            await update.ExecuteNonQueryAsync();
        }
        await AnalysisRunner.RegenerateAsync(input, [ReportType.AnalisisFuncional], synthesis: false, host.Roles, host.Services, null, new CapturingReporter(), CancellationToken.None);
        Assert.Equal(2, host.Llm.Count(CallKind.Naming));
        Assert.Contains("2026-10-05", Read<DomainMap>(input).GraphFingerprint);

        // Sin el fichero, también se recalcula.
        File.Delete(Path.Combine(input.OutputReportsPath, DomainMap.FileName));
        await AnalysisRunner.RegenerateAsync(input, [ReportType.AnalisisFuncional], synthesis: false, host.Roles, host.Services, null, new CapturingReporter(), CancellationToken.None);
        Assert.Equal(3, host.Llm.Count(CallKind.Naming));
        Assert.True(File.Exists(Path.Combine(input.OutputReportsPath, DomainMap.FileName)));
    }

    [Fact]
    public async Task Regenerating_with_mode_off_creates_no_preparation_and_does_not_touch_the_existing_artifacts()
    {
        await using var host = Host.Create();
        var (input, graph) = await NewRunAsync(host);
        using var _ = graph;
        await RunAsync(host, input, new CapturingReporter());
        var mapBefore = await File.ReadAllTextAsync(Path.Combine(input.OutputReportsPath, DomainMap.FileName));
        await using var off = Host.Create(("CodebaseAnalysis:Preparation:Mode", "Off"));

        var result = await AnalysisRunner.RegenerateAsync(input, [ReportType.AnalisisFuncional], synthesis: false, off.Roles, off.Services, null, new CapturingReporter(), CancellationToken.None);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(CallKind.Auditor, Assert.Single(off.Llm.Calls).Kind); // solo el informe, como un auditor normal
        Assert.Equal(mapBefore, await File.ReadAllTextAsync(Path.Combine(input.OutputReportsPath, DomainMap.FileName)));
    }
}
