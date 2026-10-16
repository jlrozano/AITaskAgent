using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Support;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="DomainMapBuilder"/>, <see cref="StructuralClusterer"/> y <see cref="AffinityGraph"/> (T10, criterio A2): cada
/// fichero de producción queda en un único subsistema o en <c>unassigned</c>, los identificadores son estables, una capacidad
/// repartida en dos ramas distantes del árbol sale como un solo subsistema, un grafo pobre cae a la estructura y los subsistemas
/// diminutos se absorben. Repositorios sintéticos con nombres genéricos de prueba; ninguno pertenece a un repositorio real.
/// </summary>
public sealed class DomainMapBuilderTests
{
    private static readonly DateTimeOffset Fixed = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Un repositorio sintético: tipos con su fichero, dependencias entre ellos y ficheros sin tipos.</summary>
    private sealed class Repo
    {
        public List<(string Name, string File, string Label)> Types { get; } = [];
        public List<(string From, string To, double Weight)> Edges { get; } = [];
        public List<string> LooseFiles { get; } = [];
        public List<(string Table, string Type)> TableUses { get; } = [];
        public List<InventoryEntryPoint> EntryPoints { get; } = [];

        public string Add(string name, string file, string label = "Class")
        {
            Types.Add((name, file, label));
            return name;
        }

        public void Link(string from, string to, double weight = 2) => Edges.Add((from, to, weight));

        /// <summary>Todos con todos (en una dirección), como las clases de una misma capacidad que se llaman entre sí.</summary>
        public void Dense(IReadOnlyList<string> names, double weight = 2)
        {
            for (var i = 0; i < names.Count; i++)
            {
                for (var j = i + 1; j < names.Count; j++)
                    Link(names[i], names[j], weight);
            }
        }

        public IReadOnlyList<string> Files => [.. Types.Select(t => t.File).Concat(LooseFiles).Distinct(StringComparer.Ordinal)];

        public TypeGraph Graph(bool reversed = false)
        {
            var builder = new TypeGraph.Builder();
            foreach (var (name, file, label) in reversed ? Enumerable.Reverse(Types) : Types)
                builder.AddNode(name, name, label, file);
            foreach (var (table, _) in TableUses.DistinctBy(t => t.Table))
                builder.AddNode("table:" + table, table, "", "", isVirtual: true);
            foreach (var (from, to, weight) in reversed ? Enumerable.Reverse(Edges) : Edges)
                builder.AddEdge(from, to, weight);
            foreach (var (table, type) in TableUses)
            {
                builder.AddEdge(type, "table:" + table, 1);
                builder.AddEdge("table:" + table, type, 1);
            }
            return builder.Build();
        }

        public DomainMapInput Input(bool reversed = false, TypeGraph? graph = null) => new()
        {
            ProductionFiles = [.. (reversed ? Enumerable.Reverse(Files) : Files).Select(f => new FileLines { Path = f, Language = "java", Lines = 10 })],
            Graph = graph ?? Graph(reversed),
            EntryPoints = EntryPoints,
            GraphFingerprint = "fp",
            GeneratedAt = Fixed
        };
    }

    /// <summary>
    /// Tres capacidades repartidas entre la carpeta de la web (acciones y formularios, que comparten un marco) y la del dominio;
    /// dos de ellas cuelgan de la misma carpeta padre de dominio. Es la forma del caso que la estructura sola resuelve mal.
    /// </summary>
    private static Repo ThreeCapabilitiesRepo(out Dictionary<string, List<string>> capabilityFiles)
    {
        var repo = new Repo();
        capabilityFiles = [];
        var fw = new[] { "Action", "ActionForm", "ActionForward", "ActionMapping", "RequestContext" }
            .Select(n => repo.Add(n, $"src/main/java/app/web/fw/{n}.java")).ToList();
        repo.Dense(fw, 1);

        foreach (var (capability, parent) in new[] { ("Parcel", "shipping/transit"), ("Ledger", "shipping/books"), ("Tenant", "tenancy") })
        {
            var domain = new[] { "Service", "Policy", "Store", "Entry", "Record" }
                .Select(n => repo.Add(capability + n, $"src/main/java/app/domain/{parent}/{capability}{n}.java")).ToList();
            repo.Dense(domain);
            var list = repo.Add(capability + "ListAction", $"src/main/java/app/web/actions/{capability}ListAction.java");
            var edit = repo.Add(capability + "EditAction", $"src/main/java/app/web/actions/{capability}EditAction.java");
            var form = repo.Add(capability + "Form", $"src/main/java/app/web/forms/{capability}Form.java");
            repo.Link(list, domain[0], 2);
            repo.Link(edit, domain[0], 1);
            repo.Link(edit, form, 3);
            foreach (var action in new[] { list, edit })
            {
                foreach (var infra in fw)
                    repo.Link(action, infra, 2);
            }
            repo.Link(form, "ActionForm", 3);
            capabilityFiles[capability] = [.. repo.Types.Where(t => t.Name.StartsWith(capability, StringComparison.Ordinal)).Select(t => t.File)];
        }
        return repo;
    }

    private static DomainMap Build(Repo repo, PreparationOptions? options = null, bool reversed = false) =>
        DomainMapBuilder.Build(repo.Input(reversed), options ?? new PreparationOptions());

    private static string Json(DomainMap map) => PreparationArtifacts.Serialize(map);

    private static SqlTableReference Sql(string table, string file, int line = 1, string? schema = null) =>
        new(table, schema, file, line, "SELECT");

    [Fact]
    public void Tables_that_the_code_writes_as_sql_count_for_the_subsystem_of_the_file_that_writes_them()
    {
        var repo = ThreeCapabilitiesRepo(out var capabilityFiles);
        var parcel = capabilityFiles["Parcel"];
        var ledger = capabilityFiles["Ledger"];
        var input = repo.Input() with
        {
            SqlTables =
            [
                Sql("shipment", parcel[0]), Sql("SHIPMENT", parcel[1], 7, "app"), Sql("ledger_entry", ledger[0]), Sql("shipment", ledger[1]),
                // Un fichero que no es de producción (o que no se conoce) no pertenece a ningún subsistema.
                Sql("in_a_test", "src/test/java/app/SomeTest.java")
            ]
        };

        var map = DomainMapBuilder.Build(input, new PreparationOptions());

        Subsystem Of(string file) => map.Subsystems.Single(s => s.Files.Contains(file));
        // Dos ficheros del mismo subsistema nombran la misma tabla con otra capitalización: una sola (el nombre es el del primer fichero en orden).
        Assert.Equal("shipment", Assert.Single(Of(parcel[0]).Tables), ignoreCase: true);
        Assert.Equal(1, Of(parcel[0]).Metrics.Tables);
        Assert.Equal(["ledger_entry", "shipment"], Of(ledger[0]).Tables);
        Assert.Equal(2, Of(ledger[0]).Metrics.Tables);
        Assert.Empty(Of(capabilityFiles["Tenant"][0]).Tables);
        Assert.Equal(0, Of(capabilityFiles["Tenant"][0]).Metrics.Tables);
        Assert.DoesNotContain(map.Subsystems, s => s.Tables.Contains("in_a_test"));
    }

    [Fact]
    public void Sql_tables_join_the_graph_tables_without_repeating_one_that_differs_only_in_case()
    {
        var repo = ThreeCapabilitiesRepo(out var capabilityFiles);
        var parcel = capabilityFiles["Parcel"];
        repo.TableUses.Add(("SHIPMENT", repo.Types.First(t => t.File == parcel[0]).Name));
        var input = repo.Input() with { SqlTables = [Sql("shipment", parcel[1]), Sql("carrier", parcel[1])] };

        var map = DomainMapBuilder.Build(input, new PreparationOptions());

        var subsystem = map.Subsystems.Single(s => s.Files.Contains(parcel[0]));
        // El nombre es el del grafo, que se lee primero.
        Assert.Equal(["carrier", "SHIPMENT"], subsystem.Tables);
        Assert.Equal(2, subsystem.Metrics.Tables);
    }

    [Fact]
    public void Sql_tables_do_not_change_the_clusters_or_their_ids_and_are_deterministic()
    {
        var repo = ThreeCapabilitiesRepo(out var capabilityFiles);
        var withSql = repo.Input() with { SqlTables = [Sql("shipment", capabilityFiles["Parcel"][0]), Sql("shipment", capabilityFiles["Ledger"][0])] };

        var plain = DomainMapBuilder.Build(repo.Input(), new PreparationOptions());
        var one = DomainMapBuilder.Build(withSql, new PreparationOptions());
        var two = DomainMapBuilder.Build(withSql with { SqlTables = [.. withSql.SqlTables.Reverse()] }, new PreparationOptions());

        Assert.Equal(plain.Subsystems.Select(s => (s.Id, s.Files.Count)), one.Subsystems.Select(s => (s.Id, s.Files.Count)));
        Assert.Equal(Json(one), Json(two));
    }

    [Fact]
    public void A_saved_map_gets_the_sql_tables_added_by_the_files_of_its_subsystems()
    {
        var repo = ThreeCapabilitiesRepo(out var capabilityFiles);
        var saved = DomainMapBuilder.Build(repo.Input(), new PreparationOptions());
        Assert.All(saved.Subsystems, s => Assert.Empty(s.Tables));

        var updated = DomainMapBuilder.WithSqlTables(saved, [Sql("shipment", capabilityFiles["Parcel"][0]), Sql("Shipment", capabilityFiles["Parcel"][2])]);

        var parcel = updated.Subsystems.Single(s => s.Files.Contains(capabilityFiles["Parcel"][0]));
        Assert.Equal(["shipment"], parcel.Tables);
        Assert.Equal(1, parcel.Metrics.Tables);
        // Lo demás del mapa no cambia, y sin referencias es el mismo mapa.
        Assert.Equal(saved.Subsystems.Select(s => s.Id), updated.Subsystems.Select(s => s.Id));
        Assert.Same(saved, DomainMapBuilder.WithSqlTables(saved, []));
    }

    [Fact]
    public void A_capability_spread_over_distant_branches_is_one_subsystem_and_capabilities_stay_apart()
    {
        var repo = ThreeCapabilitiesRepo(out var capabilityFiles);

        var map = Build(repo);

        Assert.Equal(DomainMapBases.Graph, map.Basis);
        var owner = capabilityFiles.ToDictionary(c => c.Key, c => c.Value.Select(f => map.Subsystems.Single(s => s.Files.Contains(f)).Id).Distinct().ToList());
        Assert.All(owner, c => Assert.Single(c.Value));
        Assert.Equal(3, owner.Values.Select(v => v[0]).Distinct().Count());
        // El marco compartido (web/fw) no se funde con ninguna capacidad: sale como su propio subsistema.
        var infra = map.Subsystems.Single(s => s.Files.Contains("src/main/java/app/web/fw/Action.java"));
        Assert.Equal(5, infra.Files.Count);
        Assert.DoesNotContain(owner.Values, v => v[0] == infra.Id);
        // Y cada subsistema lleva el término de su capacidad como dominante.
        Assert.Equal(["parcel", "ledger", "tenant"], new[] { "Parcel", "Ledger", "Tenant" }.Select(c => map.Subsystems.Single(s => s.Id == owner[c][0]).DominantTerm));
        Assert.Empty(map.UnassignedFiles);
    }

    [Fact]
    public void Every_production_file_is_in_exactly_one_subsystem_or_unassigned()
    {
        var repo = ThreeCapabilitiesRepo(out _);
        repo.LooseFiles.AddRange(["src/main/webapp/parcel_list.jsp", "src/main/webapp/other/readme.js", "scripts/lonely.js"]);

        var map = Build(repo);

        var assigned = map.Subsystems.SelectMany(s => s.Files).Concat(map.UnassignedFiles).ToList();
        Assert.Equal(repo.Files.Count, assigned.Count);
        Assert.Equal(repo.Files.Order(StringComparer.Ordinal), assigned.Order(StringComparer.Ordinal));
        Assert.All(map.Subsystems, s => Assert.Equal(s.Files.Count, s.Metrics.Files));
        Assert.All(map.Subsystems, s => Assert.Equal(s.Files.OrderBy(f => f, StringComparer.Ordinal), s.Files));
        Assert.Equal(map.Subsystems.Select(s => s.Id).Order(StringComparer.Ordinal), map.Subsystems.Select(s => s.Id));
        Assert.Equal(map.Subsystems.Count, map.Subsystems.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void Two_runs_and_a_permuted_input_give_the_same_ids_and_files()
    {
        var repo = ThreeCapabilitiesRepo(out _);
        repo.LooseFiles.Add("src/main/webapp/parcel_list.jsp");

        var first = Json(Build(repo));
        var second = Json(Build(repo));
        var permuted = Json(Build(repo, reversed: true));

        Assert.Equal(first, second);
        Assert.Equal(first, permuted);
    }

    [Fact]
    public void Files_without_types_go_to_the_subsystem_whose_vocabulary_matches_or_to_their_folder_siblings()
    {
        var repo = ThreeCapabilitiesRepo(out _);
        repo.LooseFiles.Add("src/main/webapp/parcel_list.jsp");        // por vocabulario
        repo.LooseFiles.Add("src/main/java/app/web/fw/menu.js");       // sin término, pero comparte carpeta con el marco
        repo.LooseFiles.Add("scripts/standalone.js");                  // ni una cosa ni otra

        // Sin subsistema compartido: aquí el marco de acciones es un subsistema más y recibe sus ficheros sueltos (un fichero suelto nunca
        // se asigna a `shared`, ver SharedInfrastructureTests).
        var map = Build(repo, new PreparationOptions { SharedMinConsumers = 0 });

        var parcel = map.Subsystems.Single(s => s.DominantTerm == "parcel");
        Assert.Contains("src/main/webapp/parcel_list.jsp", parcel.Files);
        var infra = map.Subsystems.Single(s => s.Files.Contains("src/main/java/app/web/fw/Action.java"));
        Assert.Contains("src/main/java/app/web/fw/menu.js", infra.Files);
        Assert.Equal(["scripts/standalone.js"], map.UnassignedFiles);
    }

    [Fact]
    public void Without_dependencies_the_folder_structure_decides_and_the_map_declares_it()
    {
        var repo = new Repo();
        foreach (var folder in new[] { "alpha", "beta", "gamma" })
        {
            for (var i = 0; i < 12; i++)
                repo.Add($"T{folder}{i}", $"src/{folder}/T{folder}{i}.java");
        }

        var map = Build(repo);

        Assert.Equal(DomainMapBases.StructureOnly, map.Basis);
        Assert.Contains(map.Warnings, w => w.Contains("estructura de carpetas", StringComparison.Ordinal));
        Assert.Equal(3, map.Subsystems.Count);
        Assert.All(map.Subsystems, s => Assert.Single(s.Files.Select(f => f.Split('/')[1]).Distinct()));
        Assert.Empty(map.UnassignedFiles);
    }

    [Fact]
    public void No_production_files_give_an_empty_map_with_a_warning_and_no_graph_gives_the_structure()
    {
        var empty = DomainMapBuilder.Build(new DomainMapInput { GeneratedAt = Fixed }, new PreparationOptions());
        Assert.Empty(empty.Subsystems);
        Assert.Single(empty.Warnings);

        var noGraph = DomainMapBuilder.Build(new DomainMapInput
        {
            ProductionFiles = [.. Enumerable.Range(0, 6).Select(i => new FileLines { Path = $"src/a/F{i}.java", Language = "java", Lines = 1 })],
            GeneratedAt = Fixed
        }, new PreparationOptions());
        Assert.Equal(DomainMapBases.StructureOnly, noGraph.Basis);
        Assert.Single(noGraph.Subsystems);
    }

    [Fact]
    public void Tiny_clusters_are_absorbed_by_their_strongest_link_or_their_nearest_folder_or_left_unassigned()
    {
        var repo = new Repo();
        var big = Enumerable.Range(0, 10).Select(i => repo.Add($"Qa{i}x", $"src/big/Qa{i}x.java")).ToList();
        repo.Dense(big);
        var tiny = new[] { repo.Add("Wb0y", "src/tiny/Wb0y.java"), repo.Add("Wb1y", "src/tiny/Wb1y.java") };
        repo.Link(tiny[0], tiny[1]);
        repo.Link(tiny[0], big[0], 1);
        repo.Add("Ec0z", "src/big/Ec0z.java");          // aislado, en la carpeta del grande
        repo.Add("Fd0w", "src/far/deep/Fd0w.java");     // aislado y sin carpeta pariente

        var map = Build(repo);

        var bigSubsystem = map.Subsystems.Single(s => s.Files.Contains("src/big/Qa0x.java"));
        Assert.Contains("src/tiny/Wb0y.java", bigSubsystem.Files);
        Assert.Contains("src/tiny/Wb1y.java", bigSubsystem.Files);
        Assert.Contains("src/big/Ec0z.java", bigSubsystem.Files);
        Assert.Equal(["src/far/deep/Fd0w.java"], map.UnassignedFiles);
        Assert.Single(map.Subsystems);
    }

    [Fact]
    public void A_community_over_the_maximum_size_is_split_into_smaller_subsystems()
    {
        // Dos grupos densos de 100 tipos cada uno: más de los 40 ficheros del suelo del tamaño máximo.
        var repo = new Repo();
        var random = new Random(4242);
        foreach (var group in new[] { "a", "b" })
        {
            var names = Enumerable.Range(0, 100).Select(i => repo.Add($"Zz{group}{i}", $"src/{group}/Zz{group}{i}.java")).ToList();
            for (var i = 0; i < names.Count; i++)
            {
                for (var k = 0; k < 6; k++)
                    repo.Link(names[i], names[random.Next(names.Count)], 1 + random.Next(3));
            }
        }

        var map = Build(repo);

        var cap = DomainMapBuilder.MaxSubsystemFiles(repo.Files.Count, new PreparationOptions());
        Assert.Equal(PreparationOptions.MinSubsystemCap, cap);
        Assert.True(map.Subsystems.Count > 2, $"{map.Subsystems.Count} subsistemas");
        Assert.All(map.Subsystems, s => Assert.True(s.Files.Count <= cap, $"{s.Id} tiene {s.Files.Count} ficheros"));
        Assert.Equal(200, map.Subsystems.Sum(s => s.Files.Count) + map.UnassignedFiles.Count);
    }

    [Fact]
    public void Subsystems_carry_their_entry_points_tables_states_lines_and_key_types()
    {
        var repo = new Repo();
        var names = new[] { "Item", "ItemDao", "ItemAction", "ItemForm", "ItemStatus" }.Select(n => repo.Add(n, $"src/main/java/app/item/{n}.java", n == "ItemStatus" ? "Enum" : "Class")).ToList();
        repo.Dense(names);
        repo.Link("ItemAction", "Item", 9);
        repo.TableUses.Add(("ITEMS", "ItemDao"));
        repo.TableUses.Add(("ITEM_AUDIT", "ItemDao"));
        repo.EntryPoints.Add(new InventoryEntryPoint { Kind = EntryPointKinds.StrutsAction, Name = "/item.do", File = "src/main/java/app/item/ItemAction.java", Line = 4 });
        repo.EntryPoints.Add(new InventoryEntryPoint { Kind = EntryPointKinds.Scheduled, Name = "app.item.ItemDao.nightly", File = "src/main/java/app/item/ItemDao.java", Line = 9 });
        repo.EntryPoints.Add(new InventoryEntryPoint { Kind = EntryPointKinds.Main, Name = "elsewhere", File = "src/main/java/app/other/Main.java", Line = 1 });

        var map = Build(repo);

        var subsystem = Assert.Single(map.Subsystems);
        Assert.Equal(5, subsystem.Metrics.Files);
        Assert.Equal(50, subsystem.Metrics.Loc);
        Assert.Equal(["ITEMS", "ITEM_AUDIT"], subsystem.Tables);
        Assert.Equal(2, subsystem.Metrics.Tables);
        Assert.Equal(1, subsystem.Metrics.States);
        Assert.Equal(2, subsystem.Metrics.EntryPoints);
        Assert.Equal(2, subsystem.Metrics.EntryPointKinds);
        Assert.Equal(["/item.do", "app.item.ItemDao.nightly"], subsystem.EntryPoints.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal("app.item.Item", subsystem.KeyTypes[0]);
        Assert.Equal(0, subsystem.Metrics.Centrality);
        Assert.Equal(["src/main/java/app/item"], subsystem.Roots);
        Assert.Equal(SubsystemNameOrigins.Derived, subsystem.NameOrigin);
        // Con cinco ficheros todo término es «de más de la mitad»: sin término dominante, el nombre sale de la carpeta.
        Assert.Equal("", subsystem.DominantTerm);
        Assert.Equal("app/item", subsystem.Name);
    }

    [Fact]
    public void Centrality_is_the_share_of_cross_subsystem_dependencies_that_land_in_each_subsystem()
    {
        var repo = new Repo();
        var alpha = Enumerable.Range(0, 6).Select(i => repo.Add($"Aa{i}q", $"src/a/Aa{i}q.java")).ToList();
        var beta = Enumerable.Range(0, 6).Select(i => repo.Add($"Bb{i}q", $"src/b/Bb{i}q.java")).ToList();
        repo.Dense(alpha, 5);
        repo.Dense(beta, 5);
        repo.Link(beta[0], alpha[0], 3);
        repo.Link(beta[1], alpha[1], 1);

        var map = Build(repo);

        var a = map.Subsystems.Single(s => s.Files.Contains("src/a/Aa0q.java"));
        var b = map.Subsystems.Single(s => s.Files.Contains("src/b/Bb0q.java"));
        Assert.Equal(1.0, a.Metrics.Centrality, 6);
        Assert.Equal(0.0, b.Metrics.Centrality, 6);
    }

    [Fact]
    public void Ids_are_slugs_unique_and_stable_and_clusters_with_the_same_base_get_a_hash_of_their_files()
    {
        var repo = new Repo();
        foreach (var branch in new[] { "x", "y" })
        {
            var names = Enumerable.Range(0, 6).Select(i => repo.Add($"Q{branch}{i}", $"src/{branch}/common/api/Q{branch}{i}.java")).ToList();
            repo.Dense(names);
        }

        var map = Build(repo);
        var again = Build(repo, reversed: true);

        Assert.Equal(2, map.Subsystems.Count);
        Assert.All(map.Subsystems, s => Assert.Matches("^common-api-[0-9a-f]{6}$", s.Id));
        Assert.Equal(2, map.Subsystems.Select(s => s.Id).Distinct().Count());
        Assert.Equal(map.Subsystems.Select(s => s.Id), again.Subsystems.Select(s => s.Id));
        // Los nombres derivados tampoco se repiten.
        Assert.Equal(2, map.Subsystems.Select(s => s.Name).Distinct().Count());
        Assert.All(map.Subsystems, s => Assert.StartsWith("common/api (common-api-", s.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void Roots_are_the_topmost_folders_by_weight_and_at_most_eight()
    {
        var files = new List<string>();
        files.AddRange(Enumerable.Range(0, 3).Select(i => $"a/b/c/F{i}.java"));
        files.Add("a/b/G.java");               // a/b es ancestro de a/b/c: solo cuenta a/b
        files.AddRange(Enumerable.Range(0, 12).Select(i => $"top{i:00}/H.java"));

        var roots = DomainMapBuilder.RootsOf(files);

        Assert.Equal(DomainMapBuilder.MaxRoots, roots.Count);
        Assert.Equal("a/b", roots[0]);
        Assert.DoesNotContain("a/b/c", roots);
        Assert.Equal(["top00", "top01", "top02", "top03", "top04", "top05", "top06"], roots.Skip(1));
    }

    [Fact]
    public void The_structural_clusterer_descends_through_single_branches_and_splits_where_the_tree_forks()
    {
        var files = new List<string>();
        files.AddRange(Enumerable.Range(0, 30).Select(i => $"src/main/java/com/acme/billing/B{i}.java"));
        files.AddRange(Enumerable.Range(0, 30).Select(i => $"src/main/java/com/acme/orders/C{i}.java"));
        files.AddRange(Enumerable.Range(0, 2).Select(i => $"src/main/java/com/acme/misc/M{i}.java"));
        files.Add("src/main/java/com/acme/Root.java");

        var groups = StructuralClusterer.GroupByFolders(files, 5);

        Assert.Equal(3, groups.Count);
        Assert.Equal(30, groups.Single(g => g[0].Contains("/billing/")).Count);
        Assert.Equal(30, groups.Single(g => g[0].Contains("/orders/")).Count);
        // El residuo (el fichero de la carpeta padre y la rama pequeña) forma un grupo y lo absorbe quien llama.
        Assert.Equal(["src/main/java/com/acme/Root.java", "src/main/java/com/acme/misc/M0.java", "src/main/java/com/acme/misc/M1.java"], groups.Single(g => g.Count == 3));
        Assert.Equal(files.Count, groups.Sum(g => g.Count));
    }

    [Fact]
    public void The_affinity_graph_adds_coherent_name_terms_but_not_generic_ones_and_induces_subgraphs()
    {
        var repo = new Repo();
        var parcels = new[] { "ParcelA", "ParcelB", "ParcelC" }.Select(n => repo.Add(n, $"src/{n}.java")).ToList();
        repo.Link("ParcelA", "ParcelB");
        repo.Link("ParcelB", "ParcelC");
        // «list»: tres tipos sin ninguna relación entre sí, así que el término no es coherente en el código.
        foreach (var n in new[] { "ListX", "ListY", "ListZ" })
            repo.Add(n, $"src/{n}.java");
        var graph = repo.Graph();

        var affinity = AffinityGraph.Build(graph, new PreparationOptions(), 100, out var indexOf);

        Assert.Equal(graph.Nodes.Count, indexOf.Length);
        Assert.Contains(affinity.Nodes, n => n.IsVirtual && n.Key == "term:parcel");
        Assert.DoesNotContain(affinity.Nodes, n => n.IsVirtual && n.Key == "term:list");
        Assert.Equal(["ParcelA", "ParcelB", "ParcelC"], parcels.Where(p => affinity.Nodes[indexOf[graph.Nodes.Single(n => n.Name == p).Index]].Name == p));

        var members = new[] { graph.Nodes.Single(n => n.Name == "ParcelA").Index, graph.Nodes.Single(n => n.Name == "ParcelB").Index }.Select(i => indexOf[i]).ToList();
        var sub = AffinityGraph.Induce(affinity, members, out var subIndexOf);
        Assert.Equal(members.Count, subIndexOf.Length);
        Assert.Contains(sub.Nodes, n => n.Key == "term:parcel");
        Assert.DoesNotContain(sub.Nodes, n => n.Name == "ParcelC");
    }

    [Fact]
    public void The_maximum_share_is_read_from_the_configuration_and_a_bad_value_falls_back_with_a_warning()
    {
        var good = PreparationOptions.FromConfiguration(TestConfig.FromPairs(("CodebaseAnalysis:Preparation:MaxSubsystemShare", "0.2")));
        var bad = PreparationOptions.FromConfiguration(TestConfig.FromPairs(("CodebaseAnalysis:Preparation:MaxSubsystemShare", "mucho")));

        Assert.Equal(0.2, good.MaxSubsystemShare);
        Assert.Empty(good.Warnings);
        Assert.Equal(new PreparationOptions().MaxSubsystemShare, bad.MaxSubsystemShare);
        Assert.Contains("MaxSubsystemShare", Assert.Single(bad.Warnings));
    }

    [Fact]
    public void The_maximum_subsystem_size_is_a_share_of_the_files_with_a_floor()
    {
        Assert.Equal(40, DomainMapBuilder.MaxSubsystemFiles(36, new PreparationOptions()));
        Assert.Equal(405, DomainMapBuilder.MaxSubsystemFiles(8082, new PreparationOptions()));
        Assert.Equal(1000, DomainMapBuilder.MaxSubsystemFiles(8000, new PreparationOptions { MaxSubsystemShare = 0.125 }));
    }
}
