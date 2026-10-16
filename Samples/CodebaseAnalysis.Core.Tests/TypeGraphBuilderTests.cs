using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Tests.Enrichment;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="TypeGraphBuilder"/> (T7): el grafo agregado a nivel de tipo que se lee de cbm. Sobre grafos sintéticos de
/// <see cref="CbmTestGraph"/>: las llamadas de método a método suben al tipo que los define, solo cuentan los tipos de
/// producción, la herencia que cbm deja como propiedad (Java) se resuelve por nombre y el resultado no depende del orden de inserción.
/// </summary>
public sealed class TypeGraphBuilderTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private Task<CbmTestGraph> NewGraphAsync() =>
        CbmTestGraph.CreateAsync(Path.Combine(_temp.Path, $"{Guid.NewGuid():N}.db"), "demo");

    private static Task<TypeGraph> ReadAsync(CbmTestGraph graph, Func<string, bool>? production = null) =>
        TypeGraphBuilder.ReadAsync(graph.DatabasePath, production ?? (path => path.StartsWith("src/main/", StringComparison.Ordinal)), CancellationToken.None);

    private static (string From, string To, double Weight)[] EdgesOf(TypeGraph graph) =>
        [.. graph.Edges.Select(e => (graph.Nodes[e.Source].Name, graph.Nodes[e.Target].Name, e.Weight))];

    /// <summary>Un tipo con un método, unidos por <c>DEFINES_METHOD</c>; devuelve los ids del tipo y del método.</summary>
    private static async Task<(long Type, long Method)> TypeWithMethodAsync(CbmTestGraph graph, string name, string file, string label = "Class")
    {
        var type = await graph.NodeAsync(label, name, $"demo.{name}", file, 1, 20);
        var method = await graph.NodeAsync("Method", $"{name}Run", $"demo.{name}.run", file, 3, 5);
        await graph.EdgeAsync(type, method, "DEFINES_METHOD");
        return (type, method);
    }

    [Fact]
    public async Task Calls_between_methods_become_weighted_dependencies_between_their_types()
    {
        using var graph = await NewGraphAsync();
        var (a, aMethod) = await TypeWithMethodAsync(graph, "Alpha", "src/main/Alpha.java");
        var (b, bMethod) = await TypeWithMethodAsync(graph, "Beta", "src/main/Beta.java");
        var second = await graph.NodeAsync("Method", "AlphaOther", "demo.Alpha.other", "src/main/Alpha.java", 8, 9);
        await graph.EdgeAsync(a, second, "DEFINES_METHOD");
        await graph.EdgeAsync(aMethod, bMethod, "CALLS");
        await graph.EdgeAsync(second, bMethod, "CALLS");
        await graph.EdgeAsync(aMethod, b, "USAGE");
        await graph.EdgeAsync(a, b, "INHERITS");
        var c = await graph.NodeAsync("Interface", "Gamma", "demo.Gamma", "src/main/Gamma.java", 1, 5);
        await graph.EdgeAsync(bMethod, c, "THROWS");

        var result = await ReadAsync(graph);

        Assert.Equal(["Alpha", "Beta", "Gamma"], result.Nodes.Select(n => n.Name));
        // Alpha→Beta: dos CALLS (1+1) + una USAGE (1) + INHERITS (3); Beta→Gamma: THROWS (0,5). El self-loop de Alpha no cuenta.
        Assert.Equal([("Alpha", "Beta", 6.0), ("Beta", "Gamma", 0.5)], EdgesOf(result));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Only_production_files_contribute_types_and_edges()
    {
        using var graph = await NewGraphAsync();
        var (_, aMethod) = await TypeWithMethodAsync(graph, "Alpha", "src/main/Alpha.java");
        var (_, testMethod) = await TypeWithMethodAsync(graph, "AlphaTest", "src/test/AlphaTest.java");
        await graph.NodeAsync("Class", "project", "demo.pom.project", "pom.xml", 1, 1);
        await graph.EdgeAsync(testMethod, aMethod, "CALLS");
        await graph.EdgeAsync(aMethod, testMethod, "CALLS");

        var result = await ReadAsync(graph);

        Assert.Equal(["Alpha"], result.Nodes.Select(n => n.Name));
        Assert.Empty(result.Edges);
        Assert.True(result.IsPoor, "sin dependencias entre tipos de producción el grafo no sirve");
    }

    [Fact]
    public async Task Nodes_without_a_defining_type_fall_on_the_main_type_of_their_file()
    {
        using var graph = await NewGraphAsync();
        var (_, aMethod) = await TypeWithMethodAsync(graph, "Alpha", "src/main/Alpha.java");
        await graph.NodeAsync("Class", "Beta", "demo.Beta", "src/main/Beta.java", 1, 9);
        // Un campo de Beta y un módulo de fichero sin DEFINES_METHOD: caen en el tipo principal de su fichero.
        var field = await graph.NodeAsync("Field", "count", "demo.Beta.count", "src/main/Beta.java", 2, 2);
        var module = await graph.NodeAsync("Module", "Beta", "demo.Beta.__file__", "src/main/Beta.java", 1, 9);
        await graph.EdgeAsync(aMethod, field, "CALLS");
        await graph.EdgeAsync(module, aMethod, "USAGE");

        var result = await ReadAsync(graph);

        Assert.Equal([("Alpha", "Beta", 1.0), ("Beta", "Alpha", 1.0)], EdgesOf(result));
    }

    [Fact]
    public async Task Base_classes_that_cbm_leaves_only_as_a_property_become_inheritance_edges()
    {
        using var graph = await NewGraphAsync();
        await graph.NodeAsync("Interface", "Contract", "demo.Contract", "src/main/Contract.java", 1, 3);
        await graph.NodeAsync("Class", "Impl", "demo.Impl", "src/main/Impl.java", 1, 9,
            "{\"base_classes\":[\"extends Root<T>\",\"implements Contract, java.io.Serializable\"]}");
        await graph.NodeAsync("Class", "Root", "demo.Root", "src/main/Root.java", 1, 9);
        // Dos tipos con el mismo nombre simple en carpetas distintas: no se adivina a cuál se refiere (-1).
        await graph.NodeAsync("Class", "Twin", "demo.a.Twin", "src/main/a/Twin.java", 1, 3);
        await graph.NodeAsync("Class", "Twin", "demo.b.Twin", "src/main/b/Twin.java", 1, 3);
        await graph.NodeAsync("Class", "Child", "demo.Child", "src/main/Child.java", 1, 9, "{\"base_classes\":[\"extends Twin\"]}");

        var result = await ReadAsync(graph);

        Assert.Equal([("Impl", "Contract", 3.0), ("Impl", "Root", 3.0)], EdgesOf(result));
    }

    [Fact]
    public async Task A_type_with_invalid_properties_json_does_not_break_the_read()
    {
        using var graph = await NewGraphAsync();
        var (_, aMethod) = await TypeWithMethodAsync(graph, "Alpha", "src/main/Alpha.java");
        var (_, bMethod) = await TypeWithMethodAsync(graph, "Beta", "src/main/Beta.java");
        await graph.NodeAsync("Class", "Broken", "demo.Broken", "src/main/Broken.java", 1, 9, "{\"base_classes\":[\"extends Alp");
        await graph.EdgeAsync(aMethod, bMethod, "CALLS");

        var result = await ReadAsync(graph);

        Assert.Empty(result.Warnings);
        Assert.Equal(["Alpha", "Beta", "Broken"], result.Nodes.Select(n => n.Name));
        Assert.Equal([("Alpha", "Beta", 1.0)], EdgesOf(result));
    }

    [Fact]
    public async Task Tables_become_virtual_nodes_that_join_the_types_touching_them()
    {
        using var graph = await NewGraphAsync();
        var (_, aMethod) = await TypeWithMethodAsync(graph, "Alpha", "src/main/Alpha.java");
        var (_, bMethod) = await TypeWithMethodAsync(graph, "Beta", "src/main/Beta.java");
        var table = await graph.NodeAsync("Table", "ORDERS", "table.ORDERS");
        await graph.EdgeAsync(aMethod, table, "READS");
        await graph.EdgeAsync(bMethod, table, "WRITES");

        var result = await ReadAsync(graph);

        Assert.Equal(3, result.Nodes.Count);
        Assert.Equal(2, result.TypeCount);
        var orders = Assert.Single(result.Nodes, n => n.IsVirtual);
        Assert.Equal("ORDERS", orders.Name);
        // Cada tipo y la tabla, en ambos sentidos.
        Assert.Equal(4, result.Edges.Count);
        Assert.All(result.Edges, e => Assert.True(result.Nodes[e.Source].IsVirtual ^ result.Nodes[e.Target].IsVirtual));
    }

    [Fact]
    public async Task Struts_routes_join_their_action_and_form()
    {
        using var graph = await NewGraphAsync();
        await graph.NodeAsync("Class", "PayAction", "demo.PayAction", "src/main/PayAction.java", 1, 9);
        await graph.NodeAsync("Class", "PayForm", "demo.PayForm", "src/main/PayForm.java", 1, 9);
        await graph.NodeAsync("Route", "/pay", "__route__ANY__/pay", "WEB-INF/struts-config.xml", 3, 3,
            "{\"framework\":\"struts\",\"route_path\":\"/pay.do\",\"action_type\":\"com.acme.PayAction\",\"form_type\":\"com.acme.PayForm\"}");

        var result = await ReadAsync(graph);

        Assert.Equal([("PayAction", "PayForm", 2.0)], EdgesOf(result));
    }

    [Fact]
    public async Task The_graph_is_the_same_whatever_the_insertion_order()
    {
        async Task<TypeGraph> BuildAsync(bool reversed)
        {
            using var graph = await NewGraphAsync();
            var names = new[] { "Alpha", "Beta", "Gamma", "Delta" };
            var types = new Dictionary<string, (long Type, long Method)>();
            foreach (var name in reversed ? names.Reverse() : names)
                types[name] = await TypeWithMethodAsync(graph, name, $"src/main/{name}.java");
            var calls = new[] { ("Alpha", "Beta"), ("Beta", "Gamma"), ("Gamma", "Alpha"), ("Delta", "Alpha"), ("Alpha", "Beta") };
            foreach (var (from, to) in reversed ? calls.Reverse() : calls)
            {
                try
                {
                    await graph.EdgeAsync(types[from].Method, types[to].Method, "CALLS");
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // La arista repetida de la lista: UNIQUE(source, target, type).
                }
            }
            return await ReadAsync(graph);
        }

        var forward = await BuildAsync(reversed: false);
        var backward = await BuildAsync(reversed: true);

        Assert.Equal(forward.Nodes.Select(n => (n.Index, n.Key, n.File)), backward.Nodes.Select(n => (n.Index, n.Key, n.File)));
        Assert.Equal(forward.Edges, backward.Edges);
        Assert.Equal(["demo.Alpha", "demo.Beta", "demo.Delta", "demo.Gamma"], forward.Nodes.Select(n => n.Key));
    }

    [Fact]
    public async Task A_missing_database_gives_an_empty_poor_graph_with_a_warning()
    {
        var result = await TypeGraphBuilder.ReadAsync(Path.Combine(_temp.Path, "nope.db"), _ => true, CancellationToken.None);

        Assert.Empty(result.Nodes);
        Assert.True(result.IsPoor);
        Assert.Contains("estructura", Assert.Single(result.Warnings));
    }

    [Fact]
    public void The_builder_merges_duplicate_nodes_sums_parallel_edges_and_ignores_loops()
    {
        var builder = new TypeGraph.Builder();
        var b = builder.AddNode("b", "B", "Class", "b.java");
        var a = builder.AddNode("a", "A", "Class", "a.java");
        Assert.Equal(a, builder.AddNode("a", "other", "Class", "x.java"));
        builder.AddEdge(a, b, 1.5);
        builder.AddEdge(a, b, 2);
        builder.AddEdge(a, a, 9);
        builder.AddEdge(b, a, 0);
        builder.AddEdge("a", "missing", 4);

        var graph = builder.Build();

        Assert.Equal(["a", "b"], graph.Nodes.Select(n => n.Key));
        Assert.Equal([new TypeEdge(0, 1, 3.5)], graph.Edges);
        Assert.Equal("a.java", graph.Nodes[0].File);
    }
}
