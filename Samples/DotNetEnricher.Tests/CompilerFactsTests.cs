using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Enrichment;

namespace DotNetEnricher.Tests;

/// <summary>
/// Paquete B1a: aristas CALLS exactas en lugar de las de cbm (<c>CallEnricher</c>), excepciones por método y THROWS
/// (<c>ThrowsEnricher</c>), y los nodos que los enriquecedores dejaban sin propiedades: constructores, métodos de tipos
/// anidados e implementaciones explícitas de interfaz.
/// </summary>
public sealed class CompilerFactsTests
{
    private const string Calls = """
        using System.Linq;
        namespace App;

        public interface IRepo { int Get(int id); }
        public sealed class Repo : IRepo { public int Get(int id) => id; }

        public abstract class Base
        {
            protected Base(int x) { }
            public virtual int Calc() => 1;
        }

        public sealed class Service : Base
        {
            private readonly IRepo _repo;
            public Service(IRepo repo) : base(1) { _repo = repo; }

            public int Run(int[] ids)
            {
                var name = nameof(Helper);
                var total = ids.Select(Helper).Sum();
                return total + _repo.Get(1) + Calc() + name.Length;
            }

            private static int Helper(int x) => x;

            public static Service Create() => new Service(new Repo());
        }
        """;

    private static async Task<TestGraph> CallsAsync()
    {
        var graph = new TestGraph().AddProject("App", "src/App").AddFile("src/App/Service.cs", Calls);
        await graph.BuildAsync();
        // Lo que cbm habría resuelto por nombre: Get a la implementación (la llamada es por la interfaz) y Helper (bien).
        await graph.AddEdgeAsync("Service.Run", "Repo.Get", "CALLS");
        await graph.AddEdgeAsync("Service.Run", "Service.Helper", "CALLS");
        return graph;
    }

    private static List<(string Target, JsonObject Properties)> CallsFrom(TestGraph graph, string sourceSuffix) =>
        [.. graph.Edges("CALLS").Where(e => e.Source.EndsWith("." + sourceSuffix, StringComparison.Ordinal)).Select(e => (e.Target, e.Properties))];

    [Fact]
    public async Task Calls_are_exact_edges_to_the_declared_symbol_with_kind_dispatch_and_lines()
    {
        using var graph = await CallsAsync();
        await graph.EnrichAsync();

        var run = CallsFrom(graph, "Service.Run");
        Assert.Equal(3, run.Count);
        Assert.All(run, c =>
        {
            Assert.True((bool)c.Properties["exact"]!);
            Assert.Equal("compiler", (string?)c.Properties["origin"]);
            Assert.NotNull(c.Properties["line"]);
            Assert.Contains("CallEnricher", c.Properties["enriched_by"]!.AsArray().Select(v => (string?)v));
        });

        // Por la interfaz: al miembro de la interfaz, no a la implementación que cbm adivinó.
        var get = Assert.Single(run, c => c.Target.EndsWith(".IRepo.Get", StringComparison.Ordinal));
        Assert.Equal("interface", (string?)get.Properties["dispatch"]);
        Assert.Equal("App.IRepo.Get(int)", (string?)get.Properties["callee"]);
        Assert.Equal("[\"invocation\"]", get.Properties["kinds"]!.ToJsonString());
        Assert.Equal(22, (int)get.Properties["line"]!);
        Assert.DoesNotContain(run, c => c.Target.EndsWith(".Repo.Repo.Get", StringComparison.Ordinal));

        Assert.Equal("virtual", (string?)Assert.Single(run, c => c.Target.EndsWith(".Base.Calc", StringComparison.Ordinal)).Properties["dispatch"]);

        // Como delegado sí; el nameof no cuenta.
        var helper = Assert.Single(run, c => c.Target.EndsWith(".Service.Helper", StringComparison.Ordinal));
        Assert.Equal("[\"delegate\"]", helper.Properties["kinds"]!.ToJsonString());
        Assert.Equal(1, (int)helper.Properties["count"]!);
        Assert.Null(helper.Properties["dispatch"]);

        // new al constructor con nodo (el de Repo es implícito y no tiene nodo) y : base(..) al constructor base.
        var create = Assert.Single(CallsFrom(graph, "Service.Create"));
        Assert.EndsWith(".Service.Service.Service", create.Target);
        Assert.Equal("[\"creation\"]", create.Properties["kinds"]!.ToJsonString());
        var ctor = Assert.Single(CallsFrom(graph, "Service.Service.Service"));
        Assert.EndsWith(".Service.Base.Base", ctor.Target);   // {fichero}.{tipo}.{constructor}
        Assert.Equal("[\"constructor-initializer\"]", ctor.Properties["kinds"]!.ToJsonString());
    }

    [Fact]
    public async Task Cbm_calls_from_analyzed_csharp_files_are_replaced_and_recorded_but_other_languages_are_kept()
    {
        using var graph = await CallsAsync();
        await graph.AddNodeAsync("Method", "run", "proj.src.Legacy.run", "src/Legacy.java", new JsonObject());
        await graph.AddNodeAsync("Method", "stop", "proj.src.Legacy.stop", "src/Legacy.java", new JsonObject());
        await graph.AddEdgeAsync("Legacy.run", "Legacy.stop", "CALLS");

        var first = await graph.EnrichAsync();

        Assert.Equal(2, first.Outcome.CbmEdgesReplaced);
        Assert.Contains(graph.Edges("CALLS"), e => e.Source == "proj.src.Legacy.run" && e.Target == "proj.src.Legacy.stop" && e.Properties["exact"] is null);
        Assert.DoesNotContain(graph.Edges("CALLS"), e => e.Source.Contains(".src.App.", StringComparison.Ordinal) && e.Properties["exact"] is null);

        using (var conn = graph.Open())
        {
            var run = Assert.Single(await EnrichmentRuns.ListAsync(conn, CancellationToken.None, DotNetEnricherIdentity.EnricherName));
            Assert.Equal(2, run.Outcome!.CbmEdgesReplaced);
            var calls = JsonNode.Parse(run.Outcome.Details!)!["enricher_details"]!["calls"]!;
            Assert.Equal(1, (int)calls["cbm_with_equivalent"]!);    // Run → Helper
            Assert.Equal(1, (int)calls["cbm_without_equivalent"]!); // Run → Repo.Get
            Assert.Equal(2, (int)JsonNode.Parse(run.Outcome.Details!)!["cbm_edges_replaced"]!["CALLS"]!);
        }

        // Repetir la pasada no duplica ni vuelve a contar: las de cbm ya no están y las exactas se reescriben.
        var exact = graph.Edges("CALLS").Count(e => e.Properties["exact"] is not null);
        var second = await graph.EnrichAsync();
        Assert.Equal(0, second.Outcome.CbmEdgesReplaced);
        Assert.Equal(exact, graph.Edges("CALLS").Count(e => e.Properties["exact"] is not null));
    }

    private const string Throws = """
        using System;
        namespace App;

        public sealed class OrderException : Exception { public OrderException(string m) : base(m) { } }

        public sealed class Thrower
        {
            public void New(int x) { if (x < 0) throw new OrderException("neg"); }
            public string Expr(string? s) => s ?? throw new ArgumentNullException(nameof(s));
            public void Rethrow() { try { New(1); } catch (InvalidOperationException) { throw; } }
            public void RethrowVariable() { try { New(1); } catch (Exception ex) { throw ex; } }
            public void Wrap() { try { New(1); } catch (Exception ex) { throw new OrderException(ex.Message); } }
            public void Empty() { try { New(1); } catch (Exception) { } }
            public void LogOnly() { try { New(1); } catch (Exception ex) { Log(ex); } }
            public int Generic() { try { New(1); return 1; } catch { var y = 2; return y; } }
            public void Cancelled() { try { New(1); } catch (OperationCanceledException) { } }
            public void Specific() { try { New(1); } catch (FormatException ex) { Log(ex); } }
            public Func<int> Lazy() => () => throw new NotSupportedException();
            private static void Log(Exception e) { }
        }

        public interface IThrower { void Go(); }
        """;

    [Fact]
    public async Task Throws_rethrows_and_swallowed_catches_are_written_per_method_with_exact_throws_edges()
    {
        using var graph = new TestGraph().AddProject("App", "src/App").AddFile("src/App/Thrower.cs", Throws);
        await graph.BuildAsync();
        await graph.EnrichAsync();

        JsonObject P(string method) => graph.Props("Thrower." + method);
        string[] ThrowsOf(string method) => [.. P(method)["throws"]!.AsArray().Select(v => (string)v!)];
        string[] Kinds(string method) => [.. (P(method)["swallowed_catches"]?.AsArray() ?? []).Select(c => (string)c!["kind"]!)];

        Assert.Equal(["App.OrderException"], ThrowsOf("New"));
        Assert.Equal(["System.ArgumentNullException"], ThrowsOf("Expr"));
        Assert.Equal(["System.InvalidOperationException"], ThrowsOf("Rethrow"));
        Assert.True((bool)P("Rethrow")["rethrows"]!);
        Assert.Null(P("Rethrow")["rethrow_loses_stack"]);
        Assert.Equal(["System.Exception"], ThrowsOf("RethrowVariable"));
        Assert.True((bool)P("RethrowVariable")["rethrow_loses_stack"]!);
        Assert.Equal(["App.OrderException"], ThrowsOf("Wrap"));
        Assert.False((bool)P("Wrap")["rethrows"]!);
        // El de la lambda no sale al llamar a Lazy; sin excepciones no hay throws (nunca []), pero sí consta como analizado.
        Assert.Null(P("Lazy")["throws"]);
        Assert.False((bool)P("Lazy")["rethrows"]!);

        foreach (var method in new[] { "New", "Rethrow", "RethrowVariable", "Wrap", "Cancelled", "Specific", "Lazy" })
            Assert.False((bool)P(method)["swallows_exceptions"]!, method);
        Assert.Equal(["empty"], Kinds("Empty"));
        Assert.Equal(["log-only"], Kinds("LogOnly"));
        Assert.Equal(["generic"], Kinds("Generic"));
        Assert.True((bool)P("Generic")["swallows_exceptions"]!);

        // El hallazgo de IssueEnricher sigue donde estaba y no se repite.
        Assert.Equal(["empty-catch"], P("Empty")["issue_rules"]!.AsArray().Select(v => (string)v!));
        Assert.Null(P("LogOnly")["issue_rules"]);

        // Sin cuerpo no se afirma nada.
        Assert.Null(graph.Props("IThrower.Go")["throws"]);

        // THROWS exacta al tipo con nodo (OrderException); las de la BCL solo en la propiedad.
        var throwsEdges = graph.Edges("THROWS");
        Assert.All(throwsEdges, e => Assert.True((bool)e.Properties["exact"]!));
        Assert.Equal(2, throwsEdges.Count);
        Assert.All(throwsEdges, e => Assert.EndsWith(".Thrower.OrderException", e.Target));   // el nodo Class de {fichero}.OrderException
        Assert.Equal("[\"new\"]", Assert.Single(throwsEdges, e => e.Source.EndsWith(".Thrower.New", StringComparison.Ordinal)).Properties["kinds"]!.ToJsonString());
    }

    [Fact]
    public async Task A_method_without_throws_gets_the_same_contract_hash_as_one_with_the_empty_list_of_earlier_passes()
    {
        using var graph = new TestGraph().AddProject("App", "src/App").AddFile("src/App/Thrower.cs", Throws);
        await graph.BuildAsync();
        await graph.EnrichAsync();
        var hash = (string)graph.Props("Thrower.Lazy")["contract_hash"]!;

        // Lo que dejaba la versión anterior de ThrowsEnricher: throws = [] en un método que no lanza.
        using (var conn = graph.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE nodes SET properties = json_set(properties, '$.throws', json('[]')) WHERE qualified_name LIKE '%.Thrower.Lazy'";
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }
        await EnrichmentPipeline.RunAsync(graph.Request(), [new DotNetEnricher.Enrichers.HashEnricher()], TextWriter.Null, null, CancellationToken.None);

        Assert.Equal("[]", graph.Props("Thrower.Lazy")["throws"]!.ToJsonString());
        Assert.Equal(hash, (string)graph.Props("Thrower.Lazy")["contract_hash"]!);
    }

    [Fact]
    public async Task A_node_whose_properties_are_not_json_does_not_abort_the_pass_and_keeps_its_text()
    {
        using var graph = new TestGraph().AddProject("App", "src/App").AddFile("src/App/Thrower.cs", Throws);
        await graph.BuildAsync();
        const string raw = "{\"base_classes\": \"A\nB\", truncated";
        using (var conn = graph.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO nodes(project, label, name, qualified_name, file_path, start_line, end_line, properties)
                VALUES (@p, 'Method', 'Broken', @q, 'src/App/Thrower.cs', 1, 2, @props)
                """;
            cmd.Parameters.AddWithValue("@p", graph.Project);
            cmd.Parameters.AddWithValue("@q", graph.Qn("src/App/Thrower.cs", "Ghost.Broken"));
            cmd.Parameters.AddWithValue("@props", raw);
            cmd.ExecuteNonQuery();
        }

        await graph.EnrichAsync();

        Assert.Equal(raw, (string?)graph.Props("Ghost.Broken")[EnrichmentMarks.RawPropertiesKey]);
        Assert.Equal(["App.OrderException"], graph.Props("Thrower.New")["throws"]!.AsArray().Select(v => (string)v!));
    }

    private const string CleanCalls = """
        namespace App;

        public sealed class Clean
        {
            public void Send() { }
            public static void Ping() { }
            public void Go() { }
        }

        public static class Other { public static void Ping() { } }
        """;

    // Un paquete sin restaurar: el tipo del receptor no existe, así que _client.Send() no da símbolo ni candidatos.
    private const string BrokenCalls = """
        using Missing.Package;
        namespace App;

        public sealed class Broken
        {
            private readonly IMissingClient _client;
            public Broken(IMissingClient client) { _client = client; }
            public void Run() { _client.Send(); Clean.Ping(); }
        }
        """;

    [Fact]
    public async Task Cbm_calls_are_kept_in_files_with_unresolved_symbols_unless_an_exact_one_replaces_them()
    {
        using var graph = new TestGraph().AddProject("App", "src/App")
            .AddFile("src/App/Clean.cs", CleanCalls).AddFile("src/App/Broken.cs", BrokenCalls);
        await graph.BuildAsync(allowErrors: true);
        await graph.AddEdgeAsync("Broken.Run", "Clean.Send", "CALLS");   // por nombre: no hay exacta que la sustituya
        await graph.AddEdgeAsync("Broken.Run", "Clean.Ping", "CALLS");   // con exacta: el upsert la convierte
        await graph.AddEdgeAsync("Broken.Run", "Other.Ping", "CALLS");   // mismo nombre que una exacta del origen: se sustituye
        await graph.AddEdgeAsync("Clean.Go", "Clean.Send", "CALLS");     // fichero limpio: se sustituye (se borra)

        var result = await graph.EnrichAsync();

        var run = CallsFrom(graph, "Broken.Run");
        Assert.Null(Assert.Single(run, c => c.Target.EndsWith(".Clean.Send", StringComparison.Ordinal)).Properties["exact"]);
        Assert.True((bool)Assert.Single(run, c => c.Target.EndsWith(".Clean.Ping", StringComparison.Ordinal)).Properties["exact"]!);
        Assert.DoesNotContain(run, c => c.Target.EndsWith(".Other.Ping", StringComparison.Ordinal));
        Assert.Equal(2, run.Count);
        Assert.Empty(CallsFrom(graph, "Clean.Go"));
        Assert.Equal(3, result.Outcome.CbmEdgesReplaced);

        using var conn = graph.Open();
        var details = JsonNode.Parse(Assert.Single(await EnrichmentRuns.ListAsync(conn, CancellationToken.None, DotNetEnricherIdentity.EnricherName)).Outcome!.Details!)!;
        var calls = details["enricher_details"]!["calls"]!;
        Assert.Equal(1, (int)calls["cbm_kept_unresolved"]!);
        Assert.Equal(1, (int)calls["files_with_unresolved_symbols"]!);
    }

    private const string Members = """
        namespace App;

        public interface IStep { int Execute(); string Name(); }

        public abstract class StepBase : IStep
        {
            private readonly string _name;

            /// <summary>Crea el paso.</summary>
            /// <param name="name">Nombre del paso.</param>
            protected StepBase(string name)
            {
                if (name.Length == 0) name = "x";
                _name = name;
            }

            protected abstract int Execute();
            int IStep.Execute() => Execute();
            string IStep.Name() => _name;
        }

        public sealed class Host
        {
            internal Host() { }

            private sealed class Inner
            {
                public int Run() => 1;
            }
        }
        """;

    [Fact]
    public async Task Constructors_nested_types_and_explicit_implementations_get_the_method_properties()
    {
        using var graph = new TestGraph().AddProject("App", "src/App").AddFile("src/App/Steps.cs", Members);
        await graph.BuildAsync();
        var result = await graph.EnrichAsync();

        var ctor = graph.PropsOrNull(graph.Qn("src/App/Steps.cs", "StepBase.StepBase"))!;
        Assert.Equal("protected", (string?)ctor["accessibility"]);
        Assert.True((bool)ctor["is_constructor"]!);
        Assert.Null(ctor["return_type_fqn"]);
        Assert.Equal(2, (int)ctor["cyclomatic"]!);
        Assert.Equal("(string name)", (string?)ctor["overloads"]![0]!["signature"]);
        Assert.Equal("Nombre del paso.", (string?)ctor["doc_params"]!["name"]);
        Assert.Equal("internal", (string?)graph.PropsOrNull(graph.Qn("src/App/Steps.cs", "Host.Host"))!["accessibility"]);

        // cbm nombra el tipo anidado con la cadena de tipos: {fichero}.Host.Inner.Run.
        Assert.NotNull(graph.PropsOrNull(graph.Qn("src/App/Steps.cs", "Host.Inner")));
        var nested = graph.PropsOrNull(graph.Qn("src/App/Steps.cs", "Host.Inner.Run"))!;
        Assert.Equal("public", (string?)nested["accessibility"]);
        Assert.Equal(1, (int)nested["cyclomatic"]!);

        // La explícita con nodo propio lleva sus propiedades; la que comparte nodo con el método ordinario no lo pisa.
        var name = graph.PropsOrNull(graph.Qn("src/App/Steps.cs", "StepBase.Name"))!;
        Assert.Equal("private", (string?)name["accessibility"]);
        Assert.True((bool)name["is_explicit_interface_impl"]!);
        Assert.Equal("string", (string?)name["return_type_fqn"]);
        var execute = graph.PropsOrNull(graph.Qn("src/App/Steps.cs", "StepBase.Execute"))!;
        Assert.Equal("protected", (string?)execute["accessibility"]);
        Assert.True((bool)execute["is_abstract"]!);
        Assert.Null(execute["is_explicit_interface_impl"]);
        Assert.Equal(2, (int)execute["overload_count"]!);

        foreach (var enricher in new[] { "SemanticMethodEnricher", "ContractEnricher", "CyclomaticEnricher" })
            Assert.Equal(0, result.PatchStats[enricher].Missed);
    }
}
