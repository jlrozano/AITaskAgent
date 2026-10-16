using AITaskAgent.Support.Template;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Preparation;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Experimento E2 de la medición de la preparación: la infraestructura compartida (tipos de los que dependen muchas comunidades distintas y
/// que no tienen entradas ni estados propios) forma un único subsistema <c>shared</c> en lugar de repartirse entre las capacidades que la usan.
/// Repositorios sintéticos con nombres genéricos de prueba.
/// </summary>
public sealed class SharedInfrastructureTests
{
    private static readonly DateTimeOffset Fixed = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    /// <summary>El subsistema compartido está encendido por defecto (<see cref="PreparationOptions.DefaultSharedMinConsumers"/>); esta instancia fija el valor para no depender del defecto.</summary>
    private static readonly PreparationOptions On = new() { SharedMinConsumers = 3 };

    private static readonly string[] Capabilities = ["Alpha", "Beta", "Gamma", "Delta"];
    private static readonly string[] Parts = ["Service", "Policy", "Store", "Entry", "Record", "Query", "Audit", "Quota"];

    private const string Money = "src/common/Money.java";
    private const string DbConfig = "src/common/DbConfig.java";
    private const string AuditLog = "src/common/AuditLog.java";
    private const string Status = "src/common/Status.java";
    private const string Customer = "src/domain/customer/Customer.java";

    private static string FileOf(string capability, string part) => $"src/domain/{capability.ToLowerInvariant()}/{capability}{part}.java";

    /// <summary>
    /// Cuatro capacidades densas (<c>Alpha*</c>...) y cinco tipos que todas usan: <c>Money</c> y <c>DbConfig</c> (utilidades puras),
    /// <c>AuditLog</c> (una entrada del inventario), <c>Status</c> (un enumerado) y <c>Customer</c> (una entidad que además depende de
    /// todas las capacidades: no es base de nadie).
    /// </summary>
    private static DomainMapInput Repo(out IReadOnlyList<string> allFiles, bool entryOnAudit = true)
    {
        var builder = new TypeGraph.Builder();
        var files = new List<string>();
        void Add(string name, string file, string label = "Class")
        {
            builder.AddNode(name, name, label, file);
            files.Add(file);
        }

        foreach (var capability in Capabilities)
        {
            foreach (var part in Parts)
                Add(capability + part, FileOf(capability, part));
            for (var i = 0; i < Parts.Length; i++)
            {
                for (var j = i + 1; j < Parts.Length; j++)
                    builder.AddEdge(capability + Parts[i], capability + Parts[j], 2);
            }
        }

        Add("Money", Money);
        Add("DbConfig", DbConfig);
        Add("AuditLog", AuditLog);
        Add("Status", Status, "Enum");
        Add("Customer", Customer);
        foreach (var capability in Capabilities)
        {
            foreach (var shared in new[] { "Money", "DbConfig", "AuditLog", "Status", "Customer" })
                builder.AddEdge(capability + "Service", shared, 1);
            // «Customer» depende a su vez de una clase de cada capacidad: mucho hacia fuera, así que no es infraestructura.
            builder.AddEdge("Customer", capability + "Record", 1);
        }

        allFiles = files;
        return new DomainMapInput
        {
            ProductionFiles = [.. files.Select(f => new FileLines { Path = f, Language = "java", Lines = 10 })],
            Graph = builder.Build(),
            EntryPoints = entryOnAudit ? [new InventoryEntryPoint { Kind = "main", Name = "AuditLog.main", File = AuditLog, Line = 1 }] : [],
            GraphFingerprint = "fp",
            GeneratedAt = Fixed
        };
    }

    private static Subsystem Shared(DomainMap map) => map.Subsystems.Single(s => s.IsShared);

    [Fact]
    public void Types_that_many_communities_depend_on_and_that_have_no_entries_or_states_form_one_shared_subsystem()
    {
        var input = Repo(out var all);

        var map = DomainMapBuilder.Build(input, On);

        var shared = Shared(map);
        Assert.Equal("shared", shared.Id);
        Assert.Equal(SubsystemKinds.Shared, shared.Kind);
        Assert.Equal([DbConfig, Money], shared.Files);
        Assert.Equal(2, shared.Metrics.Files);
        // Las cuatro capacidades siguen enteras y cada fichero está en un único subsistema.
        foreach (var capability in Capabilities)
            Assert.Single(Parts.Select(p => map.Subsystems.Single(s => s.Files.Contains(FileOf(capability, p))).Id).Distinct());
        Assert.Equal(all.Order(StringComparer.Ordinal), map.Subsystems.SelectMany(s => s.Files).Concat(map.UnassignedFiles).Order(StringComparer.Ordinal));
        Assert.Single(map.Subsystems, s => s.IsShared);
    }

    [Fact]
    public void A_widely_used_type_with_an_entry_a_state_or_a_wide_fan_out_stays_with_its_capability()
    {
        var map = DomainMapBuilder.Build(Repo(out _), On);

        var shared = Shared(map);
        Assert.DoesNotContain(AuditLog, shared.Files);   // tiene una entrada
        Assert.DoesNotContain(Status, shared.Files);     // es un estado
        Assert.DoesNotContain(Customer, shared.Files);   // depende de todas las capacidades: no es base de nadie
        Assert.All(new[] { AuditLog, Status, Customer }, f => Assert.Contains(map.Subsystems, s => !s.IsShared && s.Files.Contains(f)));
    }

    [Fact]
    public void Without_the_entry_the_same_type_is_shared()
    {
        var map = DomainMapBuilder.Build(Repo(out _, entryOnAudit: false), On);

        Assert.Equal([AuditLog, DbConfig, Money], Shared(map).Files);
    }

    [Fact]
    public void The_threshold_of_distinct_communities_is_configurable_and_zero_turns_the_subsystem_off()
    {
        var input = Repo(out _);

        var strict = DomainMapBuilder.Build(input, On with { SharedMinConsumers = 5 });
        var off = DomainMapBuilder.Build(input, On with { SharedMinConsumers = 0 });

        // Solo hay cuatro comunidades: con un umbral de cinco nadie lo cumple.
        Assert.DoesNotContain(strict.Subsystems, s => s.IsShared);
        Assert.DoesNotContain(off.Subsystems, s => s.IsShared);
        // Y Money y DbConfig vuelven a una capacidad, como antes del experimento.
        Assert.Contains(off.Subsystems, s => s.Files.Contains(Money));
    }

    [Fact]
    public void The_shared_subsystem_is_on_by_default_and_zero_gives_the_map_without_it()
    {
        var input = Repo(out _);

        var byDefault = DomainMapBuilder.Build(input, new PreparationOptions());
        var off = DomainMapBuilder.Build(input, new PreparationOptions { SharedMinConsumers = 0 });

        Assert.Equal(3, PreparationOptions.DefaultSharedMinConsumers);
        Assert.Equal(PreparationArtifacts.Serialize(DomainMapBuilder.Build(input, On)), PreparationArtifacts.Serialize(byDefault));
        Assert.DoesNotContain(off.Subsystems, s => s.IsShared);
        Assert.All(off.Subsystems, s => Assert.Equal(SubsystemKinds.Capability, s.Kind));
    }

    [Fact]
    public void The_options_are_read_from_the_configuration_and_bad_values_fall_back_with_a_warning()
    {
        var good = PreparationOptions.FromConfiguration(TestConfig.FromPairs(
            ("CodebaseAnalysis:Preparation:SharedMinConsumers", "4"), ("CodebaseAnalysis:Preparation:SharedMinConsumerShare", "0.35")));
        var bad = PreparationOptions.FromConfiguration(TestConfig.FromPairs(
            ("CodebaseAnalysis:Preparation:SharedMinConsumers", "varias"), ("CodebaseAnalysis:Preparation:SharedMinConsumerShare", "2")));

        Assert.Equal((4, 0.35), (good.SharedMinConsumers, good.SharedMinConsumerShare));
        Assert.Empty(good.Warnings);
        Assert.Equal((PreparationOptions.DefaultSharedMinConsumers, PreparationOptions.DefaultSharedMinConsumerShare), (bad.SharedMinConsumers, bad.SharedMinConsumerShare));
        Assert.Equal(2, bad.Warnings.Count);
        Assert.Contains(bad.Warnings, w => w.Contains("SharedMinConsumers", StringComparison.Ordinal));
        Assert.Contains(bad.Warnings, w => w.Contains("SharedMinConsumerShare", StringComparison.Ordinal));
    }

    [Fact]
    public void The_share_of_communities_raises_the_threshold_in_a_large_repository()
    {
        var input = Repo(out _);

        // Con cuatro comunidades y una fracción de 1,0 hacen falta las cuatro, y las usan las cuatro: sigue siendo compartida.
        var all = DomainMapBuilder.Build(input, On with { SharedMinConsumerShare = 1.0 });
        Assert.Contains(all.Subsystems, s => s.IsShared);

        // Pero con tres capacidades que la usan de cuatro y una fracción de 1,0 no llega.
        var partial = input with
        {
            Graph = WithoutUse(input.Graph, "DeltaService", ["Money", "DbConfig"])
        };
        var map = DomainMapBuilder.Build(partial, On with { SharedMinConsumerShare = 1.0 });
        Assert.DoesNotContain(map.Subsystems, s => s.IsShared);
    }

    private static TypeGraph WithoutUse(TypeGraph graph, string from, string[] targets)
    {
        var builder = new TypeGraph.Builder();
        foreach (var n in graph.Nodes)
            builder.AddNode(n.Key, n.Name, n.Label, n.File, n.IsVirtual);
        foreach (var e in graph.Edges)
        {
            if (graph.Nodes[e.Source].Key == from && targets.Contains(graph.Nodes[e.Target].Key))
                continue;
            builder.AddEdge(graph.Nodes[e.Source].Key, graph.Nodes[e.Target].Key, e.Weight);
        }
        return builder.Build();
    }

    [Fact]
    public void Files_without_types_never_join_the_shared_subsystem()
    {
        var input = Repo(out _);
        input = input with { ProductionFiles = [.. input.ProductionFiles, new FileLines { Path = "src/common/format.js", Language = "js", Lines = 3 }] };

        var map = DomainMapBuilder.Build(input, On);

        // Comparte carpeta con los ficheros compartidos, pero un fichero sin tipos no se asigna a ese subsistema.
        Assert.DoesNotContain("src/common/format.js", Shared(map).Files);
        Assert.Contains("src/common/format.js", map.Subsystems.Where(s => !s.IsShared).SelectMany(s => s.Files).Concat(map.UnassignedFiles));
    }

    [Fact]
    public void A_single_candidate_is_not_a_subsystem()
    {
        var input = Repo(out _);
        var graph = input.Graph;
        foreach (var capability in Capabilities)
            graph = WithoutUse(graph, capability + "Service", ["DbConfig"]);

        var map = DomainMapBuilder.Build(input with { Graph = graph }, On);

        // Solo «Money» cumple, y un fichero solo no forma un grupo: se queda donde estaba.
        Assert.DoesNotContain(map.Subsystems, s => s.IsShared);
        Assert.Contains(map.Subsystems, s => s.Files.Contains(Money));
    }

    [Fact]
    public void The_shared_subsystem_is_deterministic_and_does_not_depend_on_the_input_order()
    {
        var input = Repo(out _);
        var reversed = input with { ProductionFiles = [.. input.ProductionFiles.Reverse()] };

        var first = PreparationArtifacts.Serialize(DomainMapBuilder.Build(input, On));
        var second = PreparationArtifacts.Serialize(DomainMapBuilder.Build(input, On));
        var permuted = PreparationArtifacts.Serialize(DomainMapBuilder.Build(reversed, On));

        Assert.Equal(first, second);
        Assert.Equal(first, permuted);
    }

    [Fact]
    public void The_kind_is_written_in_the_map_and_a_map_saved_without_it_reads_as_capabilities()
    {
        var map = DomainMapBuilder.Build(Repo(out _), On);
        var json = PreparationArtifacts.Serialize(map);

        Assert.Contains("\"kind\": \"shared\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"capability\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("is_shared", json, StringComparison.Ordinal);

        var directory = Path.Combine(Path.GetTempPath(), "a10-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, DomainMap.FileName), json.Replace("\"kind\": \"capability\",", "", StringComparison.Ordinal).Replace("\"kind\": \"shared\",", "", StringComparison.Ordinal));
            var read = PreparationArtifacts.TryRead<DomainMap>(directory, out var warning);
            Assert.NotNull(read);
            Assert.Null(warning);
            Assert.All(read!.Subsystems, s => Assert.False(s.IsShared));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task The_shared_subsystem_is_not_named_by_the_llm_and_keeps_its_name()
    {
        var map = DomainMapBuilder.Build(Repo(out _), On);
        var prompts = new List<string>();

        var named = await DomainNaming.NameAsync(map, (text, _) =>
        {
            prompts.Add(text);
            return Task.FromResult<string?>("### shared\nNombre: Capa de utilidades\n\n### alpha\nNombre: Alfa");
        }, [], CancellationToken.None);

        Assert.DoesNotContain(prompts, p => p.Contains("### shared", StringComparison.Ordinal));
        var shared = Shared(named);
        Assert.Equal(SubsystemNameOrigins.Derived, shared.NameOrigin);
        Assert.Equal(Shared(map).Name, shared.Name);
    }

    // ───────────────────────────── Plan de cobertura ─────────────────────────────

    private static Subsystem Sub(string id, string kind, double centrality, int entries = 0) =>
        new()
        {
            Id = id,
            Name = id,
            Kind = kind,
            Metrics = new SubsystemMetrics { Files = 10, Loc = 1000, Centrality = centrality, EntryPoints = entries, EntryPointKinds = entries > 0 ? 1 : 0 }
        };

    private sealed class NoTemplates : ITemplateProvider
    {
        public string? GetTemplate(string name) => null;
        public string? Render(string name, object model) => throw new NotSupportedException();
    }

    [Fact]
    public void The_shared_subsystem_is_left_out_of_the_functional_and_flow_reports_but_not_of_architecture_or_errors()
    {
        var map = new DomainMap
        {
            GeneratedAt = Fixed,
            Subsystems = [Sub("alpha", SubsystemKinds.Capability, 0.1, entries: 5), Sub("beta", SubsystemKinds.Capability, 0.1, entries: 4), Sub("shared", SubsystemKinds.Shared, 0.9)]
        };

        var plan = CoveragePlanner.Plan(map, new PreparationOptions { MaxSubsystemsPerReport = 8 }, new NoTemplates(), Fixed);

        Assert.DoesNotContain(plan.Reports.Single(r => r.Report == ReportType.AnalisisFuncional).Chosen, c => c.SubsystemId == "shared");
        Assert.DoesNotContain(plan.Reports.Single(r => r.Report == ReportType.FlujosEndToEnd).Chosen, c => c.SubsystemId == "shared");
        Assert.Contains(plan.Reports.Single(r => r.Report == ReportType.ErroresYBugs).Chosen, c => c.SubsystemId == "shared");
        Assert.Contains(plan.Reports.Single(r => r.Report == ReportType.ArquitecturaAcoplamiento).Chosen, c => c.SubsystemId == "shared");

        var functional = plan.Reports.Single(r => r.Report == ReportType.AnalisisFuncional);
        var excluded = Assert.Single(functional.Excluded);
        Assert.Equal(("shared", ExclusionReasons.SharedInfrastructure), (excluded.SubsystemId, excluded.Reason));
        // Y no puntúa en el ranking de esos informes, ni ocupa un puesto del K.
        Assert.DoesNotContain(functional.Scores, s => s.SubsystemId == "shared");
        Assert.Equal([1, 2], functional.Chosen.Select(c => c.Rank));
    }
}
