using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AITaskAgent.Configuration;
using AITaskAgent.LLM.Abstractions;
using CodebaseAnalysis.Core.Enrichment.AnalysisGraph;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.SharedPass;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tests.Enrichment;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Cableado de la pasada compartida (<see cref="SharedPassStep"/>): apagada, el pipeline y los roles exigidos son los de siempre;
/// encendida, el paso va tras la preparación y antes de los informes y exige sus dos roles; y D1 → D2 → D3 de punta a punta sobre
/// un repositorio temporal con un LLM falso. Ningún test llama a un LLM real.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class SharedPassStepTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDirectory _temp = new();
    private string Repo => Path.Combine(_temp.Path, "repo");
    private string Db => Path.Combine(_temp.Path, "codebase.db");
    private string Reports => Path.Combine(_temp.Path, "reports");

    public void Dispose() => _temp.Dispose();

    private static (string, string?)[] SharedPassPairs(bool? enabled, bool declareRoles, string serviceType = "canned")
    {
        string[] roles = declareRoles
            ? [.. LlmRoleFactory.RequiredRoles, SharedPassOptions.DefaultReaderRole, SharedPassOptions.DefaultJudgeRole]
            : [.. LlmRoleFactory.RequiredRoles];
        var pairs = FakeLlmConfig.Pairs(serviceType, roles).ToList();
        if (enabled is bool on)
            pairs.Add(("CodebaseAnalysis:SharedPass:Enabled", on ? "true" : "false"));
        return [.. pairs];
    }

    private static async Task<List<string>> PipelineStepNamesAsync(string repo, params (string, string?)[] pairs)
    {
        var configuration = TestConfig.FromPairs(pairs);
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddSingleton<ILlmService>(new CannedLlmService("{}"));
        services.AddAITaskAgent();
        await using var provider = services.BuildServiceProvider();
        provider.UseAITaskAgent();
        var roles = LlmRoleFactory.BuildRoles(provider, _ => null, _ => { });
        var input = AnalysisRunOptions.BuildInput(configuration, repo, _ => { });
        AnalysisToolRegistration.Register(input);
        var pipeline = AnalysisPipelineFactory.CreatePipeline(AnalysisPipelineFactory.CreateDefaultTemplateProvider(), roles, input, provider);
        return [.. pipeline.Steps.Select(s => s.Name)];
    }

    /// <summary>El pipeline de hoy, con la preparación por subsistema (la de por defecto).</summary>
    private static readonly string[] TodaysPipeline =
    [
        DetectAndIndexGraphStep.StepName, LanguageEnricherStep.StepName, DotNetEnricherStep.StepName, SqlFacetExtractorStep.StepName,
        RepositoryInventoryStep.StepName, DomainDiscoveryStep.StepName, CoveragePlanningStep.StepName, TimeBudgetStep.StepName,
        AnalysisPipelineFactory.ParallelReportsStepName, ExecutiveSynthesizerStep.StepName
    ];

    [Fact]
    public async Task Disabled_by_default_or_explicitly_the_pipeline_and_the_required_roles_are_unchanged()
    {
        // Sin la sección y con Enabled=false; en los dos casos sin declarar los roles de la pasada.
        var absent = await PipelineStepNamesAsync(_temp.Path, SharedPassPairs(enabled: null, declareRoles: false));
        var disabled = await PipelineStepNamesAsync(_temp.Path, SharedPassPairs(enabled: false, declareRoles: false));

        Assert.Equal(TodaysPipeline, absent);
        Assert.Equal(TodaysPipeline, disabled);
        Assert.False(new SharedPassOptions().Enabled);
        Assert.Empty(LlmRoleFactory.SharedPassRoles(TestConfig.FromPairs(SharedPassPairs(enabled: false, declareRoles: false))));
    }

    [Fact]
    public async Task Enabled_the_step_goes_after_the_preparation_and_before_the_reports_and_needs_both_roles()
    {
        var names = await PipelineStepNamesAsync(_temp.Path, SharedPassPairs(enabled: true, declareRoles: true));

        Assert.Equal([.. TodaysPipeline[..8], SharedPassStep.StepName, .. TodaysPipeline[8..]], names);
        Assert.Equal([SharedPassOptions.DefaultReaderRole, SharedPassOptions.DefaultJudgeRole],
            LlmRoleFactory.SharedPassRoles(TestConfig.FromPairs(SharedPassPairs(enabled: true, declareRoles: false))));

        // Encendida sin declarar sus roles, el arranque falla nombrándolos (como con cualquier rol del pipeline).
        var configuration = TestConfig.FromPairs(SharedPassPairs(enabled: true, declareRoles: false));
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddSingleton<ILlmService>(new CannedLlmService("{}"));
        services.AddAITaskAgent();
        await using var provider = services.BuildServiceProvider();
        provider.UseAITaskAgent();
        var ex = Assert.Throws<InvalidOperationException>(() => LlmRoleFactory.BuildRoles(provider, _ => null, _ => { }));
        Assert.Contains("SharedPassReader", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SharedPassJudge", ex.Message, StringComparison.Ordinal);
        Assert.Equal(AnalysisPhases.Facets, AnalysisEventMapper.PhaseOfStep(SharedPassStep.StepName));
    }

    // ───────────────────────────── De punta a punta ─────────────────────────────

    private const string AlphaCode = """
        namespace Demo;
        public class Alpha
        {
            public void Run()
            {
                try { File.Delete(_path); }
                catch { }
            }

            public void Stop() => _running = false;
        }
        """;

    private const string BetaCode = """
        class Beta
        {
            void Go()
            {
                new Alpha().Run();
            }
        }
        """;

    private void WriteFile(string relative, string text)
    {
        var path = Path.Combine(Repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.Replace("\r\n", "\n") + "\n", new UTF8Encoding(false));
    }

    private async Task<DomainMap> RepoAsync()
    {
        using (var graph = await CbmTestGraph.CreateAsync(Db, "demo"))
        {
            await graph.NodeAsync("File", "Alpha.cs", "demo.src.a.Alpha.cs", "src/a/Alpha.cs");
            var alpha = await graph.NodeAsync("Class", "Alpha", "demo.Alpha", "src/a/Alpha.cs", 2, 11);
            var run = await graph.NodeAsync("Method", "Run", "demo.Alpha.Run", "src/a/Alpha.cs", 4, 8, """{"detected_issues":["empty-catch"],"accessibility":"public"}""");
            var stop = await graph.NodeAsync("Method", "Stop", "demo.Alpha.Stop", "src/a/Alpha.cs", 10, 10);
            await graph.EdgeAsync(alpha, run, "DEFINES_METHOD");
            await graph.EdgeAsync(alpha, stop, "DEFINES_METHOD");
            var beta = await graph.NodeAsync("Class", "Beta", "demo.Beta", "src/b/Beta.cs", 1, 7,
                """{"dead_code_candidate":true,"dead_code_confidence":"medium","dead_code_reason":"sin referencias"}""");
            var go = await graph.NodeAsync("Method", "Go", "demo.Beta.Go", "src/b/Beta.cs", 3, 6, """{"is_entry_point":true}""");
            await graph.EdgeAsync(beta, go, "DEFINES_METHOD");
            await graph.EdgeAsync(go, run, "CALLS");
        }
        WriteFile("src/a/Alpha.cs", AlphaCode);
        WriteFile("src/b/Beta.cs", BetaCode);
        return new DomainMap
        {
            Subsystems = [new Subsystem { Id = "core", Name = "Core", Description = "desc", Kind = SubsystemKinds.Capability, Files = ["src/a/Alpha.cs", "src/b/Beta.cs"] }]
        };
    }

    private static JsonObject Member(string name, int start, int end) => new()
    {
        ["name"] = name, ["startLine"] = start, ["endLine"] = end, ["does"] = $"hace {name}", ["inputs"] = "nada", ["outputs"] = "nada",
        ["sideEffects"] = "ninguno", ["throws"] = "ninguno"
    };

    private static string Reading() => new JsonObject
    {
        ["batch"] = "core",
        ["files"] = new JsonArray(
            new JsonObject { ["path"] = "src/a/Alpha.cs", ["role"] = "service", ["summary"] = "Alpha borra un fichero." },
            new JsonObject { ["path"] = "src/b/Beta.cs", ["role"] = "caller", ["summary"] = "Beta llama a Alpha." }),
        ["types"] = new JsonArray(
            new JsonObject
            {
                ["name"] = "Alpha", ["kind"] = "class", ["path"] = "src/a/Alpha.cs", ["startLine"] = 2, ["endLine"] = 11, ["purpose"] = "Servicio.",
                ["collaborators"] = new JsonArray(), ["members"] = new JsonArray(Member("Run()", 4, 8), Member("Stop()", 10, 10))
            },
            new JsonObject
            {
                ["name"] = "Beta", ["kind"] = "class", ["path"] = "src/b/Beta.cs", ["startLine"] = 1, ["endLine"] = 7, ["purpose"] = "Llamador.",
                ["collaborators"] = new JsonArray(), ["members"] = new JsonArray(Member("Go()", 3, 6))
            }),
        ["findings"] = new JsonArray(
            new JsonObject
            {
                ["localId"] = "F1", ["kind"] = "bug", ["category"] = "excepcion-tragada", ["title"] = "catch vacío en Run", ["rationale"] = "Traga la excepción.",
                ["anchor"] = new JsonObject { ["path"] = "src/a/Alpha.cs", ["startLine"] = 7, ["endLine"] = 7, ["quote"] = "catch { }" },
                ["instances"] = new JsonArray(), ["symbol"] = "Alpha.Run", ["seedRef"] = "Alpha.Run ISSUE[empty-catch]",
                ["activationPath"] = "Go con un fichero bloqueado", ["impact"] = 3, ["likelihood"] = 3, ["confidence"] = 0.8
            },
            new JsonObject
            {
                ["localId"] = "F2", ["kind"] = "debt", ["category"] = "estado", ["title"] = "Stop sin sincronización", ["rationale"] = "Campo sin volatile.",
                ["anchor"] = new JsonObject { ["path"] = "src/a/Alpha.cs", ["startLine"] = 10, ["endLine"] = 10, ["quote"] = "_running = false" },
                ["instances"] = new JsonArray(), ["symbol"] = "Alpha.Stop", ["activationPath"] = "uso normal", ["impact"] = 2, ["likelihood"] = 2, ["confidence"] = 0.9
            }),
        ["seedVerdicts"] = new JsonArray(
            new JsonObject { ["seed"] = "Alpha.Run ISSUE[empty-catch]", ["verdict"] = "confirmed", ["reason"] = "catch vacío en L7." },
            new JsonObject { ["seed"] = "Beta DEAD?(medium)", ["verdict"] = "needs_context", ["reason"] = "uso externo posible." }),
        ["relations"] = new JsonArray(),
        ["openQuestions"] = new JsonArray(new JsonObject { ["symbol"] = "Alpha._path", ["question"] = "¿De dónde sale la ruta?", ["needs"] = new JsonArray("src/c/Config.cs") })
    }.ToJsonString();

    [Fact]
    public async Task Enabled_it_reads_consolidates_judges_and_persists_a_small_repository()
    {
        var map = await RepoAsync();
        var llm = new SharedPassFakeLlm((_, system, user) => SharedPassFakeLlm.IsJudge(system)
            ? (SharedPassFakeLlm.Verdicts(user, new Dictionary<string, JsonObject>
            {
                ["catch vacío en Run"] = new() { ["verdict"] = "upgrade", ["severity"] = "P1", ["activation"] = "default", ["severityBasis"] = "Go es punto de entrada", ["reason"] = "L7 traga todo." }
            }), 0.02m)
            : (Reading(), 0.05m));
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(TestConfig.FromPairs(PricedPairs(llm.Name)));
        services.AddLogging();
        services.AddSingleton<ILlmService>(llm);
        services.AddAITaskAgent();
        await using var provider = services.BuildServiceProvider();
        provider.UseAITaskAgent();
        var roles = LlmRoleFactory.BuildRoles(provider, _ => null, _ => { }, [SharedPassOptions.DefaultReaderRole, SharedPassOptions.DefaultJudgeRole]);
        var reporter = new CapturingReporter();
        var input = new AnalysisPipelineInput { RepositoryPath = Repo, DatabasePath = Db, OutputReportsPath = Reports };
        var context = AnalysisRunner.CreateContext(input, provider, null, reporter);
        context.Set(AnalysisContextKeys.DomainMap, map);
        var options = new SharedPassOptions { Enabled = true };
        var step = AnalysisPipelineFactory.CreateSharedPassStep(AnalysisPipelineFactory.CreateDefaultTemplateProvider(), roles, options);
        var facets = new ReportFacetInput { RepositoryPath = Repo, DatabasePath = Db, OutputReportsPath = Reports };

        var result = await step.ExecuteAsync(facets, context, 1, null, CancellationToken.None);

        foreach (var (level, source, message) in reporter.Entries)
            output.WriteLine($"[{level}] {source}: {message}");
        Assert.Same(facets, result);
        Assert.Equal(2, llm.Calls.Count);
        Assert.False(SharedPassFakeLlm.IsJudge(llm.Calls[0].System));
        Assert.True(SharedPassFakeLlm.IsJudge(llm.Calls[1].System));

        var registry = context.Get<ConsolidatedRegistry>(AnalysisContextKeys.SharedPassRegistry);
        Assert.NotNull(registry);
        Assert.Equal(2, registry.Entries.Count);
        var bug = registry.Entries[0];
        // Run tiene un llamador que es punto de entrada (Go): exposición 4, S = 3×3×4 = 36 → P1 por rúbrica; el juez lo confirma
        // aunque diga «upgrade» (la severidad es la misma) y queda reverificado.
        Assert.Equal(("REG-001", "P1", true, "confirm"), (bug.Id, bug.Severity, bug.Reverified, bug.JudgeVerdict));
        Assert.Equal(4, bug.Exposure.Score);
        Assert.Equal("Go es punto de entrada", bug.SeverityBasis);
        var debt = registry.Entries[1];
        Assert.Equal(("REG-002", "P3", false), (debt.Id, debt.Severity, debt.Reverified));
        Assert.Contains(registry.Notes, n => n.Contains("Semillas confirmadas: 1; 0 sin registro", StringComparison.Ordinal));

        // registry.json en la carpeta de informes y el registro en el grafo, con un Run de ámbito shared-pass.
        var json = File.ReadAllText(Path.Combine(Reports, SharedPassRegistry.FileName));
        using var doc = JsonDocument.Parse(json);
        var summary = doc.RootElement.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("total").GetInt32());
        Assert.Equal(1, summary.GetProperty("readerCalls").GetInt32());
        Assert.Equal(1, summary.GetProperty("judge").GetProperty("reviewed").GetInt32());
        Assert.Equal(["REG-001", "REG-002"], LlmRegistryJson.ParseRegistry(json).Select(e => e.Id));

        using var g = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Db}");
        await g.OpenAsync();
        using var cmd = g.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT count(*) FROM nodes WHERE label = 'Finding'),
                   (SELECT json_extract(properties, '$.scope') FROM nodes WHERE label = 'Run'),
                   (SELECT json_extract(properties, '$.llm_does') FROM nodes WHERE qualified_name = 'demo.Alpha.Run')
            """;
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt64(0));
        Assert.Equal(SharedPassRegistry.Scope, reader.GetString(1));
        Assert.Equal("hace Run()", reader.GetString(2));
        Assert.DoesNotContain(reporter.Entries, e => e.Message.Contains("falló", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enabled_it_emits_its_progress_as_structured_events_that_map_to_sharedpass_progress()
    {
        var map = await RepoAsync();
        var llm = new SharedPassFakeLlm((_, system, user) => SharedPassFakeLlm.IsJudge(system)
            ? (SharedPassFakeLlm.Verdicts(user, new Dictionary<string, JsonObject>
            {
                ["catch vacío en Run"] = new() { ["verdict"] = "confirm", ["severity"] = "P1", ["activation"] = "default", ["severityBasis"] = "Go es punto de entrada", ["reason"] = "L7 traga todo." }
            }), 0.02m)
            : (Reading(), 0.05m));
        var (provider, roles) = Roles(llm, PricedPairs(llm.Name));
        await using var _ = provider;
        var channel = new AITaskAgent.Observability.EventChannel();
        var received = new List<AITaskAgent.Observability.IProgressEvent>();
        channel.Subscribe((evt, _) =>
        {
            lock (received)
                received.Add(evt);
            return Task.CompletedTask;
        });
        var context = AnalysisRunner.CreateContext(new AnalysisPipelineInput { RepositoryPath = Repo, DatabasePath = Db, OutputReportsPath = Reports }, provider, channel, new CapturingReporter());
        context.Set(AnalysisContextKeys.DomainMap, map);
        var step = AnalysisPipelineFactory.CreateSharedPassStep(AnalysisPipelineFactory.CreateDefaultTemplateProvider(), roles, new SharedPassOptions { Enabled = true });

        await step.ExecuteAsync(new ReportFacetInput { RepositoryPath = Repo, DatabasePath = Db, OutputReportsPath = Reports }, context, 1, null, CancellationToken.None);
        await channel.DisposeAsync();

        var mapper = new AnalysisEventMapper("run");
        List<AnalysisEventDto> progress;
        lock (received)
            progress = [.. received.Select(mapper.Map).OfType<AnalysisEventDto>().Where(d => d.Type == AnalysisEventDtoTypes.SharedPassProgress)];
        var phases = progress.Select(d => (string)d.Data!["phase"]!).Distinct().ToList();
        Assert.Equal(["reading", "consolidating", "judging", "saving", "done"], phases);
        Assert.All(progress, d => Assert.Equal((SharedPassStep.StepName, AnalysisPhases.Facets), (d.Source, d.Phase)));
        Assert.All(progress, d => Assert.False(d.Data!.ContainsKey(AnalysisEventTypes.MetadataKeys.Level)));

        // El lote pasa por pendiente, leyendo y completo; el último evento trae los totales, el juez y las severidades.
        var statuses = progress.Select(d => (string)((Dictionary<string, object?>)((List<object?>)d.Data!["batches"]!)[0]!)["status"]!).Distinct().ToList();
        Assert.Equal(["pending", "reading", "complete"], statuses);
        var last = progress[^1].Data!;
        var batch = (Dictionary<string, object?>)((List<object?>)last["batches"]!)[0]!;
        Assert.Equal(("core", 2, 1, 0.05m, 2), ((string)batch["id"]!, (int)batch["files"]!, (int)batch["calls"]!, (decimal)batch["costUsd"]!, (int?)batch["findings"]));
        var totals = (Dictionary<string, object?>)last["totals"]!;
        Assert.Equal((0.07m, 2, 25d, 90d), ((decimal)totals["costUsd"]!, (int)totals["calls"]!, (double?)totals["maxCostUsd"], (double?)totals["maxMinutes"]));
        var judge = (Dictionary<string, object?>)last["judge"]!;
        Assert.Equal((1, 1), ((int)judge["candidates"]!, (int)judge["tandasDone"]!));
        var severities = (Dictionary<string, object?>)((Dictionary<string, object?>)last["result"]!)["severities"]!;
        Assert.Equal([0, 1, 0, 1], new[] { "P0", "P1", "P2", "P3" }.Select(s => (int)severities[s]!));
        Assert.Null(last["stopReason"]);
        Assert.Equal("success", progress[^1].Level);
    }

    /// <summary>El lector y el juez sobre el servicio <paramref name="serviceType"/>, con <c>fake-model</c> con precio.</summary>
    private static (string, string?)[] PricedPairs(string serviceType) =>
        [.. FakeLlmConfig.Pairs(serviceType, SharedPassOptions.DefaultReaderRole, SharedPassOptions.DefaultJudgeRole), .. SlowSharedPassLlm.Prices(1m, 2m)];

    private static (ServiceProvider Provider, Dictionary<string, AITaskAgent.LLM.Configuration.LlmRole> Roles) Roles(ILlmService llm, (string, string?)[] pairs)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(TestConfig.FromPairs(pairs));
        services.AddLogging();
        services.AddSingleton(llm);
        services.AddAITaskAgent();
        var provider = services.BuildServiceProvider();
        provider.UseAITaskAgent();
        return (provider, LlmRoleFactory.BuildRoles(provider, _ => null, _ => { }, [SharedPassOptions.DefaultReaderRole, SharedPassOptions.DefaultJudgeRole]));
    }

    [Fact]
    public async Task Enabled_with_a_role_whose_model_has_no_price_it_warns_and_does_not_run()
    {
        var map = await RepoAsync();
        var llm = new SharedPassFakeLlm((_, _, _) => ("{}", 0m));
        var (provider, roles) = Roles(llm, FakeLlmConfig.Pairs(llm.Name, SharedPassOptions.DefaultReaderRole, SharedPassOptions.DefaultJudgeRole));
        await using var _ = provider;
        var reporter = new CapturingReporter();
        var context = AnalysisRunner.CreateContext(new AnalysisPipelineInput { RepositoryPath = Repo, DatabasePath = Db, OutputReportsPath = Reports }, provider, null, reporter);
        context.Set(AnalysisContextKeys.DomainMap, map);
        var step = AnalysisPipelineFactory.CreateSharedPassStep(AnalysisPipelineFactory.CreateDefaultTemplateProvider(), roles, new SharedPassOptions { Enabled = true });
        var facets = new ReportFacetInput { RepositoryPath = Repo, DatabasePath = Db, OutputReportsPath = Reports };

        var result = await step.ExecuteAsync(facets, context, 1, null, CancellationToken.None);

        Assert.Same(facets, result);
        Assert.Empty(llm.Calls);
        Assert.Null(context.Get<ConsolidatedRegistry>(AnalysisContextKeys.SharedPassRegistry));
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Message.Contains("no tiene precio", StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_its_deadline_expires_the_pass_saves_what_it_has_and_the_analysis_goes_on()
    {
        var map = await RepoAsync();
        var llm = new SlowSharedPassLlm(TimeSpan.FromSeconds(60));
        var (provider, roles) = Roles(llm, PricedPairs(llm.Name));
        await using var _ = provider;
        var reporter = new CapturingReporter();
        var context = AnalysisRunner.CreateContext(new AnalysisPipelineInput { RepositoryPath = Repo, DatabasePath = Db, OutputReportsPath = Reports }, provider, null, reporter);
        context.Set(AnalysisContextKeys.DomainMap, map);
        // 0,005 min = 300 ms: la primera lectura no llega a terminar.
        var step = AnalysisPipelineFactory.CreateSharedPassStep(AnalysisPipelineFactory.CreateDefaultTemplateProvider(), roles,
            new SharedPassOptions { Enabled = true, MaxMinutes = 0.005 });
        var facets = new ReportFacetInput { RepositoryPath = Repo, DatabasePath = Db, OutputReportsPath = Reports };

        var started = DateTime.UtcNow;
        var result = await step.ExecuteAsync(facets, context, 1, null, CancellationToken.None);

        foreach (var (level, source, message) in reporter.Entries)
            output.WriteLine($"[{level}] {source}: {message}");
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
        Assert.Same(facets, result);
        Assert.Equal(System.Threading.Timeout.InfiniteTimeSpan, step.Timeout);
        Assert.Equal(1, llm.Calls);
        Assert.NotNull(context.Get<ConsolidatedRegistry>(AnalysisContextKeys.SharedPassRegistry));
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Message.Contains("plazo de la pasada agotado", StringComparison.Ordinal));
        Assert.DoesNotContain(reporter.Entries, e => e.Message.Contains("La pasada compartida falló", StringComparison.Ordinal));

        // El registro se guarda con el motivo del corte y la llamada cortada cuenta lo enviado.
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Reports, SharedPassRegistry.FileName)));
        var summary = doc.RootElement.GetProperty("summary");
        Assert.Contains("plazo de la pasada agotado", summary.GetProperty("stopReason").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, summary.GetProperty("readerCalls").GetInt32());
        Assert.True(summary.GetProperty("readerCostUsd").GetDecimal() > 0);
        Assert.True(summary.GetProperty("coverageGaps").GetInt32() > 0);
    }

    [Fact]
    public async Task The_callers_cancellation_still_propagates()
    {
        var map = await RepoAsync();
        var llm = new SlowSharedPassLlm(TimeSpan.FromSeconds(60));
        var (provider, roles) = Roles(llm, PricedPairs(llm.Name));
        await using var _ = provider;
        var context = AnalysisRunner.CreateContext(new AnalysisPipelineInput { RepositoryPath = Repo, DatabasePath = Db, OutputReportsPath = Reports }, provider, null, new CapturingReporter());
        context.Set(AnalysisContextKeys.DomainMap, map);
        var step = AnalysisPipelineFactory.CreateSharedPassStep(AnalysisPipelineFactory.CreateDefaultTemplateProvider(), roles, new SharedPassOptions { Enabled = true });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            step.ExecuteAsync(new ReportFacetInput { RepositoryPath = Repo, DatabasePath = Db, OutputReportsPath = Reports }, context, 1, null, cts.Token));
        Assert.False(File.Exists(Path.Combine(Reports, SharedPassRegistry.FileName)));
    }

    [Fact]
    public async Task Enabled_without_a_domain_map_it_warns_and_does_nothing()
    {
        var llm = new SharedPassFakeLlm((_, _, _) => ("{}", 0m));
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(TestConfig.FromPairs(FakeLlmConfig.Pairs(llm.Name, SharedPassOptions.DefaultReaderRole, SharedPassOptions.DefaultJudgeRole)));
        services.AddLogging();
        services.AddSingleton<ILlmService>(llm);
        services.AddAITaskAgent();
        await using var provider = services.BuildServiceProvider();
        provider.UseAITaskAgent();
        var roles = LlmRoleFactory.BuildRoles(provider, _ => null, _ => { }, [SharedPassOptions.DefaultReaderRole, SharedPassOptions.DefaultJudgeRole]);
        var reporter = new CapturingReporter();
        var context = AnalysisRunner.CreateContext(new AnalysisPipelineInput { RepositoryPath = Repo }, provider, null, reporter);
        var step = AnalysisPipelineFactory.CreateSharedPassStep(AnalysisPipelineFactory.CreateDefaultTemplateProvider(), roles, new SharedPassOptions { Enabled = true });

        await step.ExecuteAsync(new ReportFacetInput(), context, 1, null, CancellationToken.None);

        Assert.Empty(llm.Calls);
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Message.Contains("no hay mapa de dominios", StringComparison.Ordinal));
        Assert.Null(context.Get<ConsolidatedRegistry>(AnalysisContextKeys.SharedPassRegistry));
    }
}
