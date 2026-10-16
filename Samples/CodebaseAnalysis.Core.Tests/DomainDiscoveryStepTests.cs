using System.Text.Json;
using AITaskAgent.Configuration;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
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
/// <see cref="DomainDiscoveryStep"/> (T12, criterio A2) de extremo a extremo con un grafo sintético de cbm, el inventario en el
/// contexto y un LLM falso que nombra: escribe <c>_domain_map.json</c>, deja el mapa en el contexto y su versión compacta en
/// <see cref="ReportFacetInput.DomainMap"/>; con <c>Mode = Off</c> no hace nada; sin inventario avisa; y un LLM que falla, que
/// responde basura o que no responde a tiempo deja los nombres derivados sin perder ningún subsistema.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class DomainDiscoveryStepTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Repo => Path.Combine(_temp.Path, "repo");

    private string ReportsPath => Path.Combine(Repo, "reports");

    /// <summary>Dos capacidades de seis tipos cada una, con sus llamadas, y un test que no debe entrar.</summary>
    private async Task<(CbmTestGraph Graph, RepositoryInventory Inventory)> NewGraphAndInventoryAsync()
    {
        var graph = await CbmTestGraph.CreateAsync(Path.Combine(_temp.Path, "codebase.db"), "demo");
        var files = new List<FileLines>();
        foreach (var capability in new[] { "Parcel", "Ledger" })
        {
            var methods = new List<long>();
            foreach (var kind in new[] { "Service", "Policy", "Store", "Entry", "Record", "Action" })
            {
                var name = capability + kind;
                var file = $"src/main/java/app/{capability.ToLowerInvariant()}/{name}.java";
                var type = await graph.NodeAsync("Class", name, $"demo.{name}", file, 1, 30);
                var method = await graph.NodeAsync("Method", "run", $"demo.{name}.run", file, 3, 9);
                await graph.EdgeAsync(type, method, "DEFINES_METHOD");
                methods.Add(method);
                files.Add(new FileLines { Path = file, Language = "java", Lines = 30 });
            }
            for (var i = 0; i < methods.Count; i++)
            {
                for (var j = i + 1; j < methods.Count; j++)
                    await graph.EdgeAsync(methods[i], methods[j], "CALLS");
            }
        }
        await graph.NodeAsync("Class", "ParcelServiceTest", "demo.ParcelServiceTest", "src/test/java/app/ParcelServiceTest.java", 1, 9);

        var inventory = new RepositoryInventory
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            GraphFingerprint = "demo|2026-09-28T10:00:00Z",
            ProductionLoc = new ProductionLines { Total = files.Sum(f => (long)f.Lines), Files = files },
            EntryPoints = [new InventoryEntryPoint { Kind = EntryPointKinds.StrutsAction, Name = "/parcel.do", File = "src/main/java/app/parcel/ParcelAction.java", Line = 4 }],
            EntryPointsTotal = 1
        };
        return (graph, inventory);
    }

    private sealed class Host : IAsyncDisposable
    {
        private Host(ServiceProvider services, NamerLlmService llm)
        {
            Services = services;
            Llm = llm;
            Roles = LlmRoleFactory.BuildRoles(services, _ => null, _ => { });
        }

        public ServiceProvider Services { get; }
        public NamerLlmService Llm { get; }
        public Dictionary<string, LlmRole> Roles { get; }
        public AITaskAgent.Support.Template.ITemplateProvider Templates { get; } = AnalysisPipelineFactory.CreateDefaultTemplateProvider();

        public static Host Create(NamerLlmService llm, params (string, string?)[] pairs)
        {
            var configuration = TestConfig.FromPairs([.. FakeLlmConfig.Pairs("namer-fake"), .. pairs]);
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging();
            services.AddSingleton<ILlmService>(llm);
            services.AddAITaskAgent();
            var provider = services.BuildServiceProvider();
            provider.UseAITaskAgent();
            return new Host(provider, llm);
        }

        public DomainDiscoveryStep Step(TimeSpan? timeout = null) =>
            new(DomainDiscoveryStep.CreateNamingStep(Templates, Roles["FunctionalAuditor"]), timeout);

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    /// <summary>LLM falso que nombra cada <c>### id</c> que recibe como «Capacidad id».</summary>
    private sealed class NamerLlmService(Func<string, string>? answer = null) : ILlmService
    {
        private readonly Lock _lock = new();
        private readonly List<string> _users = [];
        private readonly List<string> _systems = [];
        private int _calls;

        public string Name => "namer-fake";
        public TimeSpan Delay { get; init; }
        public int Calls => Volatile.Read(ref _calls);
        public IReadOnlyList<string> UserMessages { get { lock (_lock) { return [.. _users]; } } }
        public IReadOnlyList<string> SystemPrompts { get { lock (_lock) { return [.. _systems]; } } }

        public async Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            var user = request.Conversation.History.Messages.LastOrDefault(m => m.Role == LlmConstants.MessageRoles.User)?.Content ?? string.Empty;
            lock (_lock)
            {
                _users.Add(user);
                _systems.Add(request.SystemPrompt ?? request.Conversation.History.Messages.FirstOrDefault(m => m.Role == LlmConstants.MessageRoles.System)?.Content ?? string.Empty);
            }

            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);

            try
            {
                return LlmResult.Success([new LlmResponse { Content = (answer ?? Default)(user), FinishReason = FinishReason.Stop }],
                    tokensUsed: 10, promptTokens: 5, completionTokens: 5, model: "fake-model");
            }
            catch (Exception ex)
            {
                return LlmResult.Failure(ex, isRetriable: false);
            }
        }

        private static string Default(string user) => string.Concat(
            user.Replace("\r\n", "\n").Split('\n').Where(l => l.StartsWith("### ", StringComparison.Ordinal)).Select(l => l[4..].Trim())
                .Where(id => id.Length > 0 && !id.Contains('<') && !id.Contains(' '))
                .Select(id => $"### {id}\nNombre: Capacidad {id}\nDescripción: Descripción de {id}.\nPrioridad: media\n\n"));

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int EstimateTokenCount(string text) => text.Length / 4;

        public int GetMaxContextTokens(string? model = null) => 1_000_000;
    }

    private (PipelineContext Context, CapturingReporter Reporter) NewContext(Host host, string database, RepositoryInventory? inventory)
    {
        var reporter = new CapturingReporter();
        var context = AnalysisRunner.CreateContext(
            new AnalysisPipelineInput { RepositoryPath = Repo, DatabasePath = database, OutputReportsPath = ReportsPath }, host.Services, null, reporter);
        if (inventory != null)
            context.Set(AnalysisContextKeys.RepositoryInventory, inventory);
        return (context, reporter);
    }

    private static Task<ReportFacetInput> RunAsync(DomainDiscoveryStep step, PipelineContext context, ReportFacetInput input, CancellationToken ct = default) =>
        step.ExecuteAsync(input, context, 1, null, ct);

    private DomainMap ReadArtifact()
    {
        var map = PreparationArtifacts.TryRead<DomainMap>(ReportsPath, out var warning);
        Assert.Null(warning);
        return map!;
    }

    [Fact]
    public async Task It_discovers_the_subsystems_names_them_and_leaves_the_map_in_the_artifact_the_context_and_the_input()
    {
        var (graph, inventory) = await NewGraphAndInventoryAsync();
        using var _ = graph;
        await using var host = Host.Create(new NamerLlmService());
        var (context, reporter) = NewContext(host, graph.DatabasePath, inventory);
        var input = new ReportFacetInput();

        var result = await RunAsync(host.Step(), context, input);

        Assert.Same(input, result);
        var map = ReadArtifact();
        Assert.Same(map.GetType(), context.Get<DomainMap>(AnalysisContextKeys.DomainMap)!.GetType());
        Assert.Equal(inventory.GraphFingerprint, map.GraphFingerprint);
        Assert.Equal(DomainMapBases.Graph, map.Basis);
        Assert.Equal(["ledger", "parcel"], map.Subsystems.Select(s => s.Id));
        Assert.All(map.Subsystems, s => Assert.Equal(6, s.Files.Count));
        Assert.DoesNotContain(map.Subsystems.SelectMany(s => s.Files), f => f.Contains("/test/", StringComparison.Ordinal));
        Assert.Empty(map.UnassignedFiles);
        // El LLM los nombró y el nombre pasó la validación.
        Assert.Equal(["Capacidad ledger", "Capacidad parcel"], map.Subsystems.Select(s => s.Name));
        Assert.All(map.Subsystems, s => Assert.Equal(SubsystemNameOrigins.Llm, s.NameOrigin));
        Assert.Equal(360, map.Subsystems.Sum(s => s.Metrics.Loc));
        Assert.Equal("/parcel.do", map.Subsystems.Single(s => s.Id == "parcel").EntryPoints.Single().Name);

        // La versión compacta para las plantillas.
        using var summary = JsonDocument.Parse(input.DomainMap);
        Assert.Equal(2, summary.RootElement.GetProperty("subsystems_total").GetInt32());
        Assert.Equal("graph", summary.RootElement.GetProperty("basis").GetString());
        Assert.Equal(["Capacidad ledger", "Capacidad parcel"], summary.RootElement.GetProperty("subsystems").EnumerateArray().Select(s => s.GetProperty("name").GetString()!).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("ParcelService.java", input.DomainMap);

        // El prompt es el de nombrado (no el del informe funcional, que es el prompt de sistema del rol) y lleva el resumen.
        Assert.Equal(1, host.Llm.Calls);
        Assert.Contains("Analista de Dominios", host.Llm.SystemPrompts.Single());
        Assert.Contains("### parcel", host.Llm.UserMessages.Single());
        Assert.Contains("Término dominante: parcel", host.Llm.UserMessages.Single());
        Assert.DoesNotContain(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning || e.Level == AnalysisLogLevel.Error);
    }

    [Fact]
    public async Task Two_runs_over_the_same_graph_give_the_same_subsystems_files_and_ids()
    {
        var (graph, inventory) = await NewGraphAndInventoryAsync();
        using var _ = graph;
        await using var host = Host.Create(new NamerLlmService());

        var (context1, _1) = NewContext(host, graph.DatabasePath, inventory);
        await RunAsync(host.Step(), context1, new ReportFacetInput());
        var first = ReadArtifact() with { GeneratedAt = default };
        var (context2, _2) = NewContext(host, graph.DatabasePath, inventory);
        await RunAsync(host.Step(), context2, new ReportFacetInput());
        var second = ReadArtifact() with { GeneratedAt = default };

        Assert.Equal(PreparationArtifacts.Serialize(first), PreparationArtifacts.Serialize(second));
    }

    [Fact]
    public async Task A_failing_llm_is_a_warning_and_the_map_keeps_every_subsystem_with_derived_names()
    {
        var (graph, inventory) = await NewGraphAndInventoryAsync();
        using var _ = graph;
        await using var host = Host.Create(new NamerLlmService(_ => throw new InvalidOperationException("sin red")));
        var (context, reporter) = NewContext(host, graph.DatabasePath, inventory);
        var input = new ReportFacetInput();

        await RunAsync(host.Step(), context, input);

        var map = ReadArtifact();
        Assert.Equal(["ledger", "parcel"], map.Subsystems.Select(s => s.Id));
        Assert.Equal(["Ledger", "Parcel"], map.Subsystems.Select(s => s.Name));
        Assert.All(map.Subsystems, s => Assert.Equal(SubsystemNameOrigins.Derived, s.NameOrigin));
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Message.Contains("sin red", StringComparison.Ordinal));
        Assert.NotEqual("{}", input.DomainMap);
    }

    [Fact]
    public async Task A_garbage_answer_and_an_answer_with_fewer_subsystems_lose_none()
    {
        var (graph, inventory) = await NewGraphAndInventoryAsync();
        using var _ = graph;

        await using (var garbage = Host.Create(new NamerLlmService(_ => "no sé qué decirte")))
        {
            var (context, reporter) = NewContext(garbage, graph.DatabasePath, inventory);
            await RunAsync(garbage.Step(), context, new ReportFacetInput());
            var map = ReadArtifact();
            Assert.Equal(2, map.Subsystems.Count);
            Assert.All(map.Subsystems, s => Assert.Equal(SubsystemNameOrigins.Derived, s.NameOrigin));
            Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning);
        }

        await using (var partial = Host.Create(new NamerLlmService(_ => "### parcel\nNombre: Envíos\nDescripción: Envíos.\nPrioridad: alta\n")))
        {
            var (context, _) = NewContext(partial, graph.DatabasePath, inventory);
            await RunAsync(partial.Step(), context, new ReportFacetInput());
            var map = ReadArtifact();
            Assert.Equal(["Ledger", "Envíos"], map.Subsystems.Select(s => s.Name));
        }
    }

    [Fact]
    public async Task A_naming_that_exceeds_its_limit_is_a_warning_naming_the_limit_and_keeps_the_derived_names()
    {
        var (graph, inventory) = await NewGraphAndInventoryAsync();
        using var _ = graph;
        await using var host = Host.Create(new NamerLlmService { Delay = TimeSpan.FromSeconds(30) });
        var (context, reporter) = NewContext(host, graph.DatabasePath, inventory);

        await RunAsync(host.Step(TimeSpan.FromMilliseconds(300)), context, new ReportFacetInput());

        var map = ReadArtifact();
        Assert.Equal(2, map.Subsystems.Count);
        Assert.All(map.Subsystems, s => Assert.Equal(SubsystemNameOrigins.Derived, s.NameOrigin));
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Message.Contains("límite", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_naming_step_the_names_are_derived_and_no_llm_is_called()
    {
        var (graph, inventory) = await NewGraphAndInventoryAsync();
        using var _ = graph;
        await using var host = Host.Create(new NamerLlmService());
        var (context, _) = NewContext(host, graph.DatabasePath, inventory);

        await RunAsync(new DomainDiscoveryStep(), context, new ReportFacetInput());

        Assert.Equal(0, host.Llm.Calls);
        Assert.Equal(["Ledger", "Parcel"], ReadArtifact().Subsystems.Select(s => s.Name));
    }

    [Fact]
    public async Task With_reuse_a_saved_map_of_the_same_graph_is_loaded_without_asking_the_llm_and_the_file_is_not_touched()
    {
        var (graph, inventory) = await NewGraphAndInventoryAsync();
        using var _ = graph;
        await using var host = Host.Create(new NamerLlmService());
        var (first, _1) = NewContext(host, graph.DatabasePath, inventory);
        await RunAsync(host.Step(), first, new ReportFacetInput());
        var path = Path.Combine(ReportsPath, DomainMap.FileName);
        var saved = await File.ReadAllTextAsync(path);
        Assert.Equal(1, host.Llm.Calls);

        var (context, reporter) = NewContext(host, graph.DatabasePath, inventory);
        var input = new ReportFacetInput();
        var result = await RunAsync(new DomainDiscoveryStep(DomainDiscoveryStep.CreateNamingStep(host.Templates, host.Roles["FunctionalAuditor"]), reuseSavedMap: true), context, input);

        Assert.Same(input, result);
        Assert.Equal(1, host.Llm.Calls);
        Assert.Equal(saved, await File.ReadAllTextAsync(path));
        var map = context.Get<DomainMap>(AnalysisContextKeys.DomainMap)!;
        Assert.Equal(["Capacidad ledger", "Capacidad parcel"], map.Subsystems.Select(s => s.Name));
        using var summary = JsonDocument.Parse(input.DomainMap);
        Assert.Equal(2, summary.RootElement.GetProperty("subsystems_total").GetInt32());
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Info && e.Message.Contains("reutiliza", StringComparison.Ordinal));
        Assert.DoesNotContain(reporter.Entries, e => e.Level is AnalysisLogLevel.Warning or AnalysisLogLevel.Error);
    }

    [Fact]
    public async Task The_sql_tables_of_the_inventory_reach_a_new_map_and_a_reused_one_without_touching_the_saved_file()
    {
        var (graph, inventory) = await NewGraphAndInventoryAsync();
        using var _ = graph;
        await using var host = Host.Create(new NamerLlmService());
        IReadOnlyList<SqlTableReference> tables =
        [
            new("shipment", null, "src/main/java/app/parcel/ParcelStore.java", 3, "SELECT"),
            new("ledger_entry", "app", "src/main/java/app/ledger/LedgerStore.java", 4, "INSERT")
        ];

        // Un mapa nuevo: las tablas salen del escaneo del inventario.
        var (first, _1) = NewContext(host, graph.DatabasePath, inventory);
        first.Set(AnalysisContextKeys.InventorySqlTables, tables);
        await RunAsync(host.Step(), first, new ReportFacetInput());
        var fresh = first.Get<DomainMap>(AnalysisContextKeys.DomainMap)!;
        Assert.Equal(["shipment"], fresh.Subsystems.Single(s => s.Id == "parcel").Tables);
        Assert.Equal(["ledger_entry"], fresh.Subsystems.Single(s => s.Id == "ledger").Tables);
        Assert.Equal(1, fresh.Subsystems.Single(s => s.Id == "ledger").Metrics.Tables);

        // Un mapa guardado antes de existir el escaneo (sin tablas) y reutilizado: las recibe sin volver a nombrarlo.
        var path = Path.Combine(ReportsPath, DomainMap.FileName);
        var withoutTables = fresh with { Subsystems = [.. fresh.Subsystems.Select(s => s with { Tables = [], Metrics = s.Metrics with { Tables = 0 } })] };
        await File.WriteAllTextAsync(path, PreparationArtifacts.Serialize(withoutTables));
        var saved = await File.ReadAllTextAsync(path);
        var calls = host.Llm.Calls;

        var (second, _2) = NewContext(host, graph.DatabasePath, inventory);
        second.Set(AnalysisContextKeys.InventorySqlTables, tables);
        var input = new ReportFacetInput();
        await RunAsync(new DomainDiscoveryStep(DomainDiscoveryStep.CreateNamingStep(host.Templates, host.Roles["FunctionalAuditor"]), reuseSavedMap: true), second, input);

        Assert.Equal(calls, host.Llm.Calls);
        Assert.Equal(saved, await File.ReadAllTextAsync(path));
        var reused = second.Get<DomainMap>(AnalysisContextKeys.DomainMap)!;
        Assert.Equal(["shipment"], reused.Subsystems.Single(s => s.Id == "parcel").Tables);
        Assert.Equal(1, reused.Subsystems.Single(s => s.Id == "parcel").Metrics.Tables);
        Assert.Contains("shipment", input.DomainMap);
    }

    [Fact]
    public async Task With_reuse_a_missing_a_corrupt_or_other_graph_map_is_recalculated()
    {
        var (graph, inventory) = await NewGraphAndInventoryAsync();
        using var _ = graph;
        await using var host = Host.Create(new NamerLlmService());
        DomainDiscoveryStep Reusing() => new(DomainDiscoveryStep.CreateNamingStep(host.Templates, host.Roles["FunctionalAuditor"]), reuseSavedMap: true);

        // Sin fichero: se descubre.
        var (none, _1) = NewContext(host, graph.DatabasePath, inventory);
        await RunAsync(Reusing(), none, new ReportFacetInput());
        Assert.Equal(1, host.Llm.Calls);
        Assert.Equal(2, ReadArtifact().Subsystems.Count);

        // Con la huella de otra indexación: se descubre de nuevo y el fichero queda con la huella actual.
        var other = inventory with { GraphFingerprint = "demo|2026-10-05T08:00:00Z" };
        var (changed, reporter) = NewContext(host, graph.DatabasePath, other);
        await RunAsync(Reusing(), changed, new ReportFacetInput());
        Assert.Equal(2, host.Llm.Calls);
        Assert.Equal("demo|2026-10-05T08:00:00Z", ReadArtifact().GraphFingerprint);
        Assert.Contains(reporter.Entries, e => e.Message.Contains("no es del grafo actual", StringComparison.Ordinal));

        // Ilegible: aviso y se descubre.
        await File.WriteAllTextAsync(Path.Combine(ReportsPath, DomainMap.FileName), "{ no es json");
        var (corrupt, reporter2) = NewContext(host, graph.DatabasePath, other);
        await RunAsync(Reusing(), corrupt, new ReportFacetInput());
        Assert.Equal(3, host.Llm.Calls);
        Assert.Contains(reporter2.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Message.Contains("No se pudo leer", StringComparison.Ordinal));
        Assert.Equal(2, ReadArtifact().Subsystems.Count);

        // Sin huella en el grafo actual no hay con qué comparar: no se reutiliza.
        var (noFingerprint, _3) = NewContext(host, graph.DatabasePath, inventory with { GraphFingerprint = null });
        await RunAsync(Reusing(), noFingerprint, new ReportFacetInput());
        Assert.Equal(4, host.Llm.Calls);
    }

    [Fact]
    public async Task Without_the_reuse_flag_a_matching_saved_map_is_ignored()
    {
        var (graph, inventory) = await NewGraphAndInventoryAsync();
        using var _ = graph;
        await using var host = Host.Create(new NamerLlmService());
        var (first, _1) = NewContext(host, graph.DatabasePath, inventory);
        await RunAsync(host.Step(), first, new ReportFacetInput());

        var (second, _2) = NewContext(host, graph.DatabasePath, inventory);
        await RunAsync(host.Step(), second, new ReportFacetInput());

        Assert.Equal(2, host.Llm.Calls);
    }

    [Fact]
    public async Task With_mode_off_it_does_nothing()
    {
        var (graph, inventory) = await NewGraphAndInventoryAsync();
        using var _ = graph;
        await using var host = Host.Create(new NamerLlmService(), ("CodebaseAnalysis:Preparation:Mode", "Off"));
        var (context, reporter) = NewContext(host, graph.DatabasePath, inventory);
        var input = new ReportFacetInput();

        var result = await RunAsync(host.Step(), context, input);

        Assert.Same(input, result);
        Assert.Equal("{}", input.DomainMap);
        Assert.Null(context.Get<DomainMap>(AnalysisContextKeys.DomainMap));
        Assert.False(File.Exists(Path.Combine(ReportsPath, DomainMap.FileName)));
        Assert.Equal(0, host.Llm.Calls);
        Assert.Empty(reporter.Entries);
    }

    [Fact]
    public async Task A_missing_inventory_is_a_warning_and_the_input_comes_out_untouched()
    {
        var (graph, _) = await NewGraphAndInventoryAsync();
        using var _g = graph;
        await using var host = Host.Create(new NamerLlmService());
        var (context, reporter) = NewContext(host, graph.DatabasePath, inventory: null);
        var input = new ReportFacetInput();

        var result = await RunAsync(host.Step(), context, input);

        Assert.Same(input, result);
        Assert.Equal("{}", input.DomainMap);
        Assert.Null(context.Get<DomainMap>(AnalysisContextKeys.DomainMap));
        Assert.False(File.Exists(Path.Combine(ReportsPath, DomainMap.FileName)));
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Message.Contains("inventario", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_graph_without_types_and_an_inventory_with_files_give_the_structure_and_an_empty_graph_gives_an_empty_map()
    {
        await using var host = Host.Create(new NamerLlmService());
        using var empty = await CbmTestGraph.CreateAsync(Path.Combine(_temp.Path, "empty.db"), "demo");
        var files = Enumerable.Range(0, 6).Select(i => new FileLines { Path = $"src/a/F{i}.java", Language = "java", Lines = 5 }).ToList();
        var withFiles = new RepositoryInventory { ProductionLoc = new ProductionLines { Files = files, Total = 30 } };

        var (context, reporter) = NewContext(host, empty.DatabasePath, withFiles);
        await RunAsync(host.Step(), context, new ReportFacetInput());
        var structural = ReadArtifact();
        Assert.Equal(DomainMapBases.StructureOnly, structural.Basis);
        Assert.Single(structural.Subsystems);
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning);
        Assert.Equal(1, host.Llm.Calls);

        var (context2, _) = NewContext(host, Path.Combine(_temp.Path, "missing.db"), new RepositoryInventory());
        var input = new ReportFacetInput();
        await RunAsync(host.Step(), context2, input);
        var nothing = ReadArtifact();
        Assert.Empty(nothing.Subsystems);
        Assert.Empty(nothing.UnassignedFiles);
        Assert.Contains("\"subsystems_total\":0", input.DomainMap);
        Assert.Equal(1, host.Llm.Calls); // un mapa vacío no pregunta nada al LLM
    }

    [Fact]
    public async Task The_entry_point_metrics_use_every_entry_of_the_inventory_and_not_the_capped_list_of_the_artifact()
    {
        var (graph, inventory) = await NewGraphAndInventoryAsync();
        using var _ = graph;
        await using var host = Host.Create(new NamerLlmService());
        var (context, _r) = NewContext(host, graph.DatabasePath, inventory with { EntryPoints = [], EntryPointsTotal = 4 });
        InventoryEntryPoint Entry(string name, string kind, string file) => new() { Kind = kind, Name = name, File = file, Line = 1 };
        context.Set<IReadOnlyList<InventoryEntryPoint>>(AnalysisContextKeys.InventoryEntryPoints,
        [
            Entry("/a.do", EntryPointKinds.StrutsAction, "src/main/java/app/ledger/LedgerAction.java"),
            Entry("/b.do", EntryPointKinds.StrutsAction, "src/main/java/app/ledger/LedgerEntry.java"),
            Entry("job", EntryPointKinds.Scheduled, "src/main/java/app/ledger/LedgerPolicy.java"),
            Entry("/c.do", EntryPointKinds.StrutsAction, "src/main/java/app/parcel/ParcelAction.java")
        ]);

        await RunAsync(host.Step(), context, new ReportFacetInput());

        var map = ReadArtifact();
        var ledger = map.Subsystems.Single(s => s.Id == "ledger").Metrics;
        Assert.Equal((3, 2), (ledger.EntryPoints, ledger.EntryPointKinds));
        Assert.Equal(1, map.Subsystems.Single(s => s.Id == "parcel").Metrics.EntryPoints);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var (graph, inventory) = await NewGraphAndInventoryAsync();
        using var _ = graph;
        await using var host = Host.Create(new NamerLlmService());
        var (context, _r) = NewContext(host, graph.DatabasePath, inventory);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(host.Step(), context, new ReportFacetInput(), cts.Token));
    }

    [Fact]
    public void The_naming_templates_exist_and_render_without_leftovers()
    {
        var provider = AnalysisPipelineFactory.CreateDefaultTemplateProvider();

        var system = provider.Render("domain_naming_system", new Dictionary<string, object?>())!;
        var prompt = provider.Render("domain_naming_prompt", new Dictionary<string, object?> { ["input"] = "### alpha\nTamaño: 1 ficheros" })!;

        Assert.DoesNotContain("{{", system);
        Assert.DoesNotContain("{{", prompt);
        Assert.Contains("### alpha", prompt);
        Assert.Contains("Nombre:", prompt);
    }
}
