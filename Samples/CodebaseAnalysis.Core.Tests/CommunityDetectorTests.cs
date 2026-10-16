using System.Diagnostics;
using CodebaseAnalysis.Core.Preparation;
using Xunit.Abstractions;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="CommunityDetector"/> (T8, criterio A2): Louvain determinista sobre el grafo de tipos. Dos grupos unidos por un
/// puente quedan separados, el resultado es idéntico entre ejecuciones y al permutar el orden de inserción, y sobre unos 8 000
/// tipos sintéticos (el tamaño de un repositorio grande) tarda segundos, no decenas de ellos.
/// </summary>
public sealed class CommunityDetectorTests(ITestOutputHelper output)
{
    private static TypeGraph Graph(IEnumerable<string> keys, IEnumerable<(string From, string To, double Weight)> edges)
    {
        var builder = new TypeGraph.Builder();
        foreach (var key in keys)
            builder.AddNode(key, key, "Class", key + ".java");
        foreach (var (from, to, weight) in edges)
            builder.AddEdge(from, to, weight);
        return builder.Build();
    }

    private static IEnumerable<(string, string, double)> Clique(string prefix, int size)
    {
        for (var i = 0; i < size; i++)
        {
            for (var j = i + 1; j < size; j++)
                yield return ($"{prefix}{i:00}", $"{prefix}{j:00}", 1);
        }
    }

    private static IEnumerable<string> Keys(string prefix, int size) => Enumerable.Range(0, size).Select(i => $"{prefix}{i:00}");

    private static string[][] Groups(TypeGraph graph, int[] labels) =>
        [.. labels.Select((label, index) => (label, key: graph.Nodes[index].Key)).GroupBy(x => x.label).OrderBy(g => g.Key)
            .Select(g => g.Select(x => x.key).ToArray())];

    [Fact]
    public void Two_cliques_joined_by_a_bridge_stay_separate()
    {
        var edges = Clique("a", 6).Concat(Clique("b", 6)).Append(("a00", "b00", 1.0));
        var graph = Graph(Keys("a", 6).Concat(Keys("b", 6)), edges);

        var labels = CommunityDetector.Detect(graph);

        Assert.Equal([Keys("a", 6).ToArray(), Keys("b", 6).ToArray()], Groups(graph, labels));
    }

    [Fact]
    public void The_result_is_identical_between_runs_and_whatever_the_insertion_order()
    {
        var edges = Clique("a", 5).Concat(Clique("b", 5)).Concat(Clique("c", 5))
            .Concat([("a00", "b00", 1.0), ("b01", "c01", 1.0), ("c02", "a02", 1.0)]).ToList();
        var keys = Keys("a", 5).Concat(Keys("b", 5)).Concat(Keys("c", 5)).ToList();

        var first = Graph(keys, edges);
        var second = Graph(Enumerable.Reverse(keys), Enumerable.Reverse(edges).Select(e => (e.Item2, e.Item1, e.Item3)));

        var run1 = CommunityDetector.Detect(first);
        var run2 = CommunityDetector.Detect(first);
        var permuted = CommunityDetector.Detect(second);

        Assert.Equal(run1, run2);
        Assert.Equal(first.Nodes.Select(n => n.Key), second.Nodes.Select(n => n.Key));
        Assert.Equal(run1, permuted);
        Assert.Equal(3, run1.Distinct().Count());
    }

    [Fact]
    public void Labels_are_consecutive_and_numbered_by_the_lowest_node_of_each_community()
    {
        var graph = Graph(Keys("a", 4).Concat(Keys("b", 4)).Concat(["lone"]), Clique("a", 4).Concat(Clique("b", 4)));

        var labels = CommunityDetector.Detect(graph);

        // Orden de claves: a00..a03, b00..b03, lone.
        Assert.Equal([0, 0, 0, 0, 1, 1, 1, 1, 2], labels);
    }

    [Fact]
    public void Nodes_without_dependencies_stay_alone_and_an_empty_graph_has_no_communities()
    {
        var graph = Graph(["x", "y", "z"], []);

        Assert.Equal([0, 1, 2], CommunityDetector.Detect(graph));
        Assert.Empty(CommunityDetector.Detect(TypeGraph.Empty));
    }

    [Fact]
    public void A_node_everybody_uses_does_not_glue_the_groups_it_serves_into_one()
    {
        // Tres grupos cohesionados que usan una misma utilidad con un peso pequeño por usuario.
        var edges = Clique("a", 6).Concat(Clique("b", 6)).Concat(Clique("c", 6)).ToList();
        foreach (var prefix in new[] { "a", "b", "c" })
        {
            foreach (var key in Keys(prefix, 6))
                edges.Add((key, "util", 0.2));
        }
        var graph = Graph(Keys("a", 6).Concat(Keys("b", 6)).Concat(Keys("c", 6)).Append("util"), edges);

        var labels = CommunityDetector.Detect(graph);

        var groups = Groups(graph, labels);
        Assert.Equal(3, groups.Count(g => g.Length >= 6));
        foreach (var prefix in new[] { "a", "b", "c" })
            Assert.Single(groups, g => g.Count(k => k.StartsWith(prefix, StringComparison.Ordinal)) == 6);
    }

    [Fact]
    public void About_eight_thousand_synthetic_types_are_clustered_in_seconds_and_recover_the_planted_groups()
    {
        const int groups = 80, size = 100;
        var random = new Random(20261004);
        var builder = new TypeGraph.Builder();
        for (var g = 0; g < groups; g++)
        {
            for (var i = 0; i < size; i++)
                builder.AddNode($"g{g:00}.t{i:000}", $"T{i}", "Class", $"g{g:00}/T{i}.java");
        }
        for (var g = 0; g < groups; g++)
        {
            for (var i = 0; i < size; i++)
            {
                // 10 dependencias dentro del grupo y una hacia otro.
                for (var k = 0; k < 10; k++)
                    builder.AddEdge($"g{g:00}.t{i:000}", $"g{g:00}.t{random.Next(size):000}", 1 + random.Next(3));
                builder.AddEdge($"g{g:00}.t{i:000}", $"g{random.Next(groups):00}.t{random.Next(size):000}", 1);
            }
        }
        var graph = builder.Build();

        var watch = Stopwatch.StartNew();
        var labels = CommunityDetector.Detect(graph);
        watch.Stop();
        output.WriteLine($"{graph.Nodes.Count} nodos, {graph.Edges.Count} aristas, {labels.Max() + 1} comunidades en {watch.ElapsedMilliseconds} ms");

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"tardó {watch.Elapsed}");
        // Pureza: cada comunidad detectada pertenece casi toda a un grupo plantado.
        var pure = labels.Select((label, index) => (label, group: graph.Nodes[index].Key[..3])).GroupBy(x => x.label)
            .Sum(g => g.GroupBy(x => x.group).Max(x => x.Count()));
        Assert.True(pure >= 0.95 * labels.Length, $"pureza {pure}/{labels.Length}");
    }
}
