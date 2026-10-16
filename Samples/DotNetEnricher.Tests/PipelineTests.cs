using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Enrichment;
using DotNetEnricher.Enrichers;

namespace DotNetEnricher.Tests;

/// <summary>La pasada como unidad: transacción, registro en <c>enrichment_runs</c>, limpieza al repetir y códigos de salida.</summary>
public sealed class PipelineTests
{
    private const string Source = """
        using Microsoft.Extensions.DependencyInjection;
        namespace App;
        public interface IClock { int Now(); }
        public sealed class Clock : IClock { public int Now() => 1; }
        public static class Registration
        {
            public static void Add(IServiceCollection services) => services.AddSingleton<IClock, Clock>();
        }
        """;

    private static async Task<TestGraph> SmallAsync()
    {
        var graph = new TestGraph().AddProject("App", "src/App").AddFile("src/App/Clock.cs", Source);
        return await graph.BuildAsync();
    }

    [Fact]
    public async Task A_successful_pass_is_recorded_in_enrichment_runs_with_the_index_fingerprint()
    {
        using var graph = await SmallAsync();
        var result = await graph.EnrichAsync();

        using var conn = graph.Open();
        var run = Assert.Single(await EnrichmentRuns.ListAsync(conn, CancellationToken.None, DotNetEnricherIdentity.EnricherName));
        Assert.Equal(result.RunId, run.Id);
        Assert.Equal(EnrichmentRuns.StatusOk, run.Status);
        Assert.Equal(DotNetEnricherIdentity.EnricherVersion, run.EnricherVersion);
        Assert.Equal($"{TestGraph.DefaultProject}|2026-09-30T10:00:00Z", run.IndexFingerprint);
        Assert.Equal(EnrichmentPipeline.ConfigFingerprint("sample.slnx", TestGraph.DefaultProject), run.ConfigFingerprint);
        Assert.Equal(1, run.Outcome!.FilesAnalyzed);
        Assert.Equal(0, run.Outcome.FilesUnmatched);
        Assert.True(run.Outcome.NodesCreated >= 2); // DiRegistration y CsProject
        Assert.True(await EnrichmentRuns.HasSuccessfulRunAsync(conn, DotNetEnricherIdentity.EnricherName, DotNetEnricherIdentity.EnricherVersion,
            run.IndexFingerprint, run.ConfigFingerprint, CancellationToken.None));
    }

    [Fact]
    public async Task Build_output_that_cbm_indexed_is_removed_before_the_enrichers_run()
    {
        using var graph = await SmallAsync();
        await graph.AddNodeAsync("Class", "App.xml", "demo.bin.App.xml", "src/App/bin/Debug/net10.0/App.xml", new JsonObject());
        await graph.AddNodeAsync("Variable", "version", "demo.obj.project.assets.version", "src/App/obj/project.assets.json", new JsonObject());

        var result = await graph.EnrichAsync();

        Assert.Equal(0, graph.Scalar<long>("SELECT COUNT(1) FROM nodes WHERE file_path LIKE '%/bin/%' OR file_path LIKE '%/obj/%'"));
        Assert.True(result.Outcome.NodesRemoved >= 2);
    }

    [Fact]
    public async Task The_project_is_the_majority_one_in_nodes_even_with_dots_in_its_name()
    {
        using var graph = await SmallAsync();
        var result = await graph.EnrichAsync();

        Assert.Equal(TestGraph.DefaultProject, result.Project);
        Assert.Equal(1, graph.Scalar<long>("SELECT COUNT(DISTINCT project) FROM nodes"));
        Assert.Equal(1, graph.Scalar<long>("SELECT COUNT(1) FROM nodes WHERE label = 'CsProject'"));
        Assert.NotNull(graph.PropsOrNull("_proj_.App"));
    }

    [Fact]
    public async Task Repeating_the_pass_replaces_its_previous_output_instead_of_duplicating_it()
    {
        using var graph = await SmallAsync();
        await graph.EnrichAsync();
        var nodes = graph.Scalar<long>("SELECT COUNT(1) FROM nodes");
        var edges = graph.Scalar<long>("SELECT COUNT(1) FROM edges");

        var second = await graph.EnrichAsync();

        Assert.Equal(nodes, graph.Scalar<long>("SELECT COUNT(1) FROM nodes"));
        Assert.Equal(edges, graph.Scalar<long>("SELECT COUNT(1) FROM edges"));
        Assert.True(second.Outcome.NodesRemoved >= 2);
        // Los nodos de cbm siguen ahí y no se tratan como propios aunque tengan marca de un enriquecedor.
        Assert.Equal("Singleton", (string?)graph.Props("Clock.Clock")["di_lifetime"]);
    }

    [Fact]
    public async Task A_failure_midway_rolls_back_everything_and_records_nothing()
    {
        using var graph = await SmallAsync();
        var before = graph.Scalar<string>("SELECT group_concat(properties, '|') FROM (SELECT properties FROM nodes ORDER BY id)");

        IReadOnlyList<IGraphEnricher> enrichers = [new SemanticMethodEnricher(), new DiEnricher(), new FailingEnricher()];
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EnrichmentPipeline.RunAsync(graph.Request(), enrichers, TextWriter.Null, null, CancellationToken.None));

        Assert.Equal(before, graph.Scalar<string>("SELECT group_concat(properties, '|') FROM (SELECT properties FROM nodes ORDER BY id)"));
        Assert.Equal(0, graph.Scalar<long>("SELECT COUNT(1) FROM nodes WHERE label = 'DiRegistration'"));
        using var conn = graph.Open();
        Assert.Empty(await EnrichmentRuns.ListAsync(conn, CancellationToken.None));
    }

    [Fact]
    public async Task An_empty_graph_is_an_input_error()
    {
        using var graph = new TestGraph();
        await graph.BuildAsync();

        await Assert.ThrowsAsync<EnrichmentInputException>(() => graph.EnrichAsync());
    }

    [Fact]
    public void Patches_join_enrichment_marks_and_never_change_the_origin()
    {
        var merged = JsonNode.Parse(GraphRepository.MergeJson(
            """{"origin":"cbm-like","enriched_by":["JavaAnalyzer"],"x":1}""",
            new { origin = "enricher", enriched_by = new[] { "DiEnricher" }, x = 2, y = "new" }))!.AsObject();

        Assert.Equal("cbm-like", (string?)merged["origin"]);
        Assert.Equal(["JavaAnalyzer", "DiEnricher"], merged["enriched_by"]!.AsArray().Select(v => (string)v!));
        Assert.Equal(2, (int)merged["x"]!);
        Assert.Equal("new", (string?)merged["y"]);
    }

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Temp\repo", @"C:\Users\x\AppData\Local\Temp\repo\src\A.cs", false)]
    [InlineData(@"C:\Users\x\AppData\Local\Temp\repo", @"C:\Users\x\AppData\Local\Temp\repo\src\obj\Debug\A.cs", true)]
    [InlineData(@"C:\Users\x\AppData\Local\Temp\repo", @"C:\Users\x\AppData\Local\Temp\repo\src\A.Designer.cs", true)]
    [InlineData(@"C:\Users\x\AppData\Local\Temp\repo", @"C:\Users\x\AppData\Local\Temp\other\A.cs", true)]
    public void The_exclusion_filter_looks_at_the_path_inside_the_repository(string repoRoot, string file, bool excluded)
    {
        // Con la ruta absoluta, un repositorio bajo Temp (o bin, tmp…) quedaba excluido entero.
        Assert.Equal(excluded, new CbmExclusionFilter(repoRoot).ShouldExclude(file));
    }

    [Fact]
    public async Task The_cli_fails_with_exit_code_1_when_the_solution_or_the_database_is_missing()
    {
        using var graph = await SmallAsync();
        var missingSolution = new EnrichOptions
        {
            Solution = new FileInfo(Path.Combine(graph.RepoRoot, "missing.sln")),
            Database = new FileInfo(graph.DatabasePath)
        };
        var error = new StringWriter();
        Assert.Equal(ExitCodes.InvalidInput, await EnrichCommand.RunAsync(missingSolution, TextWriter.Null, error, CancellationToken.None));
        Assert.Contains("no existe la solución", error.ToString(), StringComparison.Ordinal);

        var solution = Path.Combine(graph.RepoRoot, "app.slnx");
        await File.WriteAllTextAsync(solution, "<Solution />");
        var missingDb = missingSolution with { Solution = new FileInfo(solution), Database = new FileInfo(Path.Combine(graph.Root, "missing.db")) };
        Assert.Equal(ExitCodes.InvalidInput, await EnrichCommand.RunAsync(missingDb, TextWriter.Null, TextWriter.Null, CancellationToken.None));

        var outside = missingSolution with { Solution = new FileInfo(solution), RepoRoot = new DirectoryInfo(Path.Combine(graph.Root, "elsewhere")) };
        Directory.CreateDirectory(outside.RepoRoot!.FullName);
        Assert.Equal(ExitCodes.InvalidInput, await EnrichCommand.RunAsync(outside, TextWriter.Null, TextWriter.Null, CancellationToken.None));
    }

    [Fact]
    public void Without_repo_root_the_cli_takes_the_folder_with_git_above_the_solution()
    {
        using var graph = new TestGraph();
        var src = Directory.CreateDirectory(Path.Combine(graph.RepoRoot, "src"));
        Assert.Equal(src.FullName, EnrichCommand.FindRepositoryRoot(src));
        Directory.CreateDirectory(Path.Combine(graph.RepoRoot, ".git"));
        Assert.Equal(graph.RepoRoot, EnrichCommand.FindRepositoryRoot(src));
    }

    [Theory]
    [InlineData("Orders.Tests", false, false, "test")]
    [InlineData("Orders.Api", true, false, "host")]
    [InlineData("Orders.Integration", true, true, "test")]
    [InlineData("Orders.Adapters.Sql", false, false, "adapter")]
    [InlineData("Orders.Web", false, false, "frontend")]
    [InlineData("Orders.Core", false, false, "core")]
    public void Project_roles_come_from_the_solution_first(string name, bool exe, bool test, string role) =>
        Assert.Equal(role, ProjectEnricher.InferProjectRole(new CsProjectInfo(name, "", exe, test)));

    private sealed class FailingEnricher : IGraphEnricher
    {
        public string Name => "FailingEnricher";
        public int Order => 99;
        public Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null) =>
            throw new InvalidOperationException("fallo provocado");
    }
}
