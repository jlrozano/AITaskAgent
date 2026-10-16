using System.Text;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.SharedPass;
using CodebaseAnalysis.Core.Tests.Enrichment;
using Microsoft.Extensions.Configuration;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// D1 de la pasada compartida (<see cref="BatchPlanner"/>, <see cref="PacketBuilder"/>, <see cref="SharedPassOptions"/>) sobre un
/// grafo de cbm sintético (<see cref="CbmTestGraph"/>) y un repositorio temporal.
/// </summary>
public sealed class SharedPassPacketTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private string Repo => Path.Combine(_temp.Path, "repo");
    private string Db => Path.Combine(_temp.Path, "codebase.db");

    public void Dispose() => _temp.Dispose();

    private void WriteFile(string relative, string text, Encoding? encoding = null)
    {
        var path = Path.Combine(Repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, (encoding ?? new UTF8Encoding(false)).GetBytes(text));
    }

    private static Subsystem Sub(string id, string kind, params string[] files) =>
        new() { Id = id, Name = id.ToUpperInvariant(), Description = $"desc {id}", Kind = kind, Files = files };

    private static DomainMap Map(params Subsystem[] subsystems) => new() { Subsystems = subsystems };

    private const string AlphaProps = """
        {"signature":"(\r\n   string path)","cyclomatic":3,"fan_in":2,"fan_out":1,"accessibility":"public",
         "sensitive_apis":[{"category":"file-io","api":"System.IO.File.ReadAllText","line":4}],"sensitive_categories":["file-io"],
         "throws":["System.InvalidOperationException"],"swallows_exceptions":true,"swallowed_catches":[{"line":7,"type":"System.Exception","kind":"log-only"}],
         "concurrency_markers":[{"kind":"lock","line":5}],"detected_issues":["empty-catch"],
         "body_hash":"0123456789abcdef0123456789abcdef","contract_hash":"fedcba9876543210fedcba9876543210"}
        """;

    /// <summary>Tres subsistemas: <c>alpha</c> (2 ficheros), <c>beta</c> (usa alpha) y <c>shared</c> (lo usa alpha).</summary>
    private async Task<DomainMap> ThreeSubsystemsAsync()
    {
        using var graph = await CbmTestGraph.CreateAsync(Db, "demo");
        var alpha = await graph.NodeAsync("Class", "Alpha", "demo.Alpha", "src/a/Alpha.cs", 1, 10,
            """{"type_role":"service","afferent_coupling":1,"efferent_coupling":1,"type_hash":"aaaabbbbccccddddeeee"}""");
        var alphaRun = await graph.NodeAsync("Method", "Run", "demo.Alpha.Run", "src/a/Alpha.cs", 3, 8, AlphaProps);
        await graph.EdgeAsync(alpha, alphaRun, "DEFINES_METHOD");
        var helper = await graph.NodeAsync("Class", "AlphaHelper", "demo.AlphaHelper", "src/a/AlphaHelper.cs", 1, 3);
        var beta = await graph.NodeAsync("Class", "Beta", "demo.Beta", "src/b/Beta.cs", 1, 6);
        var betaRun = await graph.NodeAsync("Method", "Go", "demo.Beta.Go", "src/b/Beta.cs", 2, 5, """{"cyclomatic":1}""");
        await graph.EdgeAsync(beta, betaRun, "DEFINES_METHOD");
        var util = await graph.NodeAsync("Class", "Util", "demo.Util", "src/s/Util.cs", 1, 4);
        var utilDo = await graph.NodeAsync("Method", "Do", "demo.Util.Do", "src/s/Util.cs", 2, 3);
        await graph.EdgeAsync(util, utilDo, "DEFINES_METHOD");
        await graph.EdgeAsync(betaRun, alphaRun, "CALLS", """{"exact":true,"origin":"compiler"}""");
        await graph.EdgeAsync(alphaRun, utilDo, "CALLS", """{"strategy":"unique_name"}""");
        await graph.EdgeAsync(alpha, helper, "USAGE");

        WriteFile("src/a/Alpha.cs", "namespace Demo;\r\npublic class Alpha\r\n{\r\n    public void Run() { }\r\n}\r\n");
        WriteFile("src/a/AlphaHelper.cs", "class AlphaHelper { }\n");
        WriteFile("src/b/Beta.cs", "class Beta\n{\n    void Go() => new Alpha().Run();\n}\n");
        WriteFile("src/s/Util.cs", "static class Util { public static void Do() { } }\n");

        return Map(Sub("beta", SubsystemKinds.Capability, "src/b/Beta.cs"),
            Sub("alpha", SubsystemKinds.Capability, "src/a/AlphaHelper.cs", "src/a/Alpha.cs"),
            Sub("shared", SubsystemKinds.Shared, "src/s/Util.cs"));
    }

    private Task<SharedPassPacketSet> BuildAsync(DomainMap map, SharedPassOptions? options = null) =>
        PacketBuilder.BuildAllAsync(Db, Repo, map, options ?? new SharedPassOptions(), CancellationToken.None);

    [Fact]
    public async Task One_batch_per_subsystem_ordered_by_level_with_shared_first()
    {
        var set = await BuildAsync(await ThreeSubsystemsAsync());

        Assert.Equal(["shared", "alpha", "beta"], set.Plan.Batches.Select(b => b.Id));
        Assert.Equal([0, 1, 2], set.Plan.Batches.Select(b => b.Level));
        Assert.Equal([0, 1, 2], set.Plan.Batches.Select(b => b.Order));
        Assert.All(set.Plan.Batches, b => Assert.Equal(BatchSplitMethods.None, b.Split));
        Assert.Equal(["src/a/Alpha.cs", "src/a/AlphaHelper.cs"], set.Plan.Batches[1].Files);
        Assert.Equal(set.Plan.Batches.Select(b => b.Id), set.Packets.Select(p => p.BatchId));
        Assert.Empty(set.Warnings);
    }

    [Fact]
    public async Task Packet_carries_graph_facts_including_the_new_enricher_properties_and_numbered_code()
    {
        var set = await BuildAsync(await ThreeSubsystemsAsync());
        var alpha = set.Packets.Single(p => p.BatchId == "alpha").Markdown;

        Assert.StartsWith("# Lote: ALPHA (id `alpha`)", alpha, StringComparison.Ordinal);
        Assert.Contains("Descripción del mapa de dominios: desc alpha", alpha, StringComparison.Ordinal);
        Assert.Contains("- T Class Alpha | L1-10 | role=service | Ca=1 | Ce=1 | type#aaaabbbbcccc", alpha, StringComparison.Ordinal);
        Assert.Contains(
            "- M Alpha.Run(string path) | L3-8 | cc=3 | fan_in=2 | fan_out=1 | public | ISSUE[empty-catch] | throws[System.InvalidOperationException]"
            + " | swallows[log-only@L7] | sensitive[file-io:System.IO.File.ReadAllText@L4] | concurrency[lock@L5] | body#0123456789ab | contract#fedcba987654",
            alpha, StringComparison.Ordinal);
        Assert.Contains("### FILE src/a/Alpha.cs  (5 líneas)\n```\n1: namespace Demo;\n2: public class Alpha\n", alpha, StringComparison.Ordinal);
        Assert.Contains("5: }\n```", alpha, StringComparison.Ordinal);
        Assert.True(set.Packets.Single(p => p.BatchId == "alpha").EstimatedTokens >= alpha.Length / 4);
    }

    [Fact]
    public async Task Relations_list_incoming_and_outgoing_edges_with_the_batch_of_the_other_end()
    {
        var set = await BuildAsync(await ThreeSubsystemsAsync());
        string Packet(string id) => set.Packets.Single(p => p.BatchId == id).Markdown;

        Assert.Contains("Entrantes (1; quién de fuera usa este lote):\n\n- [beta] Beta.Go -CALLS(exacta)-> Alpha.Run", Packet("alpha"), StringComparison.Ordinal);
        Assert.Contains("Salientes (1; qué de fuera usa este lote):\n\n- Alpha.Run -CALLS-> [shared] Util.Do", Packet("alpha"), StringComparison.Ordinal);
        Assert.Contains("- Beta.Go -CALLS(exacta)-> [alpha] Alpha.Run", Packet("beta"), StringComparison.Ordinal);
        Assert.Contains("- [alpha] Alpha.Run -CALLS-> Util.Do", Packet("shared"), StringComparison.Ordinal);
        // La USAGE de Alpha a AlphaHelper es interna al lote: no es una relación.
        Assert.DoesNotContain("AlphaHelper -", Packet("alpha"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreadable_file_is_marked_and_warned_and_the_packet_is_still_built()
    {
        var map = await ThreeSubsystemsAsync();
        map = map with { Subsystems = [.. map.Subsystems.Select(s => s.Id == "beta" ? s with { Files = [.. s.Files, "src/b/Missing.cs"] } : s)] };

        var set = await BuildAsync(map);

        var beta = set.Packets.Single(p => p.BatchId == "beta");
        Assert.Contains("### FILE src/b/Missing.cs\n(no legible: no existe)", beta.Markdown, StringComparison.Ordinal);
        Assert.Contains("### FILE src/b/Beta.cs  (4 líneas)", beta.Markdown, StringComparison.Ordinal);
        Assert.Contains(beta.Warnings, w => w.Contains("src/b/Missing.cs", StringComparison.Ordinal));
        Assert.Contains(set.Warnings, w => w.Contains("src/b/Missing.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Non_utf8_file_is_decoded_with_the_fallback_encoding_and_long_files_are_capped()
    {
        await ThreeSubsystemsAsync();
        WriteFile("src/a/AlphaHelper.cs", "// año\nclass AlphaHelper { }\n", Encoding.Latin1);
        WriteFile("src/a/Alpha.cs", string.Join("\n", Enumerable.Range(1, 50).Select(i => $"// línea {i:00}")) + "\n");
        var map = Map(Sub("alpha", SubsystemKinds.Capability, "src/a/Alpha.cs", "src/a/AlphaHelper.cs"));

        var set = await BuildAsync(map, new SharedPassOptions { MaxFileChars = 120 });

        var packet = set.Packets.Single();
        Assert.Contains("1: // año", packet.Markdown, StringComparison.Ordinal);
        Assert.Contains("### FILE src/a/Alpha.cs  (50 líneas; se muestran las 10 primeras por MaxFileChars)", packet.Markdown, StringComparison.Ordinal);
        // 12 caracteres por línea con su salto: caben 10 en 120.
        Assert.Contains("10: // línea 10\n```\n(… faltan las líneas 11-50)", packet.Markdown, StringComparison.Ordinal);
        Assert.Contains(packet.Warnings, w => w.Contains("MaxFileChars", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Old_graph_without_new_properties_or_with_invalid_json_does_not_fail()
    {
        using (var graph = await CbmTestGraph.CreateAsync(Db, "demo"))
        {
            var type = await graph.NodeAsync("Class", "Old", "demo.Old", "src/Old.cs", 1, 3, "{not json");
            var method = await graph.NodeAsync("Method", "M", "demo.Old.M", "src/Old.cs", 2, 2, """{"complexity":1}""");
            await graph.EdgeAsync(type, method, "DEFINES_METHOD");
        }
        WriteFile("src/Old.cs", "class Old\n{ void M() { } }\n");

        var set = await BuildAsync(Map(Sub("old", SubsystemKinds.Capability, "src/Old.cs")));

        var packet = set.Packets.Single().Markdown;
        Assert.Contains("- T Class Old | L1-3\n- M Old.M | L2-2 | cc=1", packet, StringComparison.Ordinal);
        Assert.DoesNotContain("body#", packet, StringComparison.Ordinal);
        Assert.Contains("Entrantes (0;", packet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_database_gives_packets_with_code_only_and_a_warning()
    {
        WriteFile("src/A.cs", "class A { }\n");

        var set = await BuildAsync(Map(Sub("a", SubsystemKinds.Capability, "src/A.cs")));

        Assert.Contains("1: class A { }", set.Packets.Single().Markdown, StringComparison.Ordinal);
        Assert.Contains(set.Warnings, w => w.Contains("No existe la base de datos", StringComparison.Ordinal));
    }

    /// <summary>Un subsistema de seis ficheros en una carpeta: dos triángulos de tipos (K y L) unidos por una sola llamada.</summary>
    private async Task<DomainMap> TwoCommunitiesAsync()
    {
        using var graph = await CbmTestGraph.CreateAsync(Db, "demo");
        var ids = new Dictionary<string, long>();
        foreach (var name in new[] { "K1", "K2", "K3", "L1", "L2", "L3" })
        {
            ids[name] = await graph.NodeAsync("Class", name, $"demo.{name}", $"src/big/{name}.cs", 1, 20);
            WriteFile($"src/big/{name}.cs", string.Join("\n", Enumerable.Range(1, 20).Select(i => $"// {name} línea {i}")) + "\n");
        }
        foreach (var (a, b) in new[] { ("K1", "K2"), ("K2", "K3"), ("K3", "K1"), ("L1", "L2"), ("L2", "L3"), ("L3", "L1") })
        {
            await graph.EdgeAsync(ids[a], ids[b], "USAGE");
            await graph.EdgeAsync(ids[b], ids[a], "CALLS");
        }
        await graph.EdgeAsync(ids["K1"], ids["L1"], "USAGE");
        return Map(Sub("big", SubsystemKinds.Capability, [.. ids.Keys.Select(k => $"src/big/{k}.cs")]));
    }

    private int FileTokens(string file) =>
        new PacketBuilder(SharedPassGraph.Empty, Repo, new SharedPassOptions()).EstimateFileTokens(file);

    /// <summary>La estimación del paquete entero de <paramref name="files"/> como parte del único subsistema de <paramref name="map"/>.</summary>
    private async Task<int> PacketTokensAsync(DomainMap map, params string[] files)
    {
        var builder = new PacketBuilder(await SharedPassGraph.LoadAsync(Db, CancellationToken.None), Repo, new SharedPassOptions());
        return files.Sum(builder.EstimateFileTokens) + builder.OverheadEstimator(map)(map.Subsystems[0], files);
    }

    [Fact]
    public async Task Subsystem_over_budget_is_split_by_graph_communities()
    {
        var map = await TwoCommunitiesAsync();
        // El presupuesto mide el paquete entero (cabecera y relaciones incluidas): cabe un triángulo, no los dos.
        var half = await PacketTokensAsync(map, "src/big/K1.cs", "src/big/K2.cs", "src/big/K3.cs");
        var options = new SharedPassOptions { MaxBatchTokens = half + 5 };

        var set = await BuildAsync(map, options);

        Assert.Equal(["big-p1", "big-p2"], set.Plan.Batches.Select(b => b.Id));
        Assert.Equal(["src/big/K1.cs", "src/big/K2.cs", "src/big/K3.cs"], set.Plan.Batches[0].Files);
        Assert.Equal(["src/big/L1.cs", "src/big/L2.cs", "src/big/L3.cs"], set.Plan.Batches[1].Files);
        Assert.All(set.Plan.Batches, b =>
        {
            Assert.Equal(BatchSplitMethods.Communities, b.Split);
            Assert.Equal(2, b.PartCount);
            Assert.True(b.EstimatedTokens <= options.MaxBatchTokens);
        });
        Assert.All(set.Packets, p => Assert.True(p.EstimatedTokens <= options.MaxBatchTokens, $"{p.BatchId}: {p.EstimatedTokens}"));
        Assert.StartsWith("# Lote: BIG (parte 1 de 2) (id `big-p1`)", set.Packets[0].Markdown, StringComparison.Ordinal);
        // La llamada entre los dos triángulos es ahora una relación entre las dos partes.
        Assert.Contains("- [big-p1] K1 -USAGE-> L1", set.Packets[1].Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_types_the_split_falls_back_to_folders_in_file_order()
    {
        foreach (var file in new[] { "src/x/A.cs", "src/x/B.cs", "src/y/C.cs" })
            WriteFile(file, string.Join("\n", Enumerable.Range(1, 30).Select(i => $"// {file} {i}")) + "\n");
        var map = Map(Sub("loose", SubsystemKinds.Capability, "src/y/C.cs", "src/x/B.cs", "src/x/A.cs"));
        var folderX = await PacketTokensAsync(map, "src/x/A.cs", "src/x/B.cs");

        var set = await BuildAsync(map, new SharedPassOptions { MaxBatchTokens = folderX + 5 });

        Assert.Equal([["src/x/A.cs", "src/x/B.cs"], ["src/y/C.cs"]], set.Plan.Batches.Select(b => b.Files.ToArray()));
        Assert.All(set.Plan.Batches, b => Assert.Equal(BatchSplitMethods.Folders, b.Split));
    }

    [Fact]
    public async Task A_single_file_over_budget_gets_its_own_batch_with_a_warning()
    {
        WriteFile("src/x/Huge.cs", new string('x', 4000) + "\n");
        WriteFile("src/x/Small.cs", "class S { }\n");

        var set = await BuildAsync(Map(Sub("h", SubsystemKinds.Capability, "src/x/Huge.cs", "src/x/Small.cs")), new SharedPassOptions { MaxBatchTokens = 200 });

        Assert.Equal([["src/x/Huge.cs"], ["src/x/Small.cs"]], set.Plan.Batches.Select(b => b.Files.ToArray()));
        Assert.Contains(set.Plan.Warnings, w => w.Contains("src/x/Huge.cs", StringComparison.Ordinal) && w.Contains("MaxBatchTokens", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plan_order_does_not_depend_on_the_order_of_the_map()
    {
        var map = await TwoCommunitiesAsync();
        var options = new SharedPassOptions { MaxBatchTokens = new[] { "K1", "K2", "K3" }.Sum(k => FileTokens($"src/big/{k}.cs")) + 60 };
        var reversed = map with { Subsystems = [.. map.Subsystems.Select(s => s with { Files = [.. s.Files.Reverse()] })] };
        var typeGraph = await TypeGraphBuilder.ReadAsync(Db, _ => true, CancellationToken.None);

        var first = BatchPlanner.Plan(map, typeGraph, FileTokens, options);
        var second = BatchPlanner.Plan(reversed, typeGraph, FileTokens, options);

        Assert.Equal(first.Batches.Select(b => (b.Id, string.Join(",", b.Files))), second.Batches.Select(b => (b.Id, string.Join(",", b.Files))));
    }

    [Fact]
    public async Task Two_runs_produce_byte_identical_packets()
    {
        var map = await ThreeSubsystemsAsync();

        var first = await BuildAsync(map);
        var second = await BuildAsync(map);

        Assert.Equal(first.Packets.Count, second.Packets.Count);
        for (var i = 0; i < first.Packets.Count; i++)
            Assert.Equal(Encoding.UTF8.GetBytes(first.Packets[i].Markdown), Encoding.UTF8.GetBytes(second.Packets[i].Markdown));
    }

    [Fact]
    public void Options_have_safe_defaults_and_typos_fall_back_to_them()
    {
        var defaults = SharedPassOptions.FromConfiguration(new ConfigurationBuilder().Build());
        Assert.Equal(new SharedPassOptions(), defaults);
        Assert.False(defaults.Enabled);
        Assert.Equal(50_000, defaults.MaxBatchTokens);
        Assert.Equal(2, defaults.MaxReaderRetries);

        var configured = SharedPassOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CodebaseAnalysis:SharedPass:Enabled"] = "true",
            ["CodebaseAnalysis:SharedPass:MaxBatchTokens"] = "30000",
            ["CodebaseAnalysis:SharedPass:ReaderRole"] = "Reader",
            ["CodebaseAnalysis:SharedPass:MaxCostPerBatchUsd"] = "0.5",
            ["CodebaseAnalysis:SharedPass:MaxCostPerRunUsd"] = "mucho",
            ["CodebaseAnalysis:SharedPass:MaxReaderRetries"] = "dos"
        }).Build());
        Assert.True(configured.Enabled);
        Assert.Equal(30_000, configured.MaxBatchTokens);
        Assert.Equal("Reader", configured.ReaderRole);
        Assert.Equal(0.5, configured.MaxCostPerBatchUsd);
        Assert.Equal(defaults.MaxCostPerRunUsd, configured.MaxCostPerRunUsd);
        Assert.Equal(defaults.MaxReaderRetries, configured.MaxReaderRetries);
    }

    [Fact]
    public void Shipped_configuration_keeps_the_shared_pass_disabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.analysis.json"))
            .Build();

        var options = SharedPassOptions.FromConfiguration(configuration);

        Assert.False(options.Enabled);
        Assert.Equal(new SharedPassOptions(), options);
    }
}
