using AITaskAgent.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tests.Enrichment;
using CodebaseAnalysis.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Las vistas de estructura de <c>graph_domain</c> (<c>projects</c> y <c>contracts</c>) y la lectura del lifetime de DI, sobre un
/// grafo sintético con la forma que deja el enriquecedor Roslyn: tres proyectos (Core, Ext, Api), un contrato con una
/// implementación en otro proyecto y consumidores que usan el contrato y una clase concreta.
/// </summary>
public sealed class GraphDomainStructureViewsTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private async Task<string> RunAsync(string args, string databasePath)
    {
        var context = new PipelineContext();
        context.Set(AnalysisContextKeys.CbmDbPath, databasePath);
        var result = await new GraphDomainTool().ExecuteAsync(args, context, "ChatAssistant", NullLogger.Instance);
        output.WriteLine($"--- graph_domain {args}\n{result}");
        return result;
    }

    private static string TypeProps(string role, bool isPublic, string project) =>
        $"{{\"type_role\":\"{role}\",\"is_public\":{(isPublic ? "true" : "false")},\"cs_project\":\"{project}\"}}";

    private async Task<CbmTestGraph> GraphAsync()
    {
        var g = await CbmTestGraph.CreateAsync(Path.Combine(_temp.Path, "codebase.db"), "demo");
        var core = await g.NodeAsync("CsProject", "Core", "_proj_.Core", "", 0, 0,
            "{\"role\":\"core\",\"directory\":\"src/Core\",\"afferent_coupling\":2,\"efferent_coupling\":0,\"instability\":0,\"abstractness\":0.5,\"distance\":0.5,"
            + "\"type_roles\":{\"contract\":1,\"implementation\":1},\"packages\":[\"Dapper\"]}");
        var ext = await g.NodeAsync("CsProject", "Ext", "_proj_.Ext", "", 0, 0,
            "{\"role\":\"adapter\",\"directory\":\"src/Ext\",\"afferent_coupling\":0,\"efferent_coupling\":1,\"instability\":1,\"abstractness\":0,\"distance\":0,"
            + "\"type_roles\":{\"implementation\":1},\"project_references\":[\"Core\",\"Api\"]}");
        var api = await g.NodeAsync("CsProject", "Api", "_proj_.Api", "", 0, 0,
            "{\"role\":\"host\",\"directory\":\"src/Api\",\"afferent_coupling\":0,\"efferent_coupling\":1,\"instability\":1}");

        var plugin = await g.NodeAsync("Interface", "IPlugin", "demo.src.Core.IPlugin.IPlugin", "src/Core/IPlugin.cs", 1, 5, TypeProps("contract", true, "Core"));
        var engine = await g.NodeAsync("Class", "Engine", "demo.src.Core.Engine.Engine", "src/Core/Engine.cs", 1, 20, TypeProps("implementation", true, "Core"));
        var pluginA = await g.NodeAsync("Class", "PluginA", "demo.src.Ext.PluginA.PluginA", "src/Ext/PluginA.cs", 1, 20, TypeProps("implementation", true, "Ext"));
        var run = await g.NodeAsync("Method", "Run", "demo.src.Ext.PluginA.PluginA.Run", "src/Ext/PluginA.cs", 5, 9);
        var start = await g.NodeAsync("Method", "Start", "demo.src.Core.Engine.Engine.Start", "src/Core/Engine.cs", 3, 8);
        var apiFile = await g.NodeAsync("Module", "src/Api/Program.cs", "demo.src.Api.Program.__file__", "src/Api/Program.cs");
        var registration = await g.NodeAsync("DiRegistration", "IPlugin->PluginA", "_di_.IPlugin->PluginA", "src/Api/Program.cs", 3, 3, "{\"lifetime\":\"Singleton\"}");

        foreach (var (member, project) in new[] { (plugin, core), (engine, core), (pluginA, ext) })
            await g.EdgeAsync(member, project, "BELONGS_TO_PROJECT");
        await g.EdgeAsync(pluginA, plugin, "IMPLEMENTS");
        await g.EdgeAsync(pluginA, plugin, "INHERITS");
        await g.EdgeAsync(engine, start, "DEFINES_METHOD");
        await g.EdgeAsync(pluginA, run, "DEFINES_METHOD");
        await g.EdgeAsync(run, plugin, "USAGE");
        await g.EdgeAsync(run, engine, "USAGE");
        await g.EdgeAsync(apiFile, engine, "USAGE");
        await g.EdgeAsync(run, start, "CALLS");
        await g.EdgeAsync(registration, pluginA, "DI_RESOLVES");
        await g.EdgeAsync(ext, core, "PROJECT_DEPENDS_ON", "{\"weight\":3,\"via\":{\"USAGE\":2,\"IMPLEMENTS\":1}}");
        await g.EdgeAsync(api, core, "PROJECT_DEPENDS_ON", "{\"weight\":1,\"via\":{\"USAGE\":1}}");
        return g;
    }

    [Fact]
    public async Task Di_reads_the_lifetime_of_the_registration_and_of_the_implementation()
    {
        using var g = await CbmTestGraph.CreateAsync(Path.Combine(_temp.Path, "codebase.db"), "demo");
        await g.NodeAsync("DiRegistration", "A", "_di_.A", "src/A.cs", 1, 1, "{\"lifetime\":\"Scoped\"}");
        await g.NodeAsync("DiRegistration", "B", "_di_.B", "src/B.cs", 1, 1, "{\"di_lifetime\":\"Singleton\"}");
        await g.NodeAsync("DiRegistration", "C", "_di_.C", "src/C.cs", 1, 1, "{}");

        var text = await RunAsync("""{"view":"di"}""", g.DatabasePath);

        Assert.Contains("| `A` | **Scoped** |", text);
        Assert.Contains("| `B` | **Singleton** |", text);
        Assert.Contains("| `C` | **desconocido** |", text);
    }

    [Fact]
    public async Task Projects_shows_coupling_role_abstraction_and_declared_references()
    {
        using var g = await GraphAsync();

        var text = await RunAsync("""{"view":"projects"}""", g.DatabasePath);

        Assert.Contains("# Proyectos C# (3)", text);
        Assert.Contains("| `Core` | core | 2 | 0 | 0 | 0.5 | 0.5 | 2 | 1 / 1 / 0 / 0 |", text);
        Assert.Contains("| `Ext` | adapter | 0 | 1 | 1 | 0 | 0 | 1 | 0 / 1 / 0 / 0 |", text);
        Assert.Contains("- `Core`: `Dapper`", text);
        // Ext declara ProjectReference a Core y la usa; Api usa Core sin declararla.
        Assert.Contains("| `Ext` | `Core` | 3 | IMPLEMENTS 1, USAGE 2 | declarada |", text);
        Assert.Contains("| `Api` | `Core` | 1 | USAGE 1 | no declarada |", text);
        // Ext declara Api y no hay dependencia observada.
        Assert.Contains("## ProjectReference declaradas sin dependencia observada", text);
        Assert.Contains("- `Ext` → `Api`", text);
    }

    [Fact]
    public async Task Projects_can_be_narrowed_to_one_and_report_an_unknown_name()
    {
        using var g = await GraphAsync();

        var one = await RunAsync("""{"view":"projects","name":"api"}""", g.DatabasePath);
        Assert.Contains("# Proyectos C# (1)", one);
        Assert.DoesNotContain("| `Ext` | adapter", one);
        Assert.Contains("| `Api` | `Core` |", one);

        var none = await RunAsync("""{"view":"projects","name":"Nada"}""", g.DatabasePath);
        Assert.Contains("No hay un proyecto llamado `Nada`", none);
        Assert.Contains("`Core`", none);
    }

    [Fact]
    public async Task Projects_without_csproject_nodes_says_so()
    {
        using var g = await CbmTestGraph.CreateAsync(Path.Combine(_temp.Path, "codebase.db"), "demo");

        Assert.Contains("No hay nodos CsProject", await RunAsync("""{"view":"projects"}""", g.DatabasePath));
    }

    [Fact]
    public async Task Contracts_overview_counts_roles_per_project_and_ranks_contracts_by_implementations()
    {
        using var g = await GraphAsync();

        var text = await RunAsync("""{"view":"contracts"}""", g.DatabasePath);

        Assert.Contains("| `Core` | 1 | 1 | 0 | 0 |", text);
        Assert.Contains("| `Ext` | 0 | 1 | 0 | 0 |", text);
        // Una sola implementación aunque tenga IMPLEMENTS e INHERITS al mismo contrato.
        Assert.Contains("| `IPlugin` | `Core` | 1 | 1 |", text);
    }

    [Fact]
    public async Task Contracts_of_an_interface_lists_implementations_with_lifetime_and_consumers()
    {
        using var g = await GraphAsync();

        var text = await RunAsync("""{"view":"contracts","name":"IPlugin"}""", g.DatabasePath);

        Assert.Contains("# `IPlugin`: contract (Interface, pública, proyecto `Core`)", text);
        Assert.Contains("## Implementaciones y derivadas (1)", text);
        Assert.Contains("| `PluginA` | implementation | pública | Ext | IMPLEMENTS + INHERITS | Singleton |", text);
        // Quien usa el contrato es el tipo que declara el método Run.
        Assert.Contains("| `PluginA` | Ext | USAGE |", text);
    }

    [Fact]
    public async Task Contracts_of_a_concrete_class_includes_the_callers_of_its_methods()
    {
        using var g = await GraphAsync();

        var text = await RunAsync("""{"view":"contracts","name":"Engine"}""", g.DatabasePath);

        Assert.Contains("# `Engine`: implementation (Class, pública, proyecto `Core`)", text);
        Assert.Contains("| `PluginA` | Ext | CALLS, USAGE |", text);
        Assert.Contains("| `src/Api/Program.cs` | Api | USAGE |", text);
        // Sus propios métodos no cuentan como consumidores.
        Assert.DoesNotContain("| `Engine` |", text.Split("## Consumidores")[1]);
    }

    [Fact]
    public async Task Contracts_of_a_project_tells_contracts_from_concrete_implementations_of_other_projects()
    {
        using var g = await GraphAsync();

        var text = await RunAsync("""{"view":"contracts","name":"Ext"}""", g.DatabasePath);

        Assert.Contains("# Qué usa `Ext` de otros proyectos", text);
        Assert.Contains("| `Core` | 1 | 1 | 0 | 0 | 0 |", text);
        Assert.Contains("| Core | `Engine` | implementation | pública | CALLS, USAGE |", text);
        Assert.Contains("| Core | `IPlugin` | contract | pública | IMPLEMENTS, INHERITS, USAGE |", text);
        // Lo que Ext define o usa dentro de sí mismo no entra.
        Assert.DoesNotContain("`PluginA`", text.Split("## Tipos usados")[1]);
    }

    [Fact]
    public async Task Contracts_for_an_ambiguous_or_unknown_name_asks_for_the_exact_qualified_name_or_says_none()
    {
        using var g = await GraphAsync();
        await g.NodeAsync("Class", "Engine", "demo.src.Other.Engine.Engine", "src/Other/Engine.cs", 1, 4, TypeProps("implementation", true, "Other"));

        var ambiguous = await RunAsync("""{"view":"contracts","name":"Engine"}""", g.DatabasePath);
        Assert.Contains("Hay 2 tipos que casan con `Engine`", ambiguous);
        Assert.Contains("`demo.src.Other.Engine.Engine`", ambiguous);

        Assert.Contains("No hay un proyecto ni un tipo llamado `Nope`", await RunAsync("""{"view":"contracts","name":"Nope"}""", g.DatabasePath));
    }

    [Fact]
    public async Task Contracts_overview_without_type_roles_asks_to_reanalyse()
    {
        using var g = await CbmTestGraph.CreateAsync(Path.Combine(_temp.Path, "codebase.db"), "demo");
        await g.NodeAsync("Class", "Old", "demo.Old", "src/Old.cs", 1, 2);

        Assert.Contains("Ningún tipo del grafo tiene `type_role`", await RunAsync("""{"view":"contracts"}""", g.DatabasePath));
    }

    [Fact]
    public void The_tool_description_advertises_the_new_views()
    {
        var tool = new GraphDomainTool();

        Assert.Contains("'projects'", tool.Description);
        Assert.Contains("'contracts'", tool.Description);
    }
}
