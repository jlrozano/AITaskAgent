using System.Text.Json.Nodes;

namespace DotNetEnricher.Tests;

/// <summary>
/// Lo que aportan las referencias exactas, los campos, los patrones de error y los comentarios (tandas 2 a 4 del informe de
/// uso de Roslyn), sobre soluciones pequeñas y un grafo con el esquema de cbm. Cada test ejecuta la pasada completa, como el CLI.
/// Los ficheros viven bajo <c>src/App</c>; <see cref="TestGraph.Qn"/> da el qualified_name de cbm.
/// </summary>
public sealed class RoslynTests
{
    private static async Task<TestGraph> BuildAsync(params (string Path, string Source)[] files)
    {
        var graph = new TestGraph().AddProject("App", "src/App");
        foreach (var (path, source) in files) graph.AddFile("src/App/" + path, source);
        await graph.BuildAsync();
        return graph;
    }

    private static string Qn(TestGraph graph, string file, string member) => graph.Qn("src/App/" + file, member);

    private static string[] Issues(JsonObject props) =>
        props["detected_issues"]?.AsArray().Select(i => (string)i!).ToArray() ?? [];

    // ─── Tanda 2: DI, código muerto, fan, jerarquía ─────────────────────────────

    private const string Registrations = """
        using System;
        using System.Collections.Generic;
        using Microsoft.Extensions.DependencyInjection;
        namespace App;

        public interface IUnused { }
        public sealed class Unused : IUnused { }
        public interface IInjected { }
        public sealed class Injected : IInjected { }
        public interface IResolved { }
        public sealed class Resolved : IResolved { }
        public interface IMany { }
        public sealed class ManyA : IMany { }
        public interface IFactoryDep { }
        public sealed class FactoryDep : IFactoryDep { }
        public interface IViaFactory { }
        public sealed class ViaFactory : IViaFactory { public ViaFactory(IFactoryDep dep) { } }

        public sealed class Consumer
        {
            public Consumer(IInjected injected, IEnumerable<IMany> many) { }
            public void Run(IServiceProvider provider) { provider.GetRequiredService<IResolved>(); }
        }

        public sealed class ConsumerTests
        {
            public void Runs() { new Consumer(null!, null!); }
        }

        public static class Startup
        {
            public static void Register(IServiceCollection services)
            {
                services.AddScoped<IUnused, Unused>();
                services.AddScoped<IInjected, Injected>();
                services.AddScoped<IResolved, Resolved>();
                services.AddScoped<IMany, ManyA>();
                services.AddScoped<IFactoryDep, FactoryDep>();
                services.AddScoped<IViaFactory>(sp => new ViaFactory(sp.GetRequiredService<IFactoryDep>()));
            }
        }
        """;

    [Fact]
    public async Task Unused_registration_ignores_the_registration_call_and_the_base_list_and_counts_every_consumer()
    {
        using var graph = await BuildAsync(("Registrations.cs", Registrations));
        await graph.EnrichAsync();

        JsonObject Reg(string service, string impl) => graph.PropsOrNull($"_di_.App.{service}->App.{impl}")!;

        // IUnused solo aparece en su registro y en la lista de bases de Unused: nadie lo pide.
        Assert.Equal(0, (int)Reg("IUnused", "Unused")["consumer_count"]!);
        Assert.True((bool)Reg("IUnused", "Unused")["unused_registration"]!);

        // Un parámetro de constructor, GetRequiredService<T>, IEnumerable<T> y una factoría son consumidores.
        Assert.Equal(1, (int)Reg("IInjected", "Injected")["consumer_count"]!);
        Assert.Equal(1, (int)Reg("IResolved", "Resolved")["consumer_count"]!);
        Assert.Equal(1, (int)Reg("IMany", "ManyA")["consumer_count"]!);
        // El constructor de ViaFactory y la factoría del registro piden IFactoryDep.
        Assert.Equal(2, (int)Reg("IFactoryDep", "FactoryDep")["consumer_count"]!);
        foreach (var used in new[] { ("IInjected", "Injected"), ("IResolved", "Resolved"), ("IMany", "ManyA"), ("IFactoryDep", "FactoryDep") })
            Assert.Null(Reg(used.Item1, used.Item2)["unused_registration"]);
    }

    [Fact]
    public async Task A_registration_nobody_consumes_does_not_keep_its_implementation_alive()
    {
        using var graph = await BuildAsync(("Registrations.cs", Registrations));
        await graph.EnrichAsync();

        // Antes el DI_RESOLVES de cualquier registro hacía raíz a la implementación.
        var unused = graph.Props("Registrations.Unused");
        Assert.True((bool)unused["dead_code_candidate"]!);
        Assert.False((bool)unused["is_reachable"]!);

        // Lo que consume código vivo sí vive: por la interfaz que pide el consumidor, a sus implementaciones.
        foreach (var impl in new[] { "Injected", "Resolved", "ManyA" })
            Assert.True((bool)graph.Props($"Registrations.{impl}")["is_reachable"]!, impl);
    }

    [Fact]
    public async Task Hosted_services_and_implementations_of_external_services_stay_alive_without_consumers()
    {
        using var graph = await BuildAsync(("Hosting.cs", """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            namespace App;

            public sealed class Pump : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
            }

            public sealed class Janitor : IDisposable { public void Dispose() { } }

            public static class Startup
            {
                public static void Register(IServiceCollection services)
                {
                    services.AddHostedService<Pump>();
                    services.AddSingleton<IDisposable, Janitor>();
                }
            }
            """));
        await graph.EnrichAsync();

        // Nadie nombra Pump ni Janitor: los consume el host y el contenedor, no el código del repositorio.
        Assert.True((bool)graph.Props("Hosting.Pump")["is_reachable"]!);
        Assert.True((bool)graph.Props("Hosting.Janitor")["is_reachable"]!);
        var hosted = graph.PropsOrNull("_di_.App.Pump->App.Pump")!;
        Assert.True((bool)hosted["consumed_by_framework"]!);
        Assert.Null(hosted["consumer_count"]);
        Assert.Null(hosted["unused_registration"]);
        // Un servicio externo (System.IDisposable): no se sabe quién lo pide, así que no se marca.
        var external = graph.PropsOrNull("_di_.System.IDisposable->App.Janitor")!;
        Assert.Null(external["consumer_count"]);
        Assert.Null(external["unused_registration"]);
    }

    [Fact]
    public async Task Private_methods_without_references_are_dead_even_inside_a_live_class()
    {
        using var graph = await BuildAsync(("Worker.cs", """
            namespace App;

            public sealed class Worker
            {
                public void Go() { Used(); }
                private void Used() { Helper(); }
                private void Helper() { }
                private void Forgotten() { DeadHelper(); }
                private void DeadHelper() { }
                private void OnClick(object sender, System.EventArgs e) { }
                [System.Obsolete] private void Tagged() { }
                public void PublicUnused() { }
            }

            public sealed class WorkerTests
            {
                public void Runs() { new Worker().Go(); }
            }
            """));
        await graph.EnrichAsync();

        foreach (var dead in new[] { "Forgotten", "DeadHelper" })
        {
            var props = graph.Props($"Worker.{dead}");
            Assert.True((bool)props["dead_code_candidate"]!, dead);
            Assert.Equal("high", (string?)props["dead_code_confidence"]);
            Assert.Contains("privado", (string?)props["dead_code_reason"]);
        }
        // Lo que sí se usa, y lo que no se puede dar por muerto: un manejador de eventos, un método con atributo, uno público.
        foreach (var alive in new[] { "Go", "Used", "Helper", "OnClick", "Tagged", "PublicUnused" })
            Assert.True((bool)graph.Props($"Worker.{alive}")["is_reachable"]!, alive);
        Assert.True((bool)graph.Props("Worker.Worker")["is_reachable"]!);
    }

    [Fact]
    public async Task Fan_in_and_fan_out_come_from_exact_references_and_not_from_cbm_edges()
    {
        using var graph = await BuildAsync(("Greeting.cs", """
            namespace App;

            public interface IGreeter { string Greet(string name); }
            public sealed class Greeter : IGreeter { public string Greet(string name) => name; }
            public sealed class A { public string Run(IGreeter greeter) => greeter.Greet("a"); }
            public sealed class B { public string Run() => new Greeter().Greet("b"); }
            public sealed class C
            {
                public void Loop(int n) { if (n > 0) Loop(n - 1); }
                public string Again(Greeter greeter) => greeter.Greet("c");
            }
            """));
        // Una arista por nombre que cbm se inventaría: no debe contar.
        await graph.AddEdgeAsync("A.Run", "C.Loop", "CALLS");
        await graph.EnrichAsync();

        // A llama por la interfaz, B y C por la clase: las tres llegan a la implementación.
        Assert.Equal(3, (int)graph.Props("Greeter.Greet")["fan_in"]!);
        // La recursión no es fan-in, y la arista inventada tampoco.
        Assert.Equal(0, (int)graph.Props("C.Loop")["fan_in"]!);
        // A.Run nombra el tipo IGreeter y su método Greet.
        Assert.Equal(2, (int)graph.Props("A.Run")["fan_out"]!);
    }

    [Fact]
    public async Task Implements_covers_inherited_interfaces_and_overrides_points_to_the_base_method()
    {
        using var graph = await BuildAsync(("Hierarchy.cs", """
            namespace App;

            public interface IBase { }
            public interface IDerived : IBase { }
            public class Base : IDerived { public virtual void Work() { } }
            public sealed class Leaf : Base { public override void Work() { } }
            """));
        await graph.EnrichAsync();

        var implements = graph.Edges("IMPLEMENTS");
        var direct = Assert.Single(implements, e => e.Source.EndsWith(".Hierarchy.Base", StringComparison.Ordinal) && e.Target.EndsWith(".Hierarchy.IDerived", StringComparison.Ordinal));
        Assert.Null(direct.Properties["is_inherited"]);
        var inherited = Assert.Single(implements, e => e.Source.EndsWith(".Hierarchy.Base", StringComparison.Ordinal) && e.Target.EndsWith(".Hierarchy.IBase", StringComparison.Ordinal));
        Assert.True((bool)inherited.Properties["is_inherited"]!);
        // Leaf no declara ninguna interfaz, pero las hereda de Base.
        Assert.Contains(implements, e => e.Source.EndsWith(".Hierarchy.Leaf", StringComparison.Ordinal) && e.Target.EndsWith(".Hierarchy.IBase", StringComparison.Ordinal));

        var overrides = Assert.Single(graph.Edges("OVERRIDES"));
        Assert.EndsWith(".Hierarchy.Leaf.Work", overrides.Source, StringComparison.Ordinal);
        Assert.EndsWith(".Hierarchy.Base.Work", overrides.Target, StringComparison.Ordinal);
    }

    // ─── Tanda 2: campos ────────────────────────────────────────────────────────

    [Fact]
    public async Task Fields_become_nodes_with_reads_and_writes_in_the_shape_the_unread_fields_facet_reads()
    {
        using var graph = await BuildAsync(("Counter.cs", """
            namespace App;

            public sealed class Counter
            {
                private int _written;
                private int _read;
                private int _both;
                private int _passedByRef;
                private int _passedByOut;
                private readonly string _name = "x";
                public const int Max = 3;

                public Counter(int start) { _written = start; }

                public void Tick()
                {
                    _both++;
                    _both += 2;
                    _passedByRef = 1;
                    Bump(ref _passedByRef);
                    Fill(out _passedByOut);
                }

                public int Total() { return _read + _both + _name.Length + Max + nameof(_written).Length; }

                private static void Bump(ref int x) { x++; }
                private static void Fill(out int x) { x = 1; }
            }
            """));
        await graph.EnrichAsync();

        var typeQn = Qn(graph, "Counter.cs", "Counter");
        var written = graph.PropsOrNull($"{typeQn}._written")!;
        Assert.NotNull(written);
        Assert.Equal("_written", (string?)written["field_name"]);
        Assert.Equal("int", (string?)written["type_fqn"]);
        Assert.Equal("private", (string?)written["accessibility"]);
        Assert.False((bool)written["is_static"]!);
        Assert.False((bool)written["is_final"]!);
        Assert.Equal("enricher", (string?)written["origin"]);

        var fields = graph.Edges("DEFINES").Where(e => e.Source == typeQn && e.Target.StartsWith(typeQn + ".", StringComparison.Ordinal)).ToList();
        Assert.Equal(7, fields.Count);

        List<(string Type, string Source)> Of(string field) =>
            new[] { "READS", "WRITES" }.SelectMany(t => graph.Edges(t).Where(e => e.Target == $"{typeQn}.{field}").Select(e => (t, e.Source))).ToList();

        // Solo escrito (en el constructor): es el caso de unread_fields. nameof no lee.
        Assert.Equal(("WRITES", $"{typeQn}.Counter"), Assert.Single(Of("_written")));
        // Solo leído.
        Assert.Equal(("READS", $"{typeQn}.Total"), Assert.Single(Of("_read")));
        // ++ y += dan las dos aristas del mismo método, con dos referencias.
        Assert.Contains(("READS", $"{typeQn}.Tick"), Of("_both"));
        Assert.Contains(("WRITES", $"{typeQn}.Tick"), Of("_both"));
        var bothWrites = graph.Edges("WRITES").Single(e => e.Target == $"{typeQn}._both");
        Assert.Equal(2, (int)bothWrites.Properties["count"]!);
        Assert.Equal(2, bothWrites.Properties["lines"]!.AsArray().Count);
        Assert.Contains("FieldEnricher", bothWrites.Properties["enriched_by"]!.AsArray().Select(v => (string?)v));
        // ref lee y escribe; out solo escribe.
        Assert.Contains(("READS", $"{typeQn}.Tick"), Of("_passedByRef"));
        Assert.Equal(("WRITES", $"{typeQn}.Tick"), Assert.Single(Of("_passedByOut")));
        // El inicializador escribe desde el tipo; readonly y const son finales.
        Assert.Contains(("WRITES", typeQn), Of("_name"));
        Assert.True((bool)graph.PropsOrNull($"{typeQn}._name")!["is_final"]!);
        var max = graph.PropsOrNull($"{typeQn}.Max")!;
        Assert.True((bool)max["is_static"]!);
        Assert.True((bool)max["is_final"]!);
        Assert.Contains(("READS", $"{typeQn}.Total"), Of("Max"));
    }

    // ─── Tanda 3: patrones de error ─────────────────────────────────────────────

    private const string IssueSamples = """
        using System;
        using System.Threading.Tasks;
        using Microsoft.Extensions.DependencyInjection;
        namespace App;

        public class Res : IDisposable { public void Dispose() { } public void Use() { } }
        public class Wrapper : IDisposable { public Wrapper(Res inner) { } public void Dispose() { } }

        public sealed class Samples
        {
            private Res? _held;
            private int _never;
            private int _flag = 1;

            public async void Fire() { await Task.Delay(1); }
            public async void OnClick(object sender, EventArgs e) { await Task.Delay(1); }

            public void Swallow() { try { Risky(); } catch (Exception) { } }
            public void SwallowByReturn() { try { Risky(); } catch (Exception) { return; } }
            public void SwallowCancel() { try { Risky(); } catch (OperationCanceledException) { } }
            public void Handles() { try { Risky(); } catch (Exception ex) { Console2.Log(ex); } }
            private void Risky() { }

            public void Blocks(Task<int> task) { var x = task.Result; task.Wait(); }

            public void Leak() { var r = new Res(); r.Use(); }
            public void Wrapped() { var r = new Res(); var w = new Wrapper(r); w.Dispose(); }
            public void FineUsingDeclaration() { using var r = new Res(); r.Use(); }
            public void FineUsingStatement() { using (var r = new Res()) { r.Use(); } }
            public void FineDispose() { var r = new Res(); r.Use(); r.Dispose(); }
            public Res FineReturn() { return new Res(); }
            public void FineField() { _held = new Res(); }
            public void Discarded() { new Res(); }

            public int Null(string? s) { return s.Length; }
            public async Task Forgetting() { Task.Delay(1); await Task.Delay(1); }
            public int Unreachable() { return 1; var x = 2; }
            [Obsolete("use New")] public void Old() { }
            public void CallsOld() { Old(); }
        }

        public static class Console2 { public static void Log(Exception e) { } }

        public interface IBig { }
        public sealed class Big : IBig { public Big(IScopedDep dep, ITransientDep other, IFactoryDep lazy) { } }
        public interface IScopedDep { }
        public sealed class ScopedDep : IScopedDep { }
        public interface ITransientDep { }
        public sealed class TransientDep : ITransientDep { }
        public interface IFactoryDep { }
        public sealed class FactoryDep : IFactoryDep { }
        public sealed class Fine { public Fine(IBig big) { } }

        public static class Startup
        {
            public static void Register(IServiceCollection services)
            {
                services.AddSingleton<IBig, Big>();
                services.AddScoped<IScopedDep, ScopedDep>();
                services.AddTransient<ITransientDep>();
                services.AddSingleton<IFactoryDep, FactoryDep>();
            }
        }
        """;

    [Fact]
    public async Task Detected_issues_list_the_method_level_error_patterns_with_their_line()
    {
        using var graph = await BuildAsync(("Samples.cs", IssueSamples));
        await graph.EnrichAsync();

        var sourceLines = IssueSamples.Split('\n');
        int LineOf(string fragment) => Array.FindIndex(sourceLines, l => l.Contains(fragment, StringComparison.Ordinal)) + 1;
        string[] Of(string method) => Issues(graph.Props($"Samples.{method}"));
        string[] Rules(string method) => graph.Props($"Samples.{method}")["issue_rules"]?.AsArray().Select(v => (string)v!).ToArray() ?? [];

        Assert.Contains("async-void", Rules("Fire"));
        Assert.Contains(Of("Fire"), i => i.StartsWith("async-void:", StringComparison.Ordinal) && i.EndsWith($" (línea {LineOf("public async void Fire()")})", StringComparison.Ordinal));
        Assert.Empty(Of("OnClick"));   // un manejador de eventos es el caso legítimo

        Assert.Contains("empty-catch", Rules("Swallow"));
        Assert.Contains("empty-catch", Rules("SwallowByReturn"));
        Assert.Empty(Of("SwallowCancel"));
        Assert.Empty(Of("Handles"));

        Assert.Equal(["blocking-async"], Rules("Blocks"));
        Assert.Equal(2, Of("Blocks").Length);   // .Result y .Wait()

        Assert.Equal(["undisposed-resource"], Rules("Leak"));
        Assert.Equal(["undisposed-resource"], Rules("Discarded"));
        foreach (var fine in new[] { "FineUsingDeclaration", "FineUsingStatement", "FineDispose", "FineReturn", "FineField" })
            Assert.Empty(Of(fine));
        // El envoltorio se libera y se queda con lo que recibe: no es una fuga.
        Assert.Empty(Of("Wrapped"));

        Assert.Equal(["null-deref"], Rules("Null"));
        Assert.Contains("CS8602", Of("Null").Single());
        Assert.Equal(["unawaited-task"], Rules("Forgetting"));
        Assert.Equal(["unreachable-code"], Rules("Unreachable"));
        Assert.Equal(["obsolete-api"], Rules("CallsOld"));
    }

    [Fact]
    public async Task Unused_fields_are_reported_on_the_field_node()
    {
        using var graph = await BuildAsync(("Samples.cs", IssueSamples));
        await graph.EnrichAsync();

        var typeQn = Qn(graph, "Samples.cs", "Samples");
        Assert.Contains("CS0169", Issues(graph.PropsOrNull($"{typeQn}._never")!).Single());
        Assert.Contains("CS0414", Issues(graph.PropsOrNull($"{typeQn}._flag")!).Single());
    }

    [Fact]
    public async Task Captive_dependency_flags_a_singleton_whose_constructor_asks_for_a_scoped_or_transient_service()
    {
        using var graph = await BuildAsync(("Samples.cs", IssueSamples));
        await graph.EnrichAsync();

        // Big (Singleton) pide IScopedDep (Scoped) e ITransientDep (Transient); IFactoryDep es Singleton y no cuenta.
        var issues = Issues(graph.PropsOrNull(Qn(graph, "Samples.cs", "Big.Big"))!);
        Assert.Equal(2, issues.Length);
        Assert.All(issues, i => Assert.StartsWith("captive-dependency:", i, StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Contains("'IScopedDep' (Scoped, registrado en src/App/Samples.cs:", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Contains("'ITransientDep' (Transient", StringComparison.Ordinal));
        // Fine pide un Singleton: nada.
        Assert.Null(graph.PropsOrNull(Qn(graph, "Samples.cs", "Fine.Fine"))!["detected_issues"]);
    }

    // ─── Tanda 3: autenticación explícita ───────────────────────────────────────

    private const string Endpoints = """
        using Microsoft.AspNetCore.Authorization;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.AspNetCore.Routing;
        namespace App;

        public static class Api
        {
            public static void Map(IEndpointRouteBuilder app)
            {
                app.MapGet("/open", () => "x");
                app.MapGet("/closed", () => "x").RequireAuthorization();
                app.MapGet("/anon", () => "x").AllowAnonymous();
                var group = app.MapGroup("/g").RequireAuthorization();
                group.MapGet("/inner", () => "x");
                Extra(app.MapGroup("/h"));
            }

            private static void Extra(RouteGroupBuilder group) { group.MapGet("/passed", () => "x"); }
        }

        [Authorize]
        public abstract class SecureBase : ControllerBase { }

        [ApiController]
        [Route("secure")]
        public sealed class SecureController : SecureBase
        {
            [HttpGet("a")] public string A() => "a";
            [HttpGet("b"), AllowAnonymous] public string B() => "b";
        }

        [ApiController]
        [Route("public")]
        public sealed class PublicController : ControllerBase
        {
            [HttpGet("a")] public string A() => "a";
        }
        """;

    private const string GlobalPolicy = """
        using Microsoft.AspNetCore.Authorization;
        namespace App;

        public static class Security
        {
            public static void Configure(AuthorizationOptions options) { options.FallbackPolicy = new object(); }
        }
        """;

    [Fact]
    public async Task Routes_without_any_declared_authorization_get_an_explicit_auth_required_false()
    {
        using var graph = await BuildAsync(("Endpoints.cs", Endpoints));
        await graph.EnrichAsync();

        JsonObject Route(string verb, string path) => graph.PropsOrNull($"__route__{verb}__{path}")!;

        var open = Route("GET", "/open");
        Assert.False((bool)open["auth_required"]!);
        Assert.True((bool)open["auth_inferred"]!);
        var publicController = Route("GET", "/public/a");
        Assert.False((bool)publicController["auth_required"]!);
        Assert.True((bool)publicController["auth_inferred"]!);

        // Declarada: RequireAuthorization, el de su grupo, [Authorize] heredado de una clase base.
        Assert.True((bool)Route("GET", "/closed")["auth_required"]!);
        Assert.True((bool)Route("GET", "/g/inner")["auth_required"]!);
        Assert.True((bool)Route("GET", "/secure/a")["auth_required"]!);
        Assert.Null(Route("GET", "/closed")["auth_inferred"]);

        // AllowAnonymous es un false declarado, no inferido.
        var anon = Route("GET", "/anon");
        Assert.False((bool)anon["auth_required"]!);
        Assert.Null(anon["auth_inferred"]);
        Assert.False((bool)Route("GET", "/secure/b")["auth_required"]!);
        Assert.Null(Route("GET", "/secure/b")["auth_inferred"]);

        // Un grupo que llega por parámetro puede traer su propia autorización: no se sabe.
        Assert.Null(Route("GET", "/passed")["auth_required"]);
    }

    [Fact]
    public async Task A_global_fallback_policy_protects_what_declares_nothing_so_it_is_not_marked_false()
    {
        using var graph = await BuildAsync(("Endpoints.cs", Endpoints), ("Security.cs", GlobalPolicy));
        await graph.EnrichAsync();

        Assert.Null(graph.PropsOrNull("__route__GET__/open")!["auth_required"]);
        Assert.Null(graph.PropsOrNull("__route__GET__/public/a")!["auth_required"]);
        // Lo declarado sigue igual.
        Assert.True((bool)graph.PropsOrNull("__route__GET__/closed")!["auth_required"]!);
        Assert.False((bool)graph.PropsOrNull("__route__GET__/anon")!["auth_required"]!);
    }

    // ─── Tanda 4: comentarios ───────────────────────────────────────────────────

    private const string Commented = """
        namespace App;

        public sealed class Order { }

        public sealed class Documented
        {
            /// <summary>Doubles <paramref name="x"/> for <see cref="Order"/> &amp; friends.</summary>
            /// <remarks>Watch the overflow.</remarks>
            /// <param name="x">the value</param>
            /// <returns>the double</returns>
            /// <exception cref="System.ArgumentException">if negative</exception>
            public int Double(int x)
            {
                // TODO: validate negatives
                var y = x * 2;   // FIXME (ana) check overflow
                // var z = Compute(y);
                // z++;
                // This is a plain explanation, no code here at all.
                return y; // todo el mundo lo sabe
            }

            // Calcula la media de los valores.
            // Ignora los nulos.
            public double Avg() { return 1; }

            // HACK: temporary patch
            public void Patch() { }

            /*
            if (Enabled) {
                Run();
            }
            */
            public void Gone() { }
        }
        """;

    [Fact]
    public async Task Comments_give_todo_markers_commented_code_full_docstrings_and_the_file_encoding()
    {
        using var graph = await BuildAsync(("Commented.cs", Commented));
        await graph.EnrichAsync();
        var lines = Commented.Split('\n');
        int LineOf(string fragment) => Array.FindIndex(lines, l => l.Contains(fragment, StringComparison.Ordinal)) + 1;

        // XML doc completa, con see/paramref resueltos, entidades decodificadas y las secciones que había.
        var doubled = graph.Props("Documented.Double");
        Assert.Equal("xml", (string?)doubled["docstring_source"]);
        Assert.Equal("Doubles x for Order & friends. Remarks: Watch the overflow. Param x: the value Returns: the double Throws System.ArgumentException: if negative",
            (string?)doubled["docstring"]);

        // TODO y FIXME con su línea; «todo» en minúsculas es una palabra, no una marca.
        var markers = doubled["todo_markers"]!.AsArray();
        Assert.Equal(2, (int)doubled["todo_count"]!);
        Assert.Equal("TODO", (string?)markers[0]!["kind"]);
        Assert.Equal(LineOf("// TODO"), (int)markers[0]!["line"]!);
        Assert.Equal("validate negatives", (string?)markers[0]!["text"]);
        Assert.Equal("FIXME", (string?)markers[1]!["kind"]);
        Assert.Equal(LineOf("// FIXME"), (int)markers[1]!["line"]!);

        // Código comentado: las dos líneas de código, sin la prosa que las sigue.
        var code = doubled["commented_code"]!.AsArray();
        var run = Assert.Single(code);
        Assert.Equal(LineOf("// var z"), (int)run!["start_line"]!);
        Assert.Equal(LineOf("// z++"), (int)run["end_line"]!);
        Assert.Equal(2, (int)doubled["commented_code_lines"]!);
        Assert.Equal("var z = Compute(y);", (string?)run["preview"]);

        // El comentario pegado a la declaración hace de docstring; un HACK no (tiene su propia propiedad).
        var avg = graph.Props("Documented.Avg");
        Assert.Equal("Calcula la media de los valores. Ignora los nulos.", (string?)avg["docstring"]);
        Assert.Equal("comment", (string?)avg["docstring_source"]);
        var patch = graph.Props("Documented.Patch");
        Assert.Null(patch["docstring"]);
        Assert.Equal("HACK", (string?)patch["todo_markers"]!.AsArray()[0]!["kind"]);

        // Un bloque /* */ con código: cuenta las cinco líneas, con los delimitadores.
        var gone = graph.Props("Documented.Gone");
        Assert.Equal(5, (int)gone["commented_code_lines"]!);
        Assert.Equal("if (Enabled) {", (string?)gone["commented_code"]!.AsArray()[0]!["preview"]);

        Assert.Equal("utf-8", (string?)graph.PropsOrNull(SymbolQn.ForFile(Path.Combine(graph.RepoRoot, "src", "App", "Commented.cs"), graph.Project, graph.RepoRoot)!)!["encoding"]);
    }

    [Theory]
    [InlineData("var z = Compute(y);", true)]
    [InlineData("z++;", true)]
    [InlineData("if (x) { Run(); }", true)]
    [InlineData("return value;", true)]
    [InlineData("this.Name = name;", true)]
    [InlineData("Esto se hace así porque el cliente lo pidió.", false)]
    [InlineData("Returns the total; see the docs", false)]
    [InlineData("foo;", false)]
    [InlineData("x", false)]
    [InlineData("TODO: revisar esto", false)]
    [InlineData("=== Section ===", false)]
    public void Commented_code_is_what_parses_as_csharp_without_errors_and_does_something(string comment, bool isCode) =>
        Assert.Equal(isCode, CommentScanner.LooksLikeCode([comment]));
}
