using System.Text.Json.Nodes;

namespace DotNetEnricher.Tests;

/// <summary>
/// Los enriquecedores cuya salida consume la app (<c>SqlFacetExtractorStep</c>), sobre soluciones pequeñas compiladas
/// sin MSBuild y un grafo con el esquema de cbm. Cada test ejecuta la pasada completa, como el CLI.
/// </summary>
public sealed class EnricherTests
{
    private const string Services = """
        using System.Threading.Tasks;
        namespace Shop.Core;

        public interface IOrderService { Task<int> CreateAsync(string customer); }

        /// <summary>Servicio de pedidos.</summary>
        public sealed class OrderService : IOrderService
        {
            public async Task<int> CreateAsync(string customer)
            {
                await Task.Yield();
                return Validate(customer) ? 1 : 0;
            }

            private static bool Validate(string customer)
            {
                if (customer is null || customer.Length == 0) return false;
                switch (customer[0])
                {
                    case 'A': return true;
                    case 'B': return customer.Length > 2 && customer.Length < 10;
                    default: return customer.Length > 1 ? true : false;
                }
            }
        }

        public interface IUnusedService { }
        public sealed class UnusedService : IUnusedService { }

        internal sealed class Orphan
        {
            private void Nobody() { }
        }
        """;

    private const string Startup = """
        using Microsoft.Extensions.DependencyInjection;
        using Shop.Core;
        namespace Shop.Api;

        public static class Startup
        {
            public static void Register(IServiceCollection services)
            {
                services.AddScoped<IOrderService, OrderService>();
                services.AddSingleton<IUnusedService, UnusedService>();
            }
        }
        """;

    private const string Controller = """
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Authorization;
        using Microsoft.AspNetCore.Mvc;
        using Shop.Core;
        namespace Shop.Api;

        [ApiController]
        [Route("api/[controller]")]
        [Authorize]
        public sealed class OrdersController : ControllerBase
        {
            private readonly IOrderService _service;
            public OrdersController(IOrderService service) { _service = service; }

            [HttpPost("{customer}")]
            public async Task<int> Create(string customer)
            {
                var id = await _service.CreateAsync(customer);
                return id;
            }

            [HttpGet("health")]
            [AllowAnonymous]
            public string Health() => "ok";
        }
        """;

    private const string MinimalApi = """
        using Microsoft.AspNetCore.Routing;
        namespace Shop.Api;

        public static class ReportEndpoints
        {
            public static void Map(IEndpointRouteBuilder app)
            {
                var group = app.MapGroup("/reports").RequireAuthorization();
                group.MapGet("/daily", Daily);
                group.MapPost("/rebuild", () => "done");
            }

            private static string Daily() => "daily";
        }
        """;

    private const string Worker = """
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.Hosting;
        namespace Shop.Api;

        public sealed class ImportWorker : BackgroundService
        {
            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                await LoadAsync();
                if (stoppingToken.IsCancellationRequested)
                    await AbortAsync();
                await SaveAsync();
            }

            public Task LoadAsync() => Task.CompletedTask;
            public Task AbortAsync() => Task.CompletedTask;
            public Task SaveAsync() => Task.CompletedTask;
        }
        """;

    // El test llama de verdad al servicio: las CALLS de cbm que salen de los ficheros C# se sustituyen por las exactas de
    // CallEnricher, así que la dependencia Tests → Core tiene que estar en el código y no solo en una arista inventada.
    private const string Tests = """
        namespace Shop.Tests;
        public sealed class OrderServiceTests
        {
            public void Creates() { _ = new Shop.Core.OrderService().CreateAsync("A"); }
        }
        """;

    private static async Task<TestGraph> ShopAsync()
    {
        var graph = new TestGraph()
            .AddProject("Shop.Core", "src/Shop.Core")
            .AddProject("Shop.Api", "src/Shop.Api", isExecutable: true)
            .AddProject("Shop.Tests", "tests/Shop.Tests", isExecutable: true, isTest: true)
            .AddFile("src/Shop.Core/Services/OrderService.cs", Services)
            .AddFile("src/Shop.Api/Startup.cs", Startup)
            .AddFile("src/Shop.Api/Controllers/OrdersController.cs", Controller)
            .AddFile("src/Shop.Api/ReportEndpoints.cs", MinimalApi)
            .AddFile("src/Shop.Api/ImportWorker.cs", Worker)
            .AddFile("tests/Shop.Tests/OrderServiceTests.cs", Tests);
        await graph.BuildAsync();
        // Lo que cbm habría resuelto: el controller llama al servicio y usa su interfaz; el test usa el servicio.
        await graph.AddEdgeAsync("OrdersController.Create", "IOrderService.CreateAsync", "CALLS");
        await graph.AddEdgeAsync("OrdersController.OrdersController.OrdersController", "IOrderService", "USAGE");
        await graph.AddEdgeAsync("OrderService.CreateAsync", "OrderService.Validate", "CALLS");
        await graph.AddEdgeAsync("OrderServiceTests.Creates", "OrderService.CreateAsync", "CALLS");
        return graph;
    }

    [Fact]
    public async Task Di_registrations_become_nodes_with_their_edges_and_mark_the_implementation()
    {
        using var graph = await ShopAsync();
        await graph.EnrichAsync();

        var reg = graph.PropsOrNull("_di_.Shop.Core.IOrderService->Shop.Core.OrderService")!;
        Assert.NotNull(reg);
        Assert.Equal("Scoped", (string?)reg["lifetime"]);
        Assert.Equal("enricher", (string?)reg["origin"]);
        Assert.Contains("DiEnricher", reg["enriched_by"]!.AsArray().Select(v => (string?)v));
        Assert.Equal("src/Shop.Api/Startup.cs",
            graph.Scalar<string>("SELECT file_path FROM nodes WHERE label = 'DiRegistration' AND name LIKE 'AddScoped%'"));

        var resolves = graph.Edges("DI_RESOLVES").Where(e => e.Source.StartsWith("_di_.Shop.Core.IOrderService", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, resolves.Count);
        Assert.Contains(resolves, e => e.Target.EndsWith(".IOrderService", StringComparison.Ordinal) && e.Properties["role"] is null);
        Assert.Contains(resolves, e => e.Target.EndsWith(".OrderService.OrderService", StringComparison.Ordinal) && (string?)e.Properties["role"] == "implementation");

        var impl = graph.Props("OrderService.OrderService");
        Assert.Equal("Scoped", (string?)impl["di_lifetime"]);
        Assert.Equal("Shop.Core.IOrderService", (string?)impl["di_registered_as"]);
    }

    [Fact]
    public async Task Controller_and_minimal_api_routes_get_handles_edges_auth_and_route_path()
    {
        using var graph = await ShopAsync();
        await graph.EnrichAsync();

        var create = graph.PropsOrNull("__route__POST__/api/Orders/{customer}")!;
        Assert.NotNull(create);
        Assert.True((bool)create["auth_required"]!);
        var health = graph.PropsOrNull("__route__GET__/api/Orders/health")!;
        Assert.False((bool)health["auth_required"]!);

        var handles = graph.Edges("HANDLES");
        Assert.Contains(handles, e => e.Source.EndsWith(".OrdersController.Create", StringComparison.Ordinal)
                                      && e.Target == "__route__POST__/api/Orders/{customer}"
                                      && (string?)e.Properties["route_path"] == "/api/Orders/{customer}");
        var method = graph.Props("OrdersController.Create");
        Assert.Equal("/api/Orders/{customer}", (string?)method["route_path"]);
        Assert.Equal("POST", (string?)method["route_method"]);

        // Minimal API: prefijo del grupo y autorización heredada; el method group se enlaza, la lambda queda huérfana.
        var daily = graph.PropsOrNull("__route__GET__/reports/daily")!;
        Assert.True((bool)daily["auth_required"]!);
        Assert.Equal("/reports", (string?)daily["group_prefix"]);
        Assert.Contains(handles, e => e.Source.EndsWith(".ReportEndpoints.Daily", StringComparison.Ordinal) && e.Target == "__route__GET__/reports/daily");
        Assert.NotNull(graph.PropsOrNull("__route__POST__/reports/rebuild"));
        Assert.DoesNotContain(handles, e => e.Target == "__route__POST__/reports/rebuild");
    }

    [Fact]
    public async Task Background_service_and_http_handlers_become_flows_with_ordered_steps()
    {
        using var graph = await ShopAsync();
        await graph.EnrichAsync();

        var flow = graph.PropsOrNull("_flow_.ImportWorker.ExecuteAsync")!;
        Assert.NotNull(flow);
        Assert.Equal("background_service", (string?)flow["flow_kind"]);
        // Cuatro aristas del camino feliz: ExecuteAsync→Load, Load→Abort, Abort→Save y Load→Save.
        Assert.Equal(4, (int)flow["step_count"]!);
        var entry = Assert.Single(graph.Edges("FLOW_ENTRY"), e => e.Source == "_flow_.ImportWorker.ExecuteAsync");
        Assert.EndsWith(".ImportWorker.ExecuteAsync", entry.Target, StringComparison.Ordinal);

        var next = graph.Edges("FLOW_NEXT").Where(e => (string?)e.Properties["flow_qn"] == "_flow_.ImportWorker.ExecuteAsync").ToList();
        Assert.Contains(next, e => e.Source.EndsWith(".ExecuteAsync", StringComparison.Ordinal) && e.Target.EndsWith(".LoadAsync", StringComparison.Ordinal) && (int)e.Properties["order"]! == 1);
        Assert.Contains(next, e => e.Source.EndsWith(".LoadAsync", StringComparison.Ordinal) && e.Target.EndsWith(".AbortAsync", StringComparison.Ordinal)
                                   && (string?)e.Properties["condition"] == "stoppingToken.IsCancellationRequested");
        // Tras el if sin else, SaveAsync sigue tanto a AbortAsync como a LoadAsync (rama que no entra).
        Assert.Contains(next, e => e.Source.EndsWith(".AbortAsync", StringComparison.Ordinal) && e.Target.EndsWith(".SaveAsync", StringComparison.Ordinal));
        Assert.Contains(next, e => e.Source.EndsWith(".LoadAsync", StringComparison.Ordinal) && e.Target.EndsWith(".SaveAsync", StringComparison.Ordinal)
                                   && (string?)e.Properties["condition"] == "!stoppingToken.IsCancellationRequested");

        // Un handler HTTP con awaits (tras RouteEnricher) es un flujo http_endpoint.
        var http = graph.PropsOrNull("_flow_.OrdersController.Create")!;
        Assert.Equal("http_endpoint", (string?)http["flow_kind"]);
    }

    [Fact]
    public async Task Cyclomatic_is_mccabe_on_the_syntax_tree()
    {
        using var graph = await ShopAsync();
        await graph.EnrichAsync();

        // Validate: 1 + if + || + case 'A' + case 'B' + && + ?: = 7 (default no cuenta).
        Assert.Equal(7, (int)graph.Props("OrderService.Validate")["cyclomatic"]!);
        Assert.Equal(2, (int)graph.Props("OrderService.CreateAsync")["cyclomatic"]!);
    }

    [Fact]
    public async Task Reachability_marks_dead_code_suppresses_tests_and_flags_unused_registrations()
    {
        using var graph = await ShopAsync();
        await graph.EnrichAsync();

        var orphan = graph.Props("OrderService.Orphan");
        Assert.True((bool)orphan["dead_code_candidate"]!);
        Assert.False((bool)orphan["is_reachable"]!);
        var nobody = graph.Props("Orphan.Nobody");
        Assert.Equal("high", (string?)nobody["dead_code_confidence"]);

        // Vivo desde el handler HTTP (raíz HANDLES) por CALLS y DEFINES_METHOD.
        Assert.True((bool)graph.Props("OrderService.Validate")["is_reachable"]!);
        Assert.Null(graph.Props("OrderService.Validate")["dead_code_candidate"]);

        var tests = graph.Props("OrderServiceTests.OrderServiceTests");
        Assert.True((bool)tests["is_reachable"]!);
        Assert.Equal("TestConventionSuppressor", (string?)tests["dead_code_suppressed_by"]);

        // IUnusedService no se usa en ningún sitio (sin USAGE): su registro no sirve de nada.
        Assert.True((bool)graph.PropsOrNull("_di_.Shop.Core.IUnusedService->Shop.Core.UnusedService")!["unused_registration"]!);
        Assert.Null(graph.PropsOrNull("_di_.Shop.Core.IOrderService->Shop.Core.OrderService")!["unused_registration"]);
    }

    [Fact]
    public async Task Projects_come_from_the_solution_folders_with_coupling_roles_and_membership()
    {
        using var graph = await ShopAsync();
        await graph.EnrichAsync();

        Assert.Equal(3, graph.Scalar<long>("SELECT COUNT(1) FROM nodes WHERE label = 'CsProject'"));
        var api = graph.PropsOrNull("_proj_.Shop.Api")!;
        var core = graph.PropsOrNull("_proj_.Shop.Core")!;
        var tests = graph.PropsOrNull("_proj_.Shop.Tests")!;
        Assert.Equal("host", (string?)api["role"]);
        Assert.Equal("core", (string?)core["role"]);
        Assert.Equal("test", (string?)tests["role"]);
        Assert.Equal("src/Shop.Core", (string?)core["directory"]);

        // Api → Core y Tests → Core: Core tiene Ca = 2 y Ce = 0 (estable); Api, Ce = 1 (inestable).
        Assert.Equal(2, (int)core["afferent_coupling"]!);
        Assert.Equal(0, (int)core["efferent_coupling"]!);
        Assert.Equal(0.0, (double)core["instability"]!);
        Assert.Equal(1.0, (double)api["instability"]!);
        var deps = graph.Edges("PROJECT_DEPENDS_ON");
        Assert.Contains(deps, e => e.Source == "_proj_.Shop.Api" && e.Target == "_proj_.Shop.Core");
        Assert.Contains(deps, e => e.Source == "_proj_.Shop.Tests" && e.Target == "_proj_.Shop.Core");
        // Un test que depende del resto no es una violación de capas: el rol test está fuera de la jerarquía.
        Assert.All(deps, e => Assert.Null(e.Properties["layer_violation"]));

        Assert.Contains(graph.Edges("BELONGS_TO_PROJECT"), e => e.Source.EndsWith(".OrderService.Validate", StringComparison.Ordinal) && e.Target == "_proj_.Shop.Core");
        Assert.NotNull(core["cyclomatic_p90"]);
        Assert.NotNull(core["doc_coverage"]);
    }

    private const string Roles = """
        using System;
        namespace App;

        public enum Status { Open, Closed }
        public record OrderDto(int Id, string Name);
        public sealed class Customer { public string Name { get; set; } = ""; public int Age; }
        public struct Point { public int X; public int Y; }
        public class OrderException : Exception { }
        public interface IThing { void Do(); }
        public abstract class Handler { public abstract void Run(); }
        internal interface IInternal { }
        public sealed class Thing : IThing { public void Do() { } }
        internal sealed class Worker { public void Work() { } }
        public static class Helpers { public static int One() => 1; }
        public static class Program { public static void Main() { } }
        public class Plain { public void Go() { } }
        """;

    [Fact]
    public async Task Every_type_gets_a_role_visibility_and_project_and_the_project_gets_abstractness()
    {
        using var graph = new TestGraph()
            .AddProject("App", "src/App")
            .AddFile("src/App/Roles.cs", Roles);
        await graph.BuildAsync();
        await graph.EnrichAsync();

        string Role(string type) => (string)graph.Props(type)["type_role"]!;
        Assert.Equal("contract", Role("Roles.IThing"));
        Assert.Equal("contract", Role("Roles.Handler"));
        Assert.Equal("implementation", Role("Roles.Thing"));
        Assert.Equal("implementation", Role("Roles.Plain"));
        Assert.Equal("implementation", Role("Roles.Worker"));
        Assert.Equal("model", Role("Roles.Status"));
        Assert.Equal("model", Role("Roles.OrderDto"));
        Assert.Equal("model", Role("Roles.Customer"));
        Assert.Equal("model", Role("Roles.Point"));
        Assert.Equal("model", Role("Roles.OrderException"));
        Assert.Equal("infrastructure", Role("Roles.Helpers"));
        Assert.Equal("infrastructure", Role("Roles.Program"));
        // Una abstracción que no es pública no es un contrato para los demás proyectos.
        Assert.Equal("infrastructure", Role("Roles.IInternal"));

        Assert.True((bool)graph.Props("Roles.IThing")["is_public"]!);
        Assert.False((bool)graph.Props("Roles.Worker")["is_public"]!);
        Assert.Equal("App", (string?)graph.Props("Roles.Thing")["cs_project"]);

        // 3 abstracciones (IThing, Handler, IInternal) de 13 tipos; sin dependencias, la inestabilidad es 0.
        var project = graph.PropsOrNull("_proj_.App")!;
        Assert.Equal(0.23, (double)project["abstractness"]!, 2);
        Assert.Equal(0.77, (double)project["distance"]!, 2);
        Assert.Equal(2, (int)project["type_roles"]!["contract"]!);
    }

    [Fact]
    public async Task Framework_base_classes_make_a_type_infrastructure_and_source_interfaces_make_it_an_implementation()
    {
        using var graph = await ShopAsync();
        await graph.EnrichAsync();

        Assert.Equal("infrastructure", (string?)graph.Props("OrdersController.OrdersController")["type_role"]);
        Assert.Equal("infrastructure", (string?)graph.Props("ImportWorker.ImportWorker")["type_role"]);
        Assert.Equal("infrastructure", (string?)graph.Props("Startup.Startup")["type_role"]);
        Assert.Equal("implementation", (string?)graph.Props("OrderService.OrderService")["type_role"]);
        Assert.Equal("implementation", (string?)graph.Props("OrderService.UnusedService")["type_role"]);
        Assert.Equal("contract", (string?)graph.Props("OrderService.IOrderService")["type_role"]);
    }

    [Fact]
    public async Task Declared_project_references_and_packages_are_written_on_the_project_node()
    {
        using var graph = new TestGraph()
            .AddProject("App", "src/App", projectReferences: ["Lib"], packages: ["Dapper", "Serilog"])
            .AddProject("Lib", "src/Lib")
            .AddFile("src/App/A.cs", "namespace App; public sealed class A { }")
            .AddFile("src/Lib/B.cs", "namespace Lib; public sealed class B { }");
        await graph.BuildAsync();
        await graph.EnrichAsync();

        var app = graph.PropsOrNull("_proj_.App")!;
        Assert.Equal(["Lib"], app["project_references"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(["Dapper", "Serilog"], app["packages"]!.AsArray().Select(n => (string)n!));
        Assert.Null(graph.PropsOrNull("_proj_.Lib")!["project_references"]);
    }

    [Fact]
    public async Task Semantic_methods_get_modifiers_and_classes_implement_their_interfaces()
    {
        using var graph = await ShopAsync();
        await graph.EnrichAsync();

        var create = graph.Props("OrderService.CreateAsync");
        Assert.True((bool)create["is_async"]!);
        Assert.Equal("public", (string?)create["accessibility"]);
        Assert.Equal("System.Threading.Tasks.Task<int>", (string?)create["return_type_fqn"]);
        Assert.Equal("private", (string?)graph.Props("OrderService.Validate")["accessibility"]);
        Assert.Equal("Servicio de pedidos.", (string?)graph.Props("OrderService.OrderService")["docstring"]);

        var implements = graph.Edges("IMPLEMENTS");
        Assert.Contains(implements, e => e.Source.EndsWith(".OrderService.OrderService", StringComparison.Ordinal)
                                         && e.Target.EndsWith(".OrderService.IOrderService", StringComparison.Ordinal)
                                         && ((JsonArray)e.Properties["enriched_by"]!).Any(v => (string?)v == "SemanticMethodEnricher"));
        // Clases acopladas (GraphAnalytics): el controller depende del servicio.
        var controller = graph.PropsOrNull(graph.Qn("src/Shop.Api/Controllers/OrdersController.cs", "OrdersController"))!;
        Assert.True((int)controller["efferent_coupling"]! >= 1);
    }

    [Fact]
    public async Task Java_nodes_of_a_mixed_repository_keep_the_marks_the_java_enricher_wrote()
    {
        using var graph = await ShopAsync();
        var javaProps = () => new JsonObject
        {
            ["is_reachable"] = false,
            ["dead_code_candidate"] = true,
            ["dead_code_confidence"] = "high",
            ["dead_code_reason"] = "java",
            ["detected_issues"] = new JsonArray("empty-catch: catch vacío (línea 3)"),
            ["issue_rules"] = new JsonArray("empty-catch"),
            ["todo_markers"] = new JsonArray(new JsonObject { ["kind"] = "TODO", ["line"] = 2, ["text"] = "x" }),
            ["todo_count"] = 1,
            ["commented_code"] = new JsonArray(),
            ["commented_code_lines"] = 4,
            ["fan_in"] = 7,
            ["fan_out"] = 2,
            ["hotspot"] = 3.5
        };
        var javaClass = await graph.AddNodeAsync("Class", "Legacy", "proj.src.Legacy", "src/Legacy.java", javaProps());
        var javaMethod = await graph.AddNodeAsync("Method", "run", "proj.src.Legacy.run", "src/Legacy.java", javaProps());
        await graph.AddNodeAsync("Method", "other", "proj.src.Legacy.other", "src/Legacy.java", new JsonObject { ["cyclomatic"] = 9 });

        await graph.EnrichAsync();

        foreach (var qn in new[] { "proj.src.Legacy", "proj.src.Legacy.run" })
        {
            var props = graph.PropsOrNull(qn)!;
            Assert.False((bool)props["is_reachable"]!);
            Assert.True((bool)props["dead_code_candidate"]!);
            Assert.Equal("java", (string?)props["dead_code_reason"]);
            Assert.Equal("empty-catch", (string?)((JsonArray)props["issue_rules"]!)[0]);
            Assert.Equal(1, (int)props["todo_count"]!);
            Assert.Equal(4, (int)props["commented_code_lines"]!);
        }
        var method = graph.PropsOrNull("proj.src.Legacy.run")!;
        Assert.Equal(7, (int)method["fan_in"]!);
        Assert.Equal(2, (int)method["fan_out"]!);
        Assert.Equal(3.5, (double)method["hotspot"]!);
        // El enriquecedor de C# tampoco marca como alcanzable ni muerto un método Java que no escribió nadie.
        var untouched = graph.PropsOrNull("proj.src.Legacy.other")!;
        Assert.Null(untouched["is_reachable"]);
        Assert.Null(untouched["dead_code_candidate"]);
        Assert.Null(untouched["fan_in"]);
        _ = javaClass; _ = javaMethod;
    }
}
