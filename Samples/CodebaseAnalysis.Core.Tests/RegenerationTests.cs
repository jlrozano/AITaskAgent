using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Knowledge.Graph;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Timeouts configurables del análisis (<c>CodebaseAnalysis:Timeouts</c>) y regeneración de informes sobre el grafo que ya
/// existe (<see cref="AnalysisPipelineFactory.CreateRegenerationPipeline"/>), sin red ni procesos externos.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class RegenerationTests
{
    private const string CannedReport = """
        # Informe

        ## Resumen

        Contenido generado por el servicio falso de los tests, sin datos del repositorio.

        <!-- analysis-meta: health=Green -->
        """;

    private static readonly (string, string?)[] FakeProvider = FakeLlmConfig.Pairs("canned");

    // ── AnalysisTimeouts ─────────────────────────────────────────────────

    [Fact]
    public void Timeouts_default_to_the_documented_values()
    {
        var timeouts = AnalysisTimeouts.FromConfiguration(TestConfig.FromPairs());

        Assert.Equal(new AnalysisTimeouts(), timeouts);
        Assert.Equal(TimeSpan.FromMinutes(240), timeouts.Pipeline);
        Assert.Equal(TimeSpan.FromMinutes(15), timeouts.Detect);
        Assert.Equal(TimeSpan.FromMinutes(20), timeouts.Enrich);
        Assert.Equal(TimeSpan.FromMinutes(60), timeouts.Reports);
        Assert.Equal(TimeSpan.FromMinutes(15), timeouts.Auditor);
        Assert.Equal(TimeSpan.FromMinutes(15), timeouts.Synthesis);
    }

    [Fact]
    public void Timeouts_come_from_the_configuration_and_zero_removes_the_limit()
    {
        var timeouts = AnalysisTimeouts.FromConfiguration(TestConfig.FromPairs(
            ("CodebaseAnalysis:Timeouts:PipelineMinutes", "0"),
            ("CodebaseAnalysis:Timeouts:DetectMinutes", "3"),
            ("CodebaseAnalysis:Timeouts:EnrichMinutes", "0"),
            ("CodebaseAnalysis:Timeouts:ReportsMinutes", "90"),
            ("CodebaseAnalysis:Timeouts:AuditorMinutes", "-1"),
            ("CodebaseAnalysis:Timeouts:SynthesisMinutes", "7")));

        Assert.Equal(Timeout.InfiniteTimeSpan, timeouts.Pipeline);
        Assert.Equal(TimeSpan.FromMinutes(3), timeouts.Detect);
        Assert.Equal(Timeout.InfiniteTimeSpan, timeouts.Enrich);
        Assert.Equal(TimeSpan.FromMinutes(90), timeouts.Reports);
        Assert.Equal(Timeout.InfiniteTimeSpan, timeouts.Auditor);
        Assert.Equal(TimeSpan.FromMinutes(7), timeouts.Synthesis);
    }

    [Fact]
    public void A_limit_beyond_what_CancelAfter_accepts_is_treated_as_no_limit()
    {
        Assert.Equal(TimeSpan.FromMinutes(AnalysisTimeouts.MaxMinutes), AnalysisTimeouts.ToTimeSpan(AnalysisTimeouts.MaxMinutes));
        Assert.Equal(Timeout.InfiniteTimeSpan, AnalysisTimeouts.ToTimeSpan(AnalysisTimeouts.MaxMinutes + 1));
        Assert.Equal(Timeout.InfiniteTimeSpan, AnalysisTimeouts.ToTimeSpan(int.MaxValue));

        // Los dos valores extremos que llegan al framework no lanzan: el mayor admitido y el infinito.
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(AnalysisTimeouts.ToTimeSpan(AnalysisTimeouts.MaxMinutes));
        cts.CancelAfter(Timeout.InfiniteTimeSpan);
        using var infinite = new CancellationTokenSource(Timeout.InfiniteTimeSpan);
        Assert.False(infinite.IsCancellationRequested);
    }

    // ── Factoría ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_factory_applies_the_configured_timeouts_to_every_step_and_to_the_pipeline()
    {
        await using var host = await TestHost.CreateAsync(
            ("CodebaseAnalysis:Timeouts:PipelineMinutes", "1"),
            ("CodebaseAnalysis:Timeouts:DetectMinutes", "2"),
            ("CodebaseAnalysis:Timeouts:EnrichMinutes", "3"),
            ("CodebaseAnalysis:Timeouts:ReportsMinutes", "4"),
            ("CodebaseAnalysis:Timeouts:AuditorMinutes", "0"),
            ("CodebaseAnalysis:Timeouts:SynthesisMinutes", "6"));
        var input = new AnalysisPipelineInput { RequestedReports = [ReportType.CodeSmells, ReportType.ErroresYBugs] };

        var pipeline = AnalysisPipelineFactory.CreatePipeline(host.Templates, host.Roles, input, host.Services);

        Assert.Equal(TimeSpan.FromMinutes(1), pipeline.PipelineTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), Assert.IsType<DetectAndIndexGraphStep>(pipeline.Steps[0]).Timeout);
        Assert.Equal(TimeSpan.FromMinutes(3), Assert.IsType<DotNetEnricherStep>(pipeline.Steps[2]).Timeout);
        var reports = Assert.IsType<TolerantReportsParallelStep>(pipeline.Steps.Single(s => s.Name == AnalysisPipelineFactory.ParallelReportsStepName));
        Assert.Equal(TimeSpan.FromMinutes(4), reports.Timeout);
        Assert.Equal(2, reports.Branches.Count);
        // El envoltorio de modo B (ErroresYBugs) también queda sin límite si el del auditor lo está.
        Assert.All(reports.Branches, b => Assert.Equal(Timeout.InfiniteTimeSpan, b.Timeout));
        Assert.Equal(TimeSpan.FromMinutes(6), Assert.IsType<ExecutiveSynthesizerStep>(pipeline.Steps.Single(s => s.Name == ExecutiveSynthesizerStep.StepName)).Timeout);
    }

    /// <summary>
    /// H-4: <c>CodebaseAnalysis:Reports:MaxParallelAuditors</c> llega al paralelo de los auditores del pipeline completo y
    /// del de regeneración; sin la clave es 0, sin tope propio, como antes.
    /// </summary>
    [Fact]
    public async Task The_factory_applies_the_auditor_concurrency_limit_to_both_pipelines()
    {
        await using var host = await TestHost.CreateAsync();
        var limited = new AnalysisPipelineInput { RequestedReports = [ReportType.CodeSmells, ReportType.ErroresYBugs], MaxParallelAuditors = 3 };
        var unlimited = new AnalysisPipelineInput { RequestedReports = [ReportType.CodeSmells] };

        var full = AnalysisPipelineFactory.CreatePipeline(host.Templates, host.Roles, limited, host.Services);
        var regeneration = AnalysisPipelineFactory.CreateRegenerationPipeline(host.Templates, host.Roles, limited, synthesis: false, host.Services);
        var byDefault = AnalysisPipelineFactory.CreatePipeline(host.Templates, host.Roles, unlimited, host.Services);

        Assert.Equal(3, full.Steps.OfType<TolerantReportsParallelStep>().Single().MaxDegreeOfParallelism);
        Assert.Equal(3, regeneration.Steps.OfType<TolerantReportsParallelStep>().Single().MaxDegreeOfParallelism);
        Assert.Equal(0, byDefault.Steps.OfType<TolerantReportsParallelStep>().Single().MaxDegreeOfParallelism);
    }

    /// <summary>Los pasos y, de los paralelos, sus ramas, recursivamente.</summary>
    private static IEnumerable<IStep> WithBranches(IEnumerable<IStep> steps) =>
        steps.SelectMany(s => s is ParallelStep parallel ? [s, .. WithBranches(parallel.Branches)] : new[] { s });

    /// <summary>
    /// H-4: un paso con <c>Timeout</c> null hereda <c>DefaultStepTimeout</c> del motor (1 min), que cortó el chat a los 60 s.
    /// Recorre los pasos de primer nivel y las ramas del pipeline completo, el de regeneración con síntesis y el turno del
    /// chat. Los sub-steps LLM del enriquecimiento y de la síntesis quedan fuera: se invocan sin middleware y su
    /// <c>Timeout</c> no corta nada.
    /// </summary>
    [Fact]
    public async Task Every_step_the_app_builds_has_an_explicit_timeout()
    {
        await using var host = await TestHost.CreateAsync();
        var all = new AnalysisPipelineInput { RequestedReports = Enum.GetValues<ReportType>().ToList() };
        var full = AnalysisPipelineFactory.CreatePipeline(host.Templates, host.Roles, new AnalysisPipelineInput(), host.Services);
        var regeneration = AnalysisPipelineFactory.CreateRegenerationPipeline(host.Templates, host.Roles, all, synthesis: true, host.Services);

        using var dir = new TempDirectory();
        var workspace = new Chat.ChatWorkspace(dir.Path, Path.Combine(dir.Path, ".codebase", "codebase.db"), Path.Combine(dir.Path, "reports"));
        var store = new Chat.ChatSessionStore(Path.Combine(dir.Path, ".codebase", "chat"));
        var engine = new CapturingPipeline();
        await using (var services = ChatTestHost.BuildServices(engine))
        {
            var agent = new Chat.ChatAgent(services, ChatTestHost.StreamingRole(new ScriptedStreamingLlmService()),
                host.Templates, new Chat.ChatOptions());
            await agent.RunTurnAsync(store, store.Create(workspace.RepositoryPath), "pregunta", workspace, _ => { }, CancellationToken.None);
        }

        var steps = WithBranches([.. full.Steps, .. regeneration.Steps, .. engine.Steps]).ToList();
        Assert.Contains(steps, s => s.Name == Chat.ChatAgent.StepName);
        // Los auditores de todos los informes, en los dos pipelines.
        Assert.Equal(2 * Enum.GetValues<ReportType>().Length, steps.Count(s => AnalysisPipelineFactory.TryParseAuditorStepName(s.Name, out _)));
        Assert.Empty(steps.Where(s => s.Timeout == null).Select(s => s.Name));
    }

    [Fact]
    public async Task Without_configuration_the_pipeline_limit_is_240_minutes_and_not_the_framework_default()
    {
        await using var host = await TestHost.CreateAsync();

        var pipeline = AnalysisPipelineFactory.CreatePipeline(host.Templates, host.Roles, new AnalysisPipelineInput(), host.Services);

        Assert.Equal(TimeSpan.FromMinutes(240), pipeline.PipelineTimeout);
        Assert.NotEqual(host.Services.GetRequiredService<IPipeline>().DefaultPipelineTimeout, pipeline.PipelineTimeout);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    public async Task The_engine_never_applies_its_default_timeout_to_the_analysis(string minutes)
    {
        await using var host = await TestHost.CreateAsync(("CodebaseAnalysis:Timeouts:PipelineMinutes", minutes));
        var engine = new CapturingPipeline();
        var input = new AnalysisPipelineInput { RequestedReports = [ReportType.CodeSmells] };

        var full = AnalysisPipelineFactory.CreatePipeline(host.Templates, host.Roles, input, host.Services, engine);
        var regeneration = AnalysisPipelineFactory.CreateRegenerationPipeline(host.Templates, host.Roles, input, synthesis: false, host.Services, engine);
        await full.ExecuteAsync(input, new PipelineContext(null, host.Services));
        await regeneration.ExecuteAsync(input, new PipelineContext(null, host.Services));

        // Un límite finito lo aplica el wrapper (CodebaseAnalysisPipeline) y 0 no pone ninguno: en los dos casos el motor
        // recibe InfiniteTimeSpan, nunca null, que le haría aplicar sus 10 minutos globales.
        var expected = minutes == "0" ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(1);
        Assert.Equal([expected, expected], new[] { full.PipelineTimeout, regeneration.PipelineTimeout });
        Assert.Equal([Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan], engine.Timeouts);
        Assert.Equal([CodebaseAnalysisPipeline.DefaultName, AnalysisPipelineFactory.RegenerationPipelineName], engine.Names);
    }

    // ── Timeout del pipeline ─────────────────────────────────────────────

    [Fact]
    public async Task Exceeding_the_pipeline_limit_on_the_real_engine_fails_with_the_limit_and_the_key_that_controls_it()
    {
        await using var host = await TestHost.CreateAsync();
        var reporter = new CapturingReporter();
        var input = new AnalysisPipelineInput();
        var context = AnalysisRunner.CreateContext(input, host.Services, null, reporter);
        var limit = TimeSpan.FromMilliseconds(300);
        var pipeline = new CodebaseAnalysisPipeline(new Pipeline(), [new WaitingStep()], limit);

        var result = await pipeline.ExecuteAsync(input, context, CancellationToken.None);

        Assert.True(result.HasError);
        Assert.Equal(CodebaseAnalysisPipeline.TimeoutMessage(limit), result.Error?.Message);
        Assert.Equal("El análisis superó el límite de 1 min (CodebaseAnalysis:Timeouts:PipelineMinutes); súbelo o pon 0 para quitarlo.",
            CodebaseAnalysisPipeline.TimeoutMessage(TimeSpan.FromMinutes(1)));
        var reported = Assert.Single(reporter.Entries, e => e.Level == AnalysisLogLevel.Error);
        Assert.Contains(CodebaseAnalysisPipeline.TimeoutMessage(limit), reported.Message);
    }

    [Fact]
    public async Task A_cancellation_by_the_caller_is_not_reported_as_a_timeout()
    {
        await using var host = await TestHost.CreateAsync();
        var reporter = new CapturingReporter();
        var input = new AnalysisPipelineInput();
        var context = AnalysisRunner.CreateContext(input, host.Services, null, reporter);
        var pipeline = new CodebaseAnalysisPipeline(new Pipeline(), [new WaitingStep()], TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await pipeline.ExecuteAsync(input, context, cancellation.Token);

        Assert.True(result.HasError);
        Assert.DoesNotContain(AnalysisTimeouts.SectionName, result.Error?.Message ?? string.Empty);
        Assert.DoesNotContain(reporter.Entries, e => e.Level == AnalysisLogLevel.Error);
    }

    [Fact]
    public async Task An_engine_that_throws_on_the_timeout_also_gets_the_timeout_error()
    {
        await using var host = await TestHost.CreateAsync();
        var reporter = new CapturingReporter();
        var input = new AnalysisPipelineInput();
        var context = AnalysisRunner.CreateContext(input, host.Services, null, reporter);
        var limit = TimeSpan.FromMilliseconds(200);
        var engine = new WaitingPipeline();
        var pipeline = new CodebaseAnalysisPipeline(engine, [new WaitingStep()], limit, AnalysisPipelineFactory.RegenerationPipelineName);

        var result = await pipeline.ExecuteAsync(input, context, CancellationToken.None);

        Assert.Equal(CodebaseAnalysisPipeline.TimeoutMessage(limit), result.Error?.Message);
        Assert.Equal(Timeout.InfiniteTimeSpan, engine.Timeout);
        Assert.Single(reporter.Entries, e => e.Level == AnalysisLogLevel.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_regeneration_pipeline_has_only_the_preparation_the_facets_the_subsystem_preparation_the_requested_auditors_and_optionally_the_synthesis(bool synthesis)
    {
        await using var host = await TestHost.CreateAsync(("CodebaseAnalysis:Timeouts:AuditorMinutes", "9"));
        var input = new AnalysisPipelineInput { RequestedReports = [ReportType.CodeSmells, ReportType.ErroresYBugs] };

        var pipeline = AnalysisPipelineFactory.CreateRegenerationPipeline(host.Templates, host.Roles, input, synthesis, host.Services);

        string[] expected =
        [
            PrepareRegenerationStep.StepName, SqlFacetExtractorStep.StepName, RepositoryInventoryStep.StepName, DomainDiscoveryStep.StepName,
            CoveragePlanningStep.StepName, TimeBudgetStep.StepName, AnalysisPipelineFactory.ParallelReportsStepName
        ];
        if (synthesis)
            expected = [.. expected, ExecutiveSynthesizerStep.StepName];
        Assert.Equal(expected, pipeline.Steps.Select(s => s.Name));
        var reports = Assert.IsType<TolerantReportsParallelStep>(pipeline.Steps.Single(s => s.Name == AnalysisPipelineFactory.ParallelReportsStepName));
        Assert.Equal(["Auditor_CodeSmells", "Auditor_ErroresYBugs"], reports.Branches.Select(b => b.Name));
        Assert.Equal(TimeSpan.FromMinutes(9), reports.Branches[0].Timeout);
        // El envoltorio de modo B arranca con tandas de investigaciones (8 subsistemas de 4 en 4: dos) y la consolidación, de 9 min cada una,
        // hasta que el presupuesto de tiempo le escribe el suyo.
        Assert.Equal(TimeSpan.FromMinutes(9 * 3), reports.Branches[1].Timeout);
        Assert.Equal(TimeSpan.FromMinutes(240), pipeline.PipelineTimeout);
    }

    [Fact]
    public async Task The_regeneration_pipeline_needs_at_least_one_report()
    {
        await using var host = await TestHost.CreateAsync();

        Assert.Throws<ArgumentException>(() =>
            AnalysisPipelineFactory.CreateRegenerationPipeline(host.Templates, host.Roles, new AnalysisPipelineInput(), true, host.Services));
    }

    // ── PrepareRegenerationStep ──────────────────────────────────────────

    [Fact]
    public async Task Preparation_fails_with_a_clear_message_without_a_database()
    {
        using var repo = new TempDirectory();
        await using var host = await TestHost.CreateAsync();
        var input = AnalysisRunOptions.BuildInput(host.Configuration, repo.Path, _ => { });
        input.RequestedReports = [ReportType.CodeSmells];
        var context = AnalysisRunner.CreateContext(input, host.Services, null, new CapturingReporter());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PrepareRegenerationStep().ExecuteAsync(input, context, 1, null, CancellationToken.None));

        Assert.Contains("No existe la base de datos del grafo", error.Message);
        Assert.Contains("lanza un análisis completo", error.Message);
        Assert.False(File.Exists(input.DatabasePath));
    }

    [Fact]
    public async Task Preparation_fails_with_a_clear_message_without_a_structural_graph()
    {
        using var repo = new TempDirectory();
        await using var host = await TestHost.CreateAsync();
        var input = AnalysisRunOptions.BuildInput(host.Configuration, repo.Path, _ => { });
        await GraphSchema.EnsureAsync(input.DatabasePath, CancellationToken.None);
        var context = AnalysisRunner.CreateContext(input, host.Services, null, new CapturingReporter());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PrepareRegenerationStep().ExecuteAsync(input, context, 1, null, CancellationToken.None));

        Assert.Contains("no contiene un grafo estructural", error.Message);
    }

    [Fact]
    public async Task Preparation_sets_the_context_and_retires_the_catalog_reports_from_the_graph()
    {
        using var repo = new TempDirectory();
        await using var host = await TestHost.CreateAsync();
        var input = AnalysisRunOptions.BuildInput(host.Configuration, repo.Path, _ => { });
        input.RequestedReports = [ReportType.CodeSmells];
        await CreateEnrichedGraphAsync(input.DatabasePath);
        var paths = new KnowledgePaths(input.RepositoryPath, input.OutputReportsPath);
        var report = SourceRef.Report("02_code_smells.md");
        await WriteReportAsync(input, "02_code_smells.md", "# Viejo\n\n<!-- analysis-meta: health=Red -->\n");
        await KnowledgeGraphIngestor.AddAsync(input.DatabasePath, paths, report, CancellationToken.None);
        var context = AnalysisRunner.CreateContext(input, host.Services, null, new CapturingReporter());

        var status = await new PrepareRegenerationStep().ExecuteAsync(input, context, 1, null, CancellationToken.None);

        Assert.True(status.ExistedBefore);
        Assert.True(status.IsEnriched);
        Assert.True(status.TotalNodes > 0);
        Assert.Equal(input.DatabasePath, status.DatabasePath);
        Assert.Same(input, context.Get<AnalysisPipelineInput>(AnalysisContextKeys.PipelineInput));
        Assert.Equal(input.DatabasePath, context.Get<string>(AnalysisContextKeys.CbmDbPath));
        Assert.NotNull(context.Get<KnowledgePaths>(AnalysisContextKeys.KnowledgePaths));
        Assert.Equal(true, context.GetMetadata(AnalysisContextKeys.CatalogReportsRetired));
        Assert.Equal(GraphKnowledgeStates.Pending, (await KnowledgeGraphIngestor.GetStateAsync(input.DatabasePath, paths, report, CancellationToken.None)).State);
    }

    // ── Regeneración de extremo a extremo ────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task After_a_full_run_regenerating_one_report_runs_only_its_steps_and_rewrites_only_its_file(bool synthesis)
    {
        using var repo = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(repo.Path, "src"));
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "src", "Sample.cs"), "namespace Demo; public class Sample { }");
        // Sin límite de pipeline: el motor real recibe Timeout.InfiniteTimeSpan y no debe cortar ni lanzar.
        await using var host = await TestHost.CreateAsync(("CodebaseAnalysis:Timeouts:PipelineMinutes", "0"));
        var input = AnalysisRunOptions.BuildInput(host.Configuration, repo.Path, _ => { });
        await CreateEnrichedGraphAsync(input.DatabasePath);
        AnalysisToolRegistration.Register(input);

        var full = await AnalysisRunner.RunAsync(input, host.Roles, host.Services, new EventChannel(), new CapturingReporter(), CancellationToken.None);
        Assert.False(full.HasError, full.Error?.Message);
        // 9 informes y la síntesis, más el nombrado del único subsistema y su investigación en cada uno de los 4 informes en modo B.
        Assert.Equal(10 + 1 + 4, host.Llm.Calls);

        // El informe a regenerar queda con otro contenido y en el grafo; los demás, con su fecha anterior.
        const string target = "02_code_smells.md";
        await WriteReportAsync(input, target, "# Viejo\n\n<!-- analysis-meta: health=Red -->\n");
        var paths = new KnowledgePaths(input.RepositoryPath, input.OutputReportsPath);
        await KnowledgeGraphIngestor.AddAsync(input.DatabasePath, paths, SourceRef.Report(target), CancellationToken.None);
        var old = DateTime.UtcNow.AddHours(-1);
        var others = Directory.GetFiles(input.OutputReportsPath, "*.md").Where(f => Path.GetFileName(f) != target).ToList();
        foreach (var file in others)
            File.SetLastWriteTimeUtc(file, old);
        var contents = others.ToDictionary(f => f, File.ReadAllText);

        var events = new List<IProgressEvent>();
        var channel = new EventChannel();
        channel.Subscribe((evt, _) =>
        {
            lock (events)
            {
                events.Add(evt);
            }
            return Task.CompletedTask;
        });
        var result = await AnalysisRunner.RegenerateAsync(
            input, [ReportType.CodeSmells], synthesis, host.Roles, host.Services, channel, new CapturingReporter(), CancellationToken.None);

        Assert.False(result.HasError, result.Error?.Message);
        if (synthesis)
            Assert.Empty(Assert.IsType<AnalysisPipelineFinalResult>(result).FailedReports);
        else
            Assert.IsType<ParallelResult>(result);
        // Los 15 de antes, el informe regenerado y la síntesis, y el nombrado del mapa de dominios: este grafo sintético no tiene tabla
        // projects, así que no hay huella con la que saber que el mapa guardado sigue valiendo y se calcula de nuevo (la reutilización con
        // huella la prueba PreparationPipelineTests).
        Assert.Equal(15 + 1 + (synthesis ? 2 : 1), host.Llm.Calls);
        // input no se toca: la regeneración trabaja sobre una copia.
        Assert.Equal(9, input.RequestedReports.Count);

        List<IProgressEvent> received;
        lock (events)
        {
            received = [.. events];
        }
        string[] expectedSteps =
        [
            PrepareRegenerationStep.StepName, SqlFacetExtractorStep.StepName, RepositoryInventoryStep.StepName, DomainDiscoveryStep.StepName,
            CoveragePlanningStep.StepName, TimeBudgetStep.StepName, AnalysisPipelineFactory.ParallelReportsStepName, "Auditor_CodeSmells"
        ];
        if (synthesis)
            expectedSteps = [.. expectedSteps, ExecutiveSynthesizerStep.StepName];
        Assert.Equal(expectedSteps, received.OfType<StepStartedEvent>().Select(e => e.StepName));
        Assert.Equal(AnalysisPipelineFactory.RegenerationPipelineName, Assert.Single(received.OfType<PipelineStartedEvent>()).PipelineName);
        var written = received.OfType<StepProgressEvent>().Where(e => e.EventType == AnalysisEventTypes.ReportWritten)
            .Select(e => (string)e.Metadata![AnalysisEventTypes.MetadataKeys.FileName]!);
        Assert.Equal(synthesis ? [target, ReportCatalog.ExecutiveReportFileName] : [target], written);

        Assert.Equal(CannedReport.Trim(), ProvenanceKeys.Strip(await File.ReadAllTextAsync(Path.Combine(input.OutputReportsPath, target))).Trim());
        foreach (var file in others)
        {
            var isExecutive = Path.GetFileName(file) == ReportCatalog.ExecutiveReportFileName;
            if (synthesis && isExecutive)
            {
                Assert.True(File.GetLastWriteTimeUtc(file) > old, "el resumen ejecutivo debería haberse reescrito");
                continue;
            }
            Assert.Equal(old, File.GetLastWriteTimeUtc(file));
            Assert.Equal(contents[file], await File.ReadAllTextAsync(file));
        }

        // El informe retirado vuelve al grafo con su contenido nuevo: en la síntesis o, sin ella, al terminar el pipeline.
        Assert.Equal(GraphKnowledgeStates.Present,
            (await KnowledgeGraphIngestor.GetStateAsync(input.DatabasePath, paths, SourceRef.Report(target), CancellationToken.None)).State);
    }

    // ── Soporte ──────────────────────────────────────────────────────────

    private static async Task CreateEnrichedGraphAsync(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        await GraphSchema.EnsureAsync(dbPath, CancellationToken.None);
        await using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO nodes (project, label, name, qualified_name, file_path, start_line, end_line, properties)
            VALUES ('Codebase', 'Class', 'Sample', 'Demo.Sample', 'src/Sample.cs', 1, 10, '{"cyclomatic":3}');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task WriteReportAsync(AnalysisPipelineInput input, string fileName, string content)
    {
        Directory.CreateDirectory(input.OutputReportsPath);
        await File.WriteAllTextAsync(Path.Combine(input.OutputReportsPath, fileName), content);
    }

    /// <summary>Contenedor con la configuración pedida, el proveedor LLM falso y los roles del análisis.</summary>
    private sealed class TestHost : IAsyncDisposable
    {
        private TestHost(IConfiguration configuration, ServiceProvider services, CannedLlmService llm)
        {
            Configuration = configuration;
            Services = services;
            Llm = llm;
            Roles = LlmRoleFactory.BuildRoles(services, _ => null, _ => { });
        }

        public IConfiguration Configuration { get; }
        public ServiceProvider Services { get; }
        public CannedLlmService Llm { get; }
        public Dictionary<string, LlmRole> Roles { get; }
        public AITaskAgent.Support.Template.ITemplateProvider Templates { get; } = AnalysisPipelineFactory.CreateDefaultTemplateProvider();

        public static Task<TestHost> CreateAsync(params (string, string?)[] pairs)
        {
            var configuration = TestConfig.FromPairs([.. FakeProvider, .. pairs]);
            var llm = new CannedLlmService(CannedReport);
            var services = new ServiceCollection();
            services.AddSingleton(configuration);
            services.AddLogging();
            services.AddSingleton<ILlmService>(llm);
            services.AddAITaskAgent();
            var provider = services.BuildServiceProvider();
            provider.UseAITaskAgent();
            return Task.FromResult(new TestHost(configuration, provider, llm));
        }

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    /// <summary>Paso que espera hasta que se cancela su token, sin límite propio.</summary>
    private sealed class WaitingStep() : TypedStep<AnalysisPipelineInput, AnalysisPipelineInput>("WaitingStep")
    {
        public override TimeSpan? Timeout { get; set; } = System.Threading.Timeout.InfiniteTimeSpan;

        protected override async Task<AnalysisPipelineInput> ExecuteAsync(
            AnalysisPipelineInput input, PipelineContext context, int Attempt, AnalysisPipelineInput? LastStepResult, CancellationToken cancellationToken)
        {
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            return input;
        }
    }

    /// <summary>Motor que espera a que se cancele el token y deja escapar la excepción, como un motor que no la captura.</summary>
    private sealed class WaitingPipeline : IPipeline
    {
        public TimeSpan? Timeout { get; private set; }
        public TimeSpan? DefaultPipelineTimeout => TimeSpan.FromMinutes(10);
        public TimeSpan? DefaultStepTimeout => TimeSpan.FromMinutes(1);

        public async Task<IStepResult> ExecuteAsync<T>(
            string name,
            IReadOnlyList<IStep> steps,
            T input,
            PipelineContext? context = null,
            TimeSpan? pipelineTimeout = null,
            CancellationToken cancellationToken = default,
            IEnumerable<Func<IProgressEvent, CancellationToken, Task>>? subscribers = null)
        {
            Timeout = pipelineTimeout;
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            return new AnalysisPipelineFinalResult();
        }
    }

    /// <summary>Motor que no ejecuta nada: guarda el nombre, los pasos y el <c>pipelineTimeout</c> de cada ejecución.</summary>
    private sealed class CapturingPipeline : IPipeline
    {
        public List<TimeSpan?> Timeouts { get; } = [];
        public List<string> Names { get; } = [];
        public List<IStep> Steps { get; } = [];
        public TimeSpan? DefaultPipelineTimeout => TimeSpan.FromMinutes(10);
        public TimeSpan? DefaultStepTimeout => TimeSpan.FromMinutes(1);

        public Task<IStepResult> ExecuteAsync<T>(
            string name,
            IReadOnlyList<IStep> steps,
            T input,
            PipelineContext? context = null,
            TimeSpan? pipelineTimeout = null,
            CancellationToken cancellationToken = default,
            IEnumerable<Func<IProgressEvent, CancellationToken, Task>>? subscribers = null)
        {
            Names.Add(name);
            Timeouts.Add(pipelineTimeout);
            Steps.AddRange(steps);
            return Task.FromResult<IStepResult>(new AnalysisPipelineFinalResult());
        }
    }
}
