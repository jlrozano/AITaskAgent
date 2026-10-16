using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Steps;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace CodebaseAnalysis.Core.Tests.Enrichment;

/// <summary>
/// Los artefactos de compilación y los ficheros generados (<c>bin/</c>, <c>obj/</c>, <c>*.g.cs</c>…) contaminan las
/// métricas, los ciclos y las comunidades: cbm los indexa (los .json y .xml de <c>bin/</c> acaban como nodos) y se
/// retiran del grafo por ruta, sin escribir nada en el repositorio analizado.
/// </summary>
public class GeneratedArtifactsTests
{
    [Theory]
    [InlineData("src/App/bin/Debug/net10.0/App.deps.json", true)]
    [InlineData("bin/Release/net10.0/App.xml", true)]
    [InlineData("App/obj/Debug/net10.0/App.AssemblyInfo.cs", true)]
    [InlineData("App/obj", true)]
    [InlineData("App\\obj\\Debug\\x.cs", true)]
    [InlineData("App/Views/Home.g.cs", true)]
    [InlineData("App/Form1.Designer.cs", true)]
    [InlineData("App/Form1.designer.cs", true)]
    [InlineData("App/Foo.g.i.cs", true)]
    [InlineData("App/Foo.generated.cs", true)]
    [InlineData("App/Release/net10.0/.NETCoreApp,Version=v10.0.AssemblyAttributes.cs", true)]
    [InlineData("App/App.AssemblyInfo.cs", true)]
    [InlineData("App/TemporaryGeneratedFile_036C0B5B-1481-4323-8D20-8F5ADCB23D92.cs", true)]
    // Lo que escribe una persona se queda: un AssemblyInfo.cs propio, y carpetas cuyo nombre solo contiene bin/obj.
    [InlineData("OpenAI/AssemblyInfo.cs", false)]
    [InlineData("src/Cabinet/Cabinet.cs", false)]
    [InlineData("src/combined/Job.cs", false)]
    [InlineData("src/Binder/Obj.cs", false)]
    [InlineData("src/Orders/Order.cs", false)]
    [InlineData("", false)]
    public void Recognises_build_output_and_generated_files_by_path(string path, bool generated) =>
        Assert.Equal(generated, GeneratedArtifacts.IsGenerated(path));

    [Fact]
    public async Task Removal_deletes_the_nodes_their_edges_and_vectors_and_leaves_the_rest()
    {
        using var temp = new TempDirectory();
        using var g = await CbmTestGraph.CreateAsync(Path.Combine(temp.Path, "g.db"), "demo");
        var real = await g.NodeAsync("Class", "Order", "demo.src.Order.Order", "src/Order.cs");
        var other = await g.NodeAsync("Class", "Repo", "demo.src.Repo.Repo", "src/Repo.cs");
        var junkJson = await g.NodeAsync("Class", "AITaskAgent.xml", "demo.bin.x", "App/bin/Debug/net10.0/AITaskAgent.xml");
        var junkFolder = await g.NodeAsync("Folder", "Debug", "demo.bin", "App/bin/Debug");
        var generated = await g.NodeAsync("Class", "Window", "demo.g.Window", "src/Window.g.cs");
        var projectNode = await g.NodeAsync("CsProject", "App", "_proj_.App", "");
        await g.EdgeAsync(real, other, "CALLS");
        await g.EdgeAsync(real, junkJson, "USAGE");
        await g.EdgeAsync(junkFolder, junkJson, "CONTAINS_FILE");
        await g.EdgeAsync(generated, real, "CALLS");
        await g.VectorAsync(junkJson);
        await g.VectorAsync(real);

        using var tx = g.Connection.BeginTransaction();
        var (nodes, edges) = await GeneratedArtifacts.RemoveFromGraphAsync(g.Connection, tx, CancellationToken.None);
        tx.Commit();

        Assert.Equal(3, nodes);
        Assert.Equal(3, edges);
        Assert.Equal(3, await g.CountAsync("SELECT COUNT(1) FROM nodes"));
        Assert.Equal(1, await g.CountAsync("SELECT COUNT(1) FROM edges"));
        Assert.Equal(1, await g.CountAsync("SELECT COUNT(1) FROM node_vectors"));
        Assert.Equal(1, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE id = @id", ("@id", projectNode)));

        // Idempotente: una segunda pasada no encuentra nada.
        using var again = g.Connection.BeginTransaction();
        Assert.Equal((0, 0), await GeneratedArtifacts.RemoveFromGraphAsync(g.Connection, again, CancellationToken.None));
    }
}

/// <summary>El paso de detección limpia el grafo aunque no reindexe: un grafo ya existente venía con los nodos de <c>bin/</c>.</summary>
[Collection(ConsoleCollection.Name)]
public sealed class DetectStepGeneratedArtifactsTests
{
    [Fact]
    public async Task An_existing_graph_loses_its_build_output_and_the_counts_do_not_include_it()
    {
        using var repo = new TempDirectory();
        var configuration = TestConfig.FromPairs();
        var input = AnalysisRunOptions.BuildInput(configuration, repo.Path, _ => { });
        Directory.CreateDirectory(Path.GetDirectoryName(input.DatabasePath)!);
        using (var g = await CbmTestGraph.CreateAsync(input.DatabasePath, "Codebase"))
        {
            var real = await g.NodeAsync("Class", "Order", "Codebase.src.Order.Order", "src/Order.cs");
            var junk = await g.NodeAsync("Class", "App.xml", "Codebase.bin.App.xml", "src/bin/Debug/App.xml");
            await g.EdgeAsync(real, junk, "USAGE");
        }
        SqliteConnection.ClearAllPools();
        var services = new ServiceCollection().AddSingleton(configuration).AddLogging().BuildServiceProvider();
        var reporter = new CapturingReporter();
        var context = AnalysisRunner.CreateContext(input, services, null, reporter);

        var status = await new DetectAndIndexGraphStep().ExecuteAsync(input, context, 1, null, CancellationToken.None);

        Assert.Equal(1, status.TotalNodes);
        Assert.Equal(0, status.TotalEdges);
        Assert.Contains(reporter.Entries, e => e.Message.Contains("1 nodos de salida de compilación", StringComparison.Ordinal));
    }
}
