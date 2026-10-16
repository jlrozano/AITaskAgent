using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Enrichment;

namespace DotNetEnricher.Tests;

/// <summary>
/// Paquete B1b: APIs sensibles por símbolo (<c>SensitiveApiEnricher</c>), marcas de concurrencia por método y por campo
/// (<c>ConcurrencyEnricher</c>), huellas deterministas (<c>HashEnricher</c>) y procedencia uniforme de todo lo escrito.
/// </summary>
public sealed class RiskFactsTests
{
    private const string Sensitive = """
        using System;
        using System.Diagnostics;
        using System.IO;
        using System.Linq.Expressions;
        using System.Net.Http;
        using System.Threading.Tasks;
        using Newtonsoft.Json;
        namespace App;

        public static class File { public static string ReadAllText(string path) => path; }

        public sealed class Io
        {
            public string Read(string path) => System.IO.File.ReadAllText(path) + File.ReadAllText(path);
            public void Write(string path) { using var writer = new StreamWriter(path); writer.Write(1); }
            public void Run() => Process.Start("cmd");
            public async Task<string> Get(HttpClient http) => await http.GetStringAsync("http://localhost");
            public object? Make(string name) => Activator.CreateInstance(Type.GetType(name)!);
            public Func<int> Compile() { Expression<Func<int>> e = () => 1; return e.Compile(); }
            public string? Env() => Environment.GetEnvironmentVariable("HOME");
            public int Exec(System.Data.Common.DbCommand command) => command.ExecuteNonQuery();
            public T? Load<T>(string json) => JsonConvert.DeserializeObject<T>(json, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All });
            public T? Safe<T>(string json) => JsonConvert.DeserializeObject<T>(json, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None });
            public int Pure(int x) => x * 2;
        }
        """;

    private static List<(string Category, string Api, int Line)> Apis(JsonObject props) =>
        [.. props["sensitive_apis"]!.AsArray().Select(a => ((string)a!["category"]!, (string)a["api"]!, (int)a["line"]!))];

    [Fact]
    public async Task Sensitive_apis_are_resolved_by_symbol_with_category_api_and_line()
    {
        using var graph = new TestGraph().AddProject("App", "src/App").AddFile("src/App/Io.cs", Sensitive);
        await graph.BuildAsync();
        await graph.EnrichAsync();

        // El File propio no cuenta: solo System.IO.File.
        Assert.Equal([("file-io", "System.IO.File.ReadAllText", 14)], Apis(graph.Props("Io.Read")));
        Assert.Equal([("file-io", "new System.IO.StreamWriter", 15)], Apis(graph.Props("Io.Write")));
        Assert.Equal("process", Assert.Single(Apis(graph.Props("Io.Run"))).Category);
        var get = Assert.Single(Apis(graph.Props("Io.Get")));
        Assert.Equal(("network", "System.Net.Http.HttpClient.GetStringAsync"), (get.Category, get.Api));
        Assert.Equal(["System.Activator.CreateInstance", "System.Type.GetType"], Apis(graph.Props("Io.Make")).Select(x => x.Api).Order());
        Assert.Equal("dynamic-code", Assert.Single(Apis(graph.Props("Io.Compile"))).Category);
        Assert.Equal("environment", Assert.Single(Apis(graph.Props("Io.Env"))).Category);
        // ExecuteNonQuery se declara en DbCommand: cuenta por la regla del tipo base.
        Assert.Equal("sql", Assert.Single(Apis(graph.Props("Io.Exec"))).Category);

        // Newtonsoft solo con TypeNameHandling distinto de None: la asignación y la llamada.
        var load = Apis(graph.Props("Io.Load"));
        Assert.Equal(2, load.Count);
        Assert.All(load, x => Assert.Equal("deserialization", x.Category));
        Assert.Contains(load, x => x.Api == "Newtonsoft.Json.JsonConvert.DeserializeObject");
        Assert.Null(graph.Props("Io.Safe")["sensitive_apis"]);
        Assert.Null(graph.Props("Io.Pure")["sensitive_apis"]);

        Assert.Equal("[\"reflection\"]", graph.Props("Io.Make")["sensitive_categories"]!.ToJsonString());
    }

    private const string Concurrency = """
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        namespace App;

        public sealed class Worker
        {
            private static int _counter;
            private static readonly List<int> Items = new();
            private static readonly ConcurrentDictionary<int, int> Cache = new();
            private readonly object _gate = new();
            private const int Max = 3;

            public void Locked() { lock (_gate) { _counter++; } }
            public void Atomic() => Interlocked.Increment(ref _counter);
            public async void Fire() { await Task.Delay(1); }
            public void Forget()
            {
                DoAsync();
                _ = DoAsync();
                Task.Run(() => 1);
            }
            public int Block() => DoValueAsync().Result + DoValueAsync().GetAwaiter().GetResult();
            public void Waits() => DoAsync().Wait();
            public void Par(int[] xs) => Parallel.ForEach(xs, x => { });
            public void Mutate() { Items.Add(1); Cache.TryAdd(1, 1); }
            public void Handlers(List<int> xs) => xs.ForEach(async x => await Task.Delay(x));
            public Task DoAsync() => Task.CompletedTask;
            public Task<int> DoValueAsync() => Task.FromResult(1);
            public int Clean(int x) => x + Max;
        }
        """;

    private static List<(string Kind, int Line, string? Api)> Markers(JsonObject props) =>
        [.. props["concurrency_markers"]!.AsArray().Select(m => ((string)m!["kind"]!, (int)m["line"]!, (string?)m["api"]))];

    [Fact]
    public async Task Concurrency_markers_per_method_and_static_mutable_state_per_field()
    {
        using var graph = new TestGraph().AddProject("App", "src/App").AddFile("src/App/Worker.cs", Concurrency);
        await graph.BuildAsync();
        await graph.EnrichAsync();

        static string[] Kinds(JsonObject props) => [.. props["concurrency_kinds"]!.AsArray().Select(k => (string)k!)];

        Assert.Equal(["lock", "writes-static-mutable"], Kinds(graph.Props("Worker.Locked")));
        Assert.Contains(Markers(graph.Props("Worker.Locked")), m => m == ("writes-static-mutable", 15, "App.Worker._counter"));
        Assert.Equal(["Interlocked", "writes-static-mutable"], Kinds(graph.Props("Worker.Atomic")));
        Assert.Equal(["async-void"], Kinds(graph.Props("Worker.Fire")));

        var forget = Markers(graph.Props("Worker.Forget"));
        Assert.Equal([20, 21, 22], forget.Where(m => m.Kind == "fire-and-forget").Select(m => m.Line));
        Assert.Contains(forget, m => m == ("Task.Run", 22, "System.Threading.Tasks.Task.Run"));

        Assert.Equal(2, Markers(graph.Props("Worker.Block")).Count(m => m.Kind == "blocking-wait"));
        Assert.Equal(["blocking-wait"], Kinds(graph.Props("Worker.Waits")));
        Assert.Equal(["Parallel"], Kinds(graph.Props("Worker.Par")));
        // La colección estática no concurrente cuenta; la ConcurrentDictionary, no.
        Assert.Equal([("writes-static-mutable", 27, "App.Worker.Items")], Markers(graph.Props("Worker.Mutate")));
        Assert.Equal([("async-void", 28, "lambda")], Markers(graph.Props("Worker.Handlers")));
        Assert.Null(graph.Props("Worker.Clean")["concurrency_markers"]);
        Assert.Null(graph.Props("Worker.DoAsync")["concurrency_markers"]);

        void Field(string name, bool staticMutable, bool threadSafe)
        {
            var props = graph.Props("Worker." + name);
            Assert.Equal(staticMutable, (bool)props["is_static_mutable"]!);
            Assert.Equal(threadSafe, (bool)props["is_thread_safe_type"]!);
        }
        Field("_counter", staticMutable: true, threadSafe: false);
        Field("Items", staticMutable: true, threadSafe: false);
        Field("Cache", staticMutable: false, threadSafe: true);
        Field("_gate", staticMutable: false, threadSafe: false);
        Field("Max", staticMutable: false, threadSafe: false);
    }

    private const string Hashed = """
        namespace App;

        public interface IPricing { decimal Price(int units); }

        public sealed class Pricing : IPricing
        {
            private readonly decimal _unit = 2m;

            public decimal Price(int units)
            {
                // Precio por unidad.
                return units * _unit;
            }

            public Pricing() : this(1) { }
            private Pricing(int seed) { _ = seed; }
        }
        """;

    private static async Task<(JsonObject Price, JsonObject Ctor, JsonObject Type, JsonObject Interface)> HashesAsync(string source)
    {
        using var graph = new TestGraph().AddProject("App", "src/App").AddFile("src/App/Pricing.cs", source);
        await graph.BuildAsync();
        await graph.EnrichAsync();
        JsonObject Exact(string member) => graph.PropsOrNull(graph.Qn("src/App/Pricing.cs", member))!;
        return (Exact("Pricing.Price"), Exact("Pricing.Pricing"), Exact("Pricing"), Exact("IPricing"));
    }

    private static string Hash(JsonObject props, string key) => Assert.IsType<string>((string?)props[key]);

    [Fact]
    public async Task Hashes_ignore_trivia_and_change_with_the_body_or_the_contract()
    {
        var original = await HashesAsync(Hashed);
        Assert.Matches("^[0-9a-f]{64}$", Hash(original.Price, "body_hash"));
        Assert.NotNull(original.Ctor["body_hash"]);
        Assert.Null(original.Interface["body_hash"]);   // sin cuerpo: solo contrato y huella de tipo
        Assert.NotNull(original.Interface["type_hash"]);
        Assert.NotEqual(Hash(original.Type, "type_hash"), Hash(original.Interface, "type_hash"));

        // Otro repositorio (otra ruta), con otro formato y otros comentarios: las mismas huellas.
        var reformatted = await HashesAsync(Hashed.Replace("// Precio por unidad.", "/* comentario distinto */")
            .Replace("return units * _unit;", "return   units\n            * _unit ;"));
        Assert.Equal(Hash(original.Price, "body_hash"), Hash(reformatted.Price, "body_hash"));
        Assert.Equal(Hash(original.Price, "contract_hash"), Hash(reformatted.Price, "contract_hash"));
        Assert.Equal(Hash(original.Type, "type_hash"), Hash(reformatted.Type, "type_hash"));

        // Otro cuerpo: cambia body_hash y type_hash, no el contrato.
        var body = await HashesAsync(Hashed.Replace("return units * _unit;", "return units + _unit;"));
        Assert.NotEqual(Hash(original.Price, "body_hash"), Hash(body.Price, "body_hash"));
        Assert.Equal(Hash(original.Price, "contract_hash"), Hash(body.Price, "contract_hash"));
        Assert.NotEqual(Hash(original.Type, "type_hash"), Hash(body.Type, "type_hash"));
        Assert.Equal(Hash(original.Interface, "type_hash"), Hash(body.Interface, "type_hash"));

        // Otro inicializador de constructor (: this(2)): cambia el body_hash del constructor.
        var ctor = await HashesAsync(Hashed.Replace(": this(1)", ": this(2)"));
        Assert.NotEqual(Hash(original.Ctor, "body_hash"), Hash(ctor.Ctor, "body_hash"));

        // Otro nombre de parámetro: cambia el contrato.
        var contract = await HashesAsync(Hashed.Replace("public decimal Price(int units)", "public decimal Price(int count)").Replace("return units", "return count"));
        Assert.NotEqual(Hash(original.Price, "contract_hash"), Hash(contract.Price, "contract_hash"));
    }

    [Fact]
    public async Task Every_write_carries_uniform_provenance_and_a_second_pass_is_identical()
    {
        using var graph = new TestGraph().AddProject("App", "src/App").AddFile("src/App/Worker.cs", Concurrency);
        await graph.BuildAsync();
        await graph.EnrichAsync();

        var method = graph.Props("Worker.Locked");
        var marks = method["enriched_by"]!.AsArray().Select(v => (string?)v).ToList();
        Assert.Contains("SemanticMethodEnricher", marks);
        Assert.Contains("ConcurrencyEnricher", marks);
        Assert.Contains("HashEnricher", marks);
        Assert.Equal(DotNetEnricherIdentity.EnricherVersion, (string?)method["enricher_version"]);
        Assert.NotNull(method["enriched_at"]);
        Assert.Null(method["origin"]);   // un parche no cambia quién creó el nodo

        var field = graph.Props("Worker._counter");
        Assert.Equal("enricher", (string?)field["origin"]);
        Assert.Equal(DotNetEnricherIdentity.EnricherVersion, (string?)field["enricher_version"]);

        // Toda arista de un enriquecedor lleva origin y versión; las de cbm siguen sin marca.
        foreach (var type in new[] { "CALLS", "WRITES", "READS", "DEFINES", "BELONGS_TO_PROJECT" })
            Assert.All(graph.Edges(type), e =>
            {
                Assert.NotNull(e.Properties["enriched_by"]);
                Assert.NotNull(e.Properties["origin"]);
                Assert.Equal(DotNetEnricherIdentity.EnricherVersion, (string?)e.Properties["enricher_version"]);
            });
        Assert.Equal("compiler", (string?)graph.Edges("CALLS").First().Properties["origin"]);
        Assert.Equal("enricher", (string?)graph.Edges("WRITES").First().Properties["origin"]);
        Assert.All(graph.Edges("DEFINES_METHOD"), e => Assert.Null(e.Properties["enriched_by"]));

        string Snapshot() => string.Join("\n", graph.Edges("CALLS").Concat(graph.Edges("WRITES")).Select(e => $"{e.Source}>{e.Target}"))
            + "\n" + graph.Scalar<long>("SELECT COUNT(1) FROM nodes") + "/" + graph.Scalar<long>("SELECT COUNT(1) FROM edges");
        string? TypeHash() => graph.PropsOrNull(graph.Qn("src/App/Worker.cs", "Worker"))!["type_hash"]?.ToJsonString();
        string Facts(string qn) => string.Join("|", new[] { "concurrency_markers", "body_hash", "contract_hash", "is_static_mutable" }
            .Select(k => graph.Props(qn)[k]?.ToJsonString()));
        var before = (Snapshot(), Facts("Worker.Locked"), Facts("Worker._counter"), TypeHash());

        await graph.EnrichAsync();

        Assert.Equal(before, (Snapshot(), Facts("Worker.Locked"), Facts("Worker._counter"), TypeHash()));
    }
}
