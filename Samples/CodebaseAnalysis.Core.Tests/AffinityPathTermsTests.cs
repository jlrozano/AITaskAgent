using CodebaseAnalysis.Core.Preparation;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Experimento E1 de la medición de la preparación: los términos de la <b>ruta de carpeta</b> (no solo los del nombre de clase) unen
/// tipos en el grafo de afinidad, con el mismo filtro de coherencia y un peso propio (<see cref="PreparationOptions.PathTermWeightFactor"/>).
/// Repositorios sintéticos con nombres genéricos de prueba.
/// </summary>
public sealed class AffinityPathTermsTests
{
    private static readonly DateTimeOffset Fixed = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static TypeGraph.Builder Builder() => new();

    /// <summary>Un grafo con tres tipos <c>Parcel*</c> bajo <c>parcel/</c> que se llaman entre sí y tres tipos sin relación bajo <c>legacy/</c>.</summary>
    private static TypeGraph CoherentAndIncoherentFolders()
    {
        var builder = Builder();
        foreach (var n in new[] { "ParcelA", "ParcelB", "ParcelC" })
            builder.AddNode(n, n, "Class", $"src/parcel/{n}.java");
        builder.AddEdge("ParcelA", "ParcelB", 2);
        builder.AddEdge("ParcelB", "ParcelC", 2);
        // Carpetas hermanas distintas: ningún tipo comparte carpeta con otro, así que solo el término de la ruta podría unirlos.
        foreach (var n in new[] { "Xqa", "Xqb", "Xqc" })
            builder.AddNode(n, n, "Class", $"src/legacy/{n.ToLowerInvariant()}/{n}.java");
        return builder.Build();
    }

    [Fact]
    public void Path_terms_add_a_node_per_coherent_folder_term_and_not_for_folders_whose_types_do_not_relate()
    {
        var graph = CoherentAndIncoherentFolders();

        var affinity = AffinityGraph.Build(graph, new PreparationOptions { PathTermWeightFactor = 2 }, 100, out _);

        Assert.Contains(affinity.Nodes, n => n.IsVirtual && n.Key == "pterm:parcel");
        // «legacy» lo llevan tres tipos que no se relacionan entre sí: no es coherente en el código.
        Assert.DoesNotContain(affinity.Nodes, n => n.IsVirtual && n.Key == "pterm:legacy");
    }

    [Fact]
    public void A_folder_term_that_no_class_name_carries_is_a_layer_and_does_not_join_types()
    {
        var builder = Builder();
        // «web» es la capa de acciones: sus tipos se llaman entre sí a través de un marco común, pero ninguna clase se llama «Web...».
        foreach (var n in new[] { "InvoiceAction", "ReminderAction", "ProductAction", "Frame" })
            builder.AddNode(n, n, "Class", $"src/web/{n}.java");
        builder.AddEdge("InvoiceAction", "Frame", 2);
        builder.AddEdge("ReminderAction", "Frame", 2);
        builder.AddEdge("ProductAction", "Frame", 2);
        foreach (var n in new[] { "OtherA", "OtherB", "OtherC", "OtherD", "OtherE", "OtherF" })
            builder.AddNode(n, n, "Class", $"src/other/{n}.java");
        var graph = builder.Build();

        var affinity = AffinityGraph.Build(graph, new PreparationOptions { PathTermWeightFactor = 2 }, 100, out _);

        Assert.DoesNotContain(affinity.Nodes, n => n.Key == "pterm:web");
    }

    [Fact]
    public void Path_terms_are_off_with_a_zero_weight_and_leave_the_affinity_graph_as_it_was()
    {
        var graph = CoherentAndIncoherentFolders();

        var off = AffinityGraph.Build(graph, new PreparationOptions { PathTermWeightFactor = 0 }, 100, out _);

        Assert.DoesNotContain(off.Nodes, n => n.Key.StartsWith("pterm:", StringComparison.Ordinal));
    }

    [Fact]
    public void Generic_folder_tokens_and_umbrella_terms_do_not_become_path_terms()
    {
        var builder = Builder();
        // «util» es genérico (StopTokens) y «shop» lo llevan casi todos los tipos: paraguas por encima del máximo de miembros.
        foreach (var n in new[] { "TypeA", "TypeB", "TypeC", "TypeD" })
            builder.AddNode(n, n, "Class", $"src/shop/util/{n}.java");
        builder.AddEdge("TypeA", "TypeB", 2);
        builder.AddEdge("TypeB", "TypeC", 2);
        builder.AddEdge("TypeC", "TypeD", 2);
        var graph = builder.Build();

        var affinity = AffinityGraph.Build(graph, new PreparationOptions { PathTermWeightFactor = 2 }, maxTermMembers: 3, out _);

        Assert.DoesNotContain(affinity.Nodes, n => n.Key == "pterm:util");
        Assert.DoesNotContain(affinity.Nodes, n => n.Key == "pterm:shop");
    }

    /// <summary>
    /// Dos capacidades densas (<c>Parcel*</c> bajo <c>parcel/</c>, <c>Ledger*</c> bajo <c>ledger/</c>) y cuatro tipos con nombre sin relación con
    /// ninguna, cada uno en su propia carpeta bajo <c>parcel/</c>, atados con más fuerza al libro que al paquete: solo el término de la ruta
    /// puede llevarlos al paquete.
    /// </summary>
    private static DomainMapInput OrphansUnderAFolder(out IReadOnlyList<string> orphanFiles)
    {
        var builder = Builder();
        var files = new List<string>();
        string Add(string name, string file)
        {
            builder.AddNode(name, name, "Class", file);
            files.Add(file);
            return name;
        }

        var parcels = new[] { "Service", "Policy", "Store", "Entry", "Record", "Query" }.Select(n => Add("Parcel" + n, $"src/parcel/Parcel{n}.java")).ToList();
        var ledgers = new[] { "Service", "Policy", "Store", "Entry", "Record", "Query" }.Select(n => Add("Ledger" + n, $"src/ledger/Ledger{n}.java")).ToList();
        // Un tercer grupo de relleno: sin él «parcel» lo llevaría más de la mitad de los tipos y sería un paraguas.
        var tenants = new[] { "Service", "Policy", "Store", "Entry", "Record", "Query", "Audit", "Quota" }.Select(n => Add("Tenant" + n, $"src/tenant/Tenant{n}.java")).ToList();
        foreach (var group in new[] { parcels, ledgers, tenants })
        {
            for (var i = 0; i < group.Count; i++)
            {
                for (var j = i + 1; j < group.Count; j++)
                    builder.AddEdge(group[i], group[j], 2);
            }
        }

        var orphans = new List<string>();
        foreach (var o in new[] { "Qaz", "Qbz", "Qcz", "Qdz" })
        {
            var name = Add(o, $"src/parcel/orphans/{o.ToLowerInvariant()}/{o}.java");
            builder.AddEdge(name, ledgers[0], 3);
            builder.AddEdge(name, parcels[0], 2);
            orphans.Add($"src/parcel/orphans/{o.ToLowerInvariant()}/{o}.java");
        }
        orphanFiles = orphans;

        return new DomainMapInput
        {
            ProductionFiles = [.. files.Select(f => new FileLines { Path = f, Language = "java", Lines = 10 })],
            Graph = builder.Build(),
            GraphFingerprint = "fp",
            GeneratedAt = Fixed
        };
    }

    private static string OwnerOf(DomainMap map, string file) => map.Subsystems.Single(s => s.Files.Contains(file)).DominantTerm;

    [Fact]
    public void With_path_terms_types_whose_name_does_not_say_the_capability_follow_the_folder_that_does()
    {
        var input = OrphansUnderAFolder(out var orphans);

        var off = DomainMapBuilder.Build(input, new PreparationOptions { PathTermWeightFactor = 0 });
        var on = DomainMapBuilder.Build(input, new PreparationOptions { PathTermWeightFactor = 5 });

        // Sin el término de la ruta mandan las dependencias: el libro, al que están más atados.
        Assert.All(orphans, f => Assert.Equal("ledger", OwnerOf(off, f)));
        // Con él, la carpeta «parcel» los lleva al paquete.
        Assert.All(orphans, f => Assert.Equal("parcel", OwnerOf(on, f)));
    }

    [Fact]
    public void Path_terms_keep_the_discovery_deterministic()
    {
        var input = OrphansUnderAFolder(out _);
        var options = new PreparationOptions { PathTermWeightFactor = 5 };

        var first = PreparationArtifacts.Serialize(DomainMapBuilder.Build(input, options));
        var second = PreparationArtifacts.Serialize(DomainMapBuilder.Build(input, options));

        Assert.Equal(first, second);
    }

    [Fact]
    public void The_path_term_weight_is_read_from_the_configuration_and_a_bad_value_falls_back_with_a_warning()
    {
        var good = PreparationOptions.FromConfiguration(TestConfig.FromPairs(("CodebaseAnalysis:Preparation:PathTermWeightFactor", "2.5")));
        var bad = PreparationOptions.FromConfiguration(TestConfig.FromPairs(("CodebaseAnalysis:Preparation:PathTermWeightFactor", "-1")));

        Assert.Equal(2.5, good.PathTermWeightFactor);
        Assert.Empty(good.Warnings);
        Assert.Equal(PreparationOptions.DefaultPathTermWeightFactor, bad.PathTermWeightFactor);
        // Los términos de la ruta están activados por defecto.
        Assert.True(new PreparationOptions().PathTermWeightFactor > 0);
        Assert.Contains("PathTermWeightFactor", Assert.Single(bad.Warnings));
    }
}
