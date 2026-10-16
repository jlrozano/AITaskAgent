using System.Text.Json;
using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tests.Enrichment;
using AITaskAgent.Configuration;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.Core.Steps;
using Xunit.Abstractions;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Las facetas que consumen lo que escriben los enriquecedores de Roslyn y javac (tanda 5 de la auditoría del 2026-09-30):
/// <c>detected_issues</c>, código muerto, campos, DI, autenticación de rutas, TODO, código comentado y documentación. Sobre
/// grafos sintéticos con las mismas propiedades que escriben los dos lenguajes.
/// </summary>
public sealed class CompilerFactsFacetsTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private async Task<ReportFacetInput> FacetsAsync(string databasePath)
    {
        var context = new PipelineContext();
        context.Set(AnalysisContextKeys.PipelineInput, new AnalysisPipelineInput
        {
            RepositoryPath = _temp.Path,
            DatabasePath = databasePath,
            OutputReportsPath = Path.Combine(_temp.Path, "reports")
        });
        context.Set(AnalysisContextKeys.Reporter, new CapturingReporter());
        return await new SqlFacetExtractorStep().ExecuteAsync(new GraphStatusResult { DatabasePath = databasePath }, context, 1, null, CancellationToken.None);
    }

    private JsonElement Json(string facet, string name)
    {
        output.WriteLine($"--- {name}\n{facet}");
        Assert.DoesNotContain("Faceta no disponible", facet);
        using var doc = JsonDocument.Parse(facet);
        return doc.RootElement.Clone();
    }

    private List<JsonElement> Rows(string facet, string name) => [.. Json(facet, name).EnumerateArray()];

    private static string? Text(JsonElement row, string property) =>
        row.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;

    private static string Props(params (string Key, object? Value)[] values)
    {
        var node = new JsonObject();
        foreach (var (key, value) in values)
            node[key] = value switch
            {
                null => null,
                string[] array => new JsonArray(array.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
                JsonNode json => json,
                _ => JsonValue.Create(value)
            };
        return node.ToJsonString();
    }

    private async Task<CbmTestGraph> GraphAsync() => await CbmTestGraph.CreateAsync(Path.Combine(_temp.Path, "codebase.db"), "demo");

    // ------------------------------------------------------------------ detected_issues

    [Fact]
    public async Task Issues_are_totalled_by_rule_and_the_shown_ones_cover_every_rule_with_a_file_and_line_citation()
    {
        using var g = await GraphAsync();
        // 30 métodos con catch vacío (los de mayor hotspot son los últimos) y uno de cada otra regla.
        for (var i = 1; i <= 30; i++)
            await g.NodeAsync("Method", $"m{i}", $"demo.A{i}.m{i}", $"src/A{i}.cs", 10, 20,
                Props(("hotspot", i), ("detected_issues", new[] { $"empty-catch: catch vacío (línea {10 + i})" }), ("issue_rules", new[] { "empty-catch" })));
        await g.NodeAsync("Method", "deref", "demo.B.deref", "src/B.cs", 3, 9,
            Props(("detected_issues", new[] { "null-deref: posible desreferencia nula de 'x' (línea 5)", "obsolete-api: usa X.Old (línea 7)" }),
                ("issue_rules", new[] { "null-deref", "obsolete-api" })));
        await g.NodeAsync("Method", "old", "demo.C.old", "src/C.java", 1, 4,
            Props(("detected_issues", new[] { "obsolete-api: usa Date.getYear (línea 2)" }), ("issue_rules", new[] { "obsolete-api" })));

        var facets = await FacetsAsync(g.DatabasePath);

        var summary = Json(facets.DetectedIssuesSummary, "detected_issues_summary");
        Assert.Equal(33, summary.GetProperty("total_findings").GetInt32());
        Assert.Equal(32, summary.GetProperty("nodes_with_findings").GetInt32());
        var byRule = summary.GetProperty("by_rule").EnumerateArray().ToDictionary(r => Text(r, "rule")!, r => (r.GetProperty("findings").GetInt32(), r.GetProperty("nodes").GetInt32()));
        Assert.Equal((30, 30), byRule["empty-catch"]);
        Assert.Equal((1, 1), byRule["null-deref"]);
        Assert.Equal((2, 2), byRule["obsolete-api"]);
        // Las reglas graves antes que obsolete-api en el resumen.
        Assert.True(summary.GetProperty("by_rule").EnumerateArray().Select(r => Text(r, "rule")).ToList() is { } order
                    && order.IndexOf("null-deref") < order.IndexOf("obsolete-api"));

        var shown = Rows(facets.SuspectPatterns, "suspect_patterns");
        // Con cupo por regla: no se cuelan los 30 catch vacíos, pero salen los de mayor hotspot y todas las reglas.
        Assert.True(shown.Count < 33);
        Assert.Contains(shown, r => Text(r, "rule") == "null-deref");
        Assert.Contains(shown, r => Text(r, "rule") == "obsolete-api");
        var emptyCatch = shown.Where(r => Text(r, "rule") == "empty-catch").ToList();
        Assert.Equal(8, emptyCatch.Count);
        Assert.Equal("src/A30.cs:40", Text(emptyCatch[0], "cite"));
        Assert.Equal("src/A23.cs:33", Text(emptyCatch[^1], "cite"));
        Assert.All(shown, r => Assert.Matches(@"^.+:\d+$", Text(r, "cite")!));
        Assert.Equal("src/B.cs:5", Text(shown.Single(r => Text(r, "rule") == "null-deref"), "cite"));
    }

    [Fact]
    public async Task Critical_hotspots_put_the_nodes_with_serious_issues_before_the_plain_hotspots()
    {
        using var g = await GraphAsync();
        for (var i = 1; i <= 40; i++)
            await g.NodeAsync("Method", $"h{i}", $"demo.H{i}.h{i}", $"src/H{i}.cs", 1, 9, Props(("hotspot", 100 + i)));
        await g.NodeAsync("Method", "leaky", "demo.L.leaky", "src/L.cs", 5, 15,
            Props(("hotspot", 0.5), ("detected_issues", new[] { "undisposed-resource: 'conn' no se cierra (línea 8)" }), ("issue_rules", new[] { "undisposed-resource" })));
        await g.NodeAsync("Method", "legacy", "demo.L.legacy", "src/L.cs", 20, 25,
            Props(("hotspot", 0.2), ("detected_issues", new[] { "obsolete-api: usa X (línea 21)" }), ("issue_rules", new[] { "obsolete-api" })));

        var facets = await FacetsAsync(g.DatabasePath);

        var rows = Rows(facets.CriticalHotspotsExceptions, "critical_hotspots_exceptions");
        Assert.True(rows.Count <= 30);
        Assert.Equal("demo.L.leaky", Text(rows[0], "qualified_name"));
        Assert.Equal("undisposed-resource: 'conn' no se cierra (línea 8)", rows[0].GetProperty("detected_issues")[0].GetString());
        Assert.Equal(1, rows[0].GetProperty("issues_total").GetInt32());
        // Los hotspots puros siguen entrando, por hotspot descendente, y el de obsolete-api no desplaza al más caliente.
        Assert.Contains(rows, r => Text(r, "qualified_name") == "demo.H40.h40");
        Assert.Contains(rows, r => Text(r, "qualified_name") == "demo.L.legacy");
    }

    [Fact]
    public async Task Critical_hotspots_count_throws_only_when_it_is_a_non_empty_list()
    {
        using var g = await GraphAsync();
        // Las pasadas anteriores de ThrowsEnricher escribían throws: [] en todo método con cuerpo; json_extract da '[]', no NULL.
        for (var i = 1; i <= 5; i++)
            await g.NodeAsync("Method", $"quiet{i}", $"demo.Q.quiet{i}", "src/Q.cs", i * 10, i * 10 + 5, Props(("throws", Array.Empty<string>())));
        await g.NodeAsync("Method", "scalar", "demo.Q.scalar", "src/Q.cs", 80, 85, Props(("throws", "System.Exception")));
        await g.NodeAsync("Method", "thrower", "demo.T.thrower", "src/T.cs", 1, 9, Props(("throws", new[] { "System.InvalidOperationException" })));
        await g.NodeAsync("Method", "hot", "demo.H.hot", "src/H.cs", 1, 9, Props(("hotspot", 9)));

        var facets = await FacetsAsync(g.DatabasePath);

        var names = Rows(facets.CriticalHotspotsExceptions, "critical_hotspots_exceptions").Select(r => Text(r, "qualified_name")).ToList();
        Assert.Equal(["demo.H.hot", "demo.T.thrower"], names);
    }

    // ------------------------------------------------------------------ código muerto

    [Fact]
    public async Task Dead_code_is_deduplicated_ordered_by_confidence_and_split_into_unused_private_methods()
    {
        using var g = await GraphAsync();
        const string reasonPrivate = "método privado sin ninguna referencia desde código vivo de la solución";
        string Dead(string confidence, string reason = "r", string? accessibility = null) =>
            Props(("dead_code_candidate", true), ("dead_code_confidence", confidence), ("dead_code_reason", reason), ("accessibility", accessibility));

        // Clase muerta con dos métodos muertos: solo la clase se lista.
        var deadClass = await g.NodeAsync("Class", "Huerfana", "demo.Huerfana", "src/Huerfana.cs", 1, 50, Dead("medium"));
        foreach (var name in new[] { "a", "b" })
        {
            var m = await g.NodeAsync("Method", name, $"demo.Huerfana.{name}", "src/Huerfana.cs", 5, 8, Dead("medium", accessibility: "public"));
            await g.EdgeAsync(deadClass, m, "DEFINES_METHOD");
        }
        // Clase viva con un método privado sin uso y un método de baja confianza.
        var live = await g.NodeAsync("Class", "Viva", "demo.Viva", "src/Viva.cs", 1, 60, Props(("is_reachable", true)));
        var priv = await g.NodeAsync("Method", "helper", "demo.Viva.helper", "src/Viva.cs", 10, 12, Dead("high", reasonPrivate, "private"));
        var low = await g.NodeAsync("Method", "virt", "demo.Viva.virt", "src/Viva.cs", 20, 22, Dead("low", "miembro polimórfico", "public"));
        var high = await g.NodeAsync("Method", "interno", "demo.Viva.interno", "src/Viva.cs", 30, 35, Dead("high", "método internal en un componente sin conexión", "internal"));
        // Un candidato que sobrescribe un contrato: no es código muerto.
        var contract = await g.NodeAsync("Method", "run", "demo.Iface.run", "src/Iface.cs", 3, 3);
        var impl = await g.NodeAsync("Method", "run", "demo.Viva.run", "src/Viva.cs", 40, 45, Dead("low", "override", "public"));
        await g.EdgeAsync(impl, contract, "OVERRIDES");
        foreach (var m in new[] { priv, low, high, impl })
            await g.EdgeAsync(live, m, "DEFINES_METHOD");

        var facets = await FacetsAsync(g.DatabasePath);

        var listed = Rows(facets.DeadCodeCandidates, "dead_code_candidates");
        Assert.Equal(["demo.Viva.interno", "demo.Huerfana", "demo.Viva.virt"], listed.Select(r => Text(r, "qualified_name")));
        Assert.Equal("2", Text(listed[1], "dead_members"));
        Assert.Equal(["demo.Viva.helper"], Rows(facets.DeadPrivateMethods, "dead_private_methods").Select(r => Text(r, "qualified_name")));

        var summary = Json(facets.DeadCodeSummary, "dead_code_summary");
        Assert.Equal(7, summary.GetProperty("total_candidates").GetInt32());
        Assert.Equal(2, summary.GetProperty("members_of_candidate_types").GetInt32());
        Assert.Equal(1, summary.GetProperty("private_unused_methods").GetInt32());
        Assert.Equal(1, summary.GetProperty("overriding_members_excluded").GetInt32());
        Assert.Equal(3, summary.GetProperty("listed_candidates").GetInt32());
        var confidence = summary.GetProperty("by_confidence");
        Assert.Equal((2, 3, 2), (confidence.GetProperty("high").GetInt32(), confidence.GetProperty("medium").GetInt32(), confidence.GetProperty("low").GetInt32()));
    }

    // ------------------------------------------------------------------ campos

    [Fact]
    public async Task Unread_fields_work_with_csharp_fields_and_write_only_fields_are_listed_apart()
    {
        using var g = await GraphAsync();
        string Field(string name, bool isStatic = false, bool isFinal = false, string accessibility = "private") =>
            Props(("field_name", name), ("type_fqn", "int"), ("accessibility", accessibility), ("is_static", isStatic), ("is_final", isFinal),
                ("is_readonly", isFinal && !isStatic), ("is_const", isStatic && isFinal));
        var method = await g.NodeAsync("Method", "Run", "demo.Svc.Run", "src/Svc.cs", 20, 30);
        var ctor = await g.NodeAsync("Method", "Svc", "demo.Svc.Svc", "src/Svc.cs", 10, 14);
        var onlyWritten = await g.NodeAsync("Field", "_logger", "demo.Svc._logger", "src/Svc.cs", 4, 4, Field("_logger", isFinal: true));
        var untouched = await g.NodeAsync("Field", "_never", "demo.Svc._never", "src/Svc.cs", 5, 5, Field("_never"));
        var read = await g.NodeAsync("Field", "_count", "demo.Svc._count", "src/Svc.cs", 6, 6, Field("_count"));
        await g.NodeAsync("Field", "Max", "demo.Svc.Max", "src/Svc.cs", 7, 7, Field("Max", isStatic: true, isFinal: true));
        await g.NodeAsync("Field", "Shared", "demo.Svc.Shared", "src/Svc.cs", 8, 8, Field("Shared", accessibility: "public"));
        const string access = "{\"lines\":[12],\"count\":1}";
        await g.EdgeAsync(ctor, onlyWritten, "WRITES", access);
        await g.EdgeAsync(ctor, read, "WRITES", access);
        await g.EdgeAsync(method, read, "READS", access);

        var facets = await FacetsAsync(g.DatabasePath);

        var unread = Rows(facets.UnreadFields, "unread_fields");
        Assert.Equal(["_logger", "_never"], unread.Select(r => Text(r, "field_name")));
        var summary = Json(facets.UnreadFieldsSummary, "unread_fields_summary");
        Assert.Equal((2, 1, 1), (summary.GetProperty("total_unread").GetInt32(), summary.GetProperty("never_touched").GetInt32(), summary.GetProperty("write_only").GetInt32()));
        var writeOnly = Assert.Single(Rows(facets.WriteOnlyFields, "write_only_fields"));
        Assert.Equal(("_logger", "src/Svc.cs", "4", "1"), (Text(writeOnly, "field_name"), Text(writeOnly, "file"), Text(writeOnly, "start_line"), Text(writeOnly, "writes")));
        _ = untouched;
    }

    // ------------------------------------------------------------------ DI

    [Fact]
    public async Task Di_facets_use_consumer_count_and_report_captive_dependencies()
    {
        using var g = await GraphAsync();
        string Registration(string lifetime, int? consumers, bool? unused = null, bool framework = false) =>
            Props(("lifetime", lifetime), ("service_type", "Shop.I"), ("implementation_type", "Shop.C"), ("consumer_count", consumers),
                ("unused_registration", unused), ("consumed_by_framework", framework ? true : null), ("fan_in", 0));
        await g.NodeAsync("DiRegistration", "A", "_di_.IA->A", "src/Startup.cs", 10, 10, Registration("Scoped", 0, unused: true));
        await g.NodeAsync("DiRegistration", "B", "_di_.IB->B", "src/Startup.cs", 11, 11, Registration("Scoped", 3));
        await g.NodeAsync("DiRegistration", "C", "_di_.IC->C", "src/Startup.cs", 12, 12, Registration("Singleton", null, framework: true));
        await g.NodeAsync("DiRegistration", "D", "_di_.ID->D", "src/Startup.cs", 13, 13, Registration("Transient", null));
        await g.NodeAsync("Method", "Cache", "demo.Cache.Cache", "src/Cache.cs", 8, 12,
            Props(("detected_issues", new[] { "captive-dependency: 'Cache' (Singleton, registrado en src/Startup.cs:14) depende de 'Repo' (Scoped, registrado en src/Startup.cs:10) (línea 9)" }),
                ("issue_rules", new[] { "captive-dependency" })));

        var facets = await FacetsAsync(g.DatabasePath);

        // Solo el que Roslyn marca sin consumidores y el de fan_in 0 sin dato de consumidores; ni el de 3 consumidores ni el del framework.
        var unusedRows = Rows(facets.UnusedDiRegistrations, "unused_di_registrations");
        Assert.Equal(["_di_.IA->A", "_di_.ID->D"], unusedRows.Select(r => Text(r, "qualified_name")));
        Assert.Equal("0", Text(unusedRows[0], "consumer_count"));

        var captive = Assert.Single(Rows(facets.CaptiveDependencies, "captive_dependencies"));
        Assert.Equal("src/Cache.cs:9", Text(captive, "cite"));
        Assert.Contains("depende de 'Repo'", Text(captive, "text"));

        var summary = Json(facets.DiSummary, "di_summary");
        Assert.Equal(4, summary.GetProperty("registrations").GetInt32());
        Assert.Equal(1, summary.GetProperty("unused_registrations").GetInt32());
        Assert.Equal(1, summary.GetProperty("consumed_by_framework").GetInt32());
        Assert.Equal(1, summary.GetProperty("captive_dependencies").GetInt32());
        Assert.Equal(2, summary.GetProperty("by_lifetime").GetProperty("Scoped").GetInt32());
        // La vida útil sale de la propiedad lifetime que escribe DiEnricher.
        Assert.Equal("Singleton", Text(Rows(facets.DiLifetimesMapping, "di_lifetimes_mapping").Single(r => Text(r, "qualified_name") == "_di_.IC->C"), "di_lifetime"));
    }

    // ------------------------------------------------------------------ rutas

    [Fact]
    public async Task Routes_without_declared_auth_are_told_apart_from_explicit_allow_anonymous()
    {
        using var g = await GraphAsync();
        await g.NodeAsync("Route", "/sec", "__route__GET__/sec", "src/Api.cs", 1, 1, Props(("route_path", "/sec"), ("http_verb", "GET"), ("auth_required", true)));
        await g.NodeAsync("Route", "/inferida", "__route__GET__/inferida", "src/Api.cs", 2, 2,
            Props(("route_path", "/inferida"), ("http_verb", "GET"), ("auth_required", false), ("auth_inferred", true)));
        await g.NodeAsync("Route", "/anon", "__route__GET__/anon", "src/Api.cs", 3, 3,
            Props(("route_path", "/anon"), ("http_verb", "GET"), ("auth_required", false)));
        await g.NodeAsync("Route", "/struts", "__route__ANY__/struts", "WEB-INF/struts-config.xml", 4, 4, Props(("route_path", "/struts"), ("http_method", "ANY")));

        var facets = await FacetsAsync(g.DatabasePath);

        var summary = Json(facets.RouteAuthSummary, "route_auth_summary");
        Assert.Equal((4, 1, 1, 1, 1), (summary.GetProperty("total_routes").GetInt32(), summary.GetProperty("authenticated").GetInt32(),
            summary.GetProperty("without_declared_auth").GetInt32(), summary.GetProperty("explicit_allow_anonymous").GetInt32(), summary.GetProperty("unknown").GetInt32()));
        var unvalidated = Rows(facets.UnvalidatedRoutes, "unvalidated_routes");
        Assert.Equal(["__route__GET__/inferida", "__route__GET__/anon"], unvalidated.Select(r => Text(r, "qualified_name")));
        Assert.Equal("1", Text(unvalidated[0], "auth_inferred"));
        Assert.Contains("no declara", Text(unvalidated[0], "auth_basis"));
        Assert.Contains("AllowAnonymous", Text(unvalidated[1], "auth_basis"));
        var matrix = Rows(facets.RoutesSecurityMatrix, "routes_security_matrix");
        Assert.Equal("1", Text(matrix.Single(r => Text(r, "qualified_name") == "__route__GET__/inferida"), "auth_inferred"));
    }

    // ------------------------------------------------------------------ deuda

    [Fact]
    public async Task Todo_markers_and_commented_code_are_totalled_with_the_top_files_and_citations()
    {
        using var g = await GraphAsync();
        string Todos(params (string Kind, int Line, string Text)[] markers) =>
            Props(("todo_markers", new JsonArray(markers.Select(m => (JsonNode?)new JsonObject { ["kind"] = m.Kind, ["line"] = m.Line, ["text"] = m.Text }).ToArray())),
                ("todo_count", markers.Length));
        await g.NodeAsync("Method", "a", "demo.A.a", "src/A.cs", 1, 30, Todos(("TODO", 5, "revisar"), ("FIXME", 9, "rompe con null")));
        await g.NodeAsync("File", "B.java", "demo.B.java", "src/B.java", 1, 1, Todos(("TODO", 3, "limpiar"), ("HACK", 4, "parche"), ("TODO", 20, "otro")));
        await g.NodeAsync("Method", "c", "demo.C.c", "src/C.cs", 1, 9,
            Props(("commented_code", new JsonArray(new JsonObject { ["start_line"] = 4, ["end_line"] = 9, ["lines"] = 6, ["preview"] = "var x = 1;" })), ("commented_code_lines", 6)));
        await g.NodeAsync("File", "D.java", "demo.D.java", "src/D.java", 1, 1,
            Props(("commented_code", new JsonArray(new JsonObject { ["start_line"] = 40, ["end_line"] = 42, ["lines"] = 3, ["preview"] = "foo();" })), ("commented_code_lines", 3)));

        var facets = await FacetsAsync(g.DatabasePath);

        var todos = Json(facets.TodoMarkers, "todo_markers");
        Assert.Equal((5, 2), (todos.GetProperty("total_markers").GetInt32(), todos.GetProperty("files_with_markers").GetInt32()));
        Assert.Equal(3, todos.GetProperty("by_kind").GetProperty("TODO").GetInt32());
        Assert.Equal("src/B.java", Text(todos.GetProperty("top_files")[0], "file"));
        Assert.Equal(3, todos.GetProperty("top_files")[0].GetProperty("markers").GetInt32());
        // FIXME y HACK antes que TODO.
        Assert.Equal(["src/A.cs:9", "src/B.java:4"], todos.GetProperty("top").EnumerateArray().Take(2).Select(r => Text(r, "cite")));

        var code = Json(facets.CommentedCode, "commented_code");
        Assert.Equal((2, 9, 2), (code.GetProperty("blocks").GetInt32(), code.GetProperty("lines").GetInt32(), code.GetProperty("files_with_blocks").GetInt32()));
        Assert.Equal("src/C.cs:4", Text(code.GetProperty("top")[0], "cite"));
        Assert.Equal("var x = 1;", Text(code.GetProperty("top")[0], "preview"));
    }

    // ------------------------------------------------------------------ constantes resueltas

    private static string Secrets(params (string Kind, string? Name, int Line, string Masked)[] secrets) =>
        Props(("hardcoded_secrets", new JsonArray(secrets.Select(c => (JsonNode?)new JsonObject
            { ["kind"] = c.Kind, ["name"] = c.Name, ["line"] = c.Line, ["preview_masked"] = c.Masked }).ToArray())));

    [Fact]
    public async Task Resolved_secrets_are_totalled_by_kind_with_citations_and_only_the_masked_value()
    {
        using var g = await GraphAsync();
        await g.NodeAsync("Field", "PASSWORD", "demo.Db.PASSWORD", "src/Db.java", 11, 11, Secrets(("identifier", "PASSWORD", 11, "Or*** (11 caracteres)")));
        // El tipo que contiene el campo recoge el mismo hallazgo (misma línea y valor): cuenta una vez.
        await g.NodeAsync("Class", "Db", "demo.Db", "src/Db.java", 1, 40, Secrets(("identifier", "PASSWORD", 11, "Or*** (11 caracteres)")));
        await g.NodeAsync("Method", "open", "demo.Db.open", "src/Db.java", 14, 18,
            Secrets(("call-argument", "getConnection", 15, "ar*** (10 caracteres)"), ("connection-string", "CONNECTION", 16, "Su*** (11 caracteres)")));
        await g.NodeAsync("Method", "clean", "demo.Db.clean", "src/Db.java", 20, 22, Props(("cyclomatic", 1)));

        var facets = await FacetsAsync(g.DatabasePath);

        var secrets = Json(facets.ResolvedSecrets, "resolved_secrets");
        Assert.Equal((3, 1), (secrets.GetProperty("total_secrets").GetInt32(), secrets.GetProperty("files_with_secrets").GetInt32()));
        Assert.Equal(1, secrets.GetProperty("by_kind").GetProperty("connection-string").GetInt32());
        // Las cadenas de conexión primero, y cada una con su cita ruta:línea.
        var top = secrets.GetProperty("top");
        Assert.Equal(["src/Db.java:16", "src/Db.java:11", "src/Db.java:15"], top.EnumerateArray().Select(r => Text(r, "cite")));
        Assert.Equal("Or*** (11 caracteres)", Text(top[1], "preview_masked"));
        Assert.Contains("Nunca reproduzcas el valor", Text(secrets, "note"));
        Assert.DoesNotContain("Ord3rs", facets.ResolvedSecrets);
    }

    [Fact]
    public async Task Without_resolved_secrets_or_targets_the_facets_say_zero_and_do_not_fail()
    {
        using var g = await GraphAsync();
        await g.NodeAsync("Method", "a", "demo.A.a", "src/A.cs", 1, 9, Props(("cyclomatic", 1)));

        var facets = await FacetsAsync(g.DatabasePath);

        Assert.Equal(0, Json(facets.ResolvedSecrets, "resolved_secrets").GetProperty("total_secrets").GetInt32());
        var targets = Json(facets.ReflectionTargets, "reflection_targets");
        Assert.Equal((0, 0), (targets.GetProperty("total_targets").GetInt32(), targets.GetProperty("classes_kept_alive").GetInt32()));
    }

    [Fact]
    public async Task Reflection_targets_list_the_resolved_names_with_the_calling_method_and_the_classes_kept_alive()
    {
        using var g = await GraphAsync();
        string Targets(params string[] names) => Props(("reflection_targets", names));
        await g.NodeAsync("Method", "create", "demo.AppFactory.create", "src/AppFactory.java", 15, 25, Targets("com.acme.export.*Impl"));
        await g.NodeAsync("Method", "load", "demo.Loader.load", "src/Loader.java", 5, 9, Targets("com.acme.util.Fixed", "com.acme.export.*Impl"));
        await g.NodeAsync("Class", "CsvExporterImpl", "demo.CsvExporterImpl", "src/export/CsvExporterImpl.java", 3, 30,
            Props(("is_reachable", true), ("dead_code_suppressed_by", "reflection-target")));
        await g.NodeAsync("Class", "EmailNotifier", "demo.EmailNotifier", "src/EmailNotifier.cs", 3, 30,
            Props(("is_reachable", true), ("dead_code_suppressed_by", "ReflectionTargetSuppressor")));
        await g.NodeAsync("Class", "Plain", "demo.Plain", "src/Plain.java", 3, 30, Props(("is_reachable", true), ("dead_code_suppressed_by", "class-literal")));

        var facets = await FacetsAsync(g.DatabasePath);

        var targets = Json(facets.ReflectionTargets, "reflection_targets");
        Assert.Equal((2, 1, 1), (targets.GetProperty("total_targets").GetInt32(), targets.GetProperty("exact").GetInt32(), targets.GetProperty("patterns").GetInt32()));
        // Los patrones primero, con el método que hace la llamada.
        var first = targets.GetProperty("top")[0];
        Assert.Equal(("com.acme.export.*Impl", "pattern", "src/AppFactory.java:15"), (Text(first, "target"), Text(first, "kind"), Text(first, "cite")));
        Assert.Equal(2, targets.GetProperty("classes_kept_alive").GetInt32());
        Assert.Equal(["CsvExporterImpl", "EmailNotifier"], targets.GetProperty("kept_alive").EnumerateArray().Select(k => Text(k, "name")).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Doc_coverage_counts_xml_and_javadoc_and_not_the_plain_comments_cbm_stored_as_docstring()
    {
        using var g = await GraphAsync();
        await g.NodeAsync("Method", "xml", "demo.A.xml", "src/A.cs", 1, 3, Props(("docstring", "Resumen."), ("docstring_source", "xml")));
        await g.NodeAsync("Method", "jd", "demo.B.jd", "src/B.java", 1, 3, Props(("docstring", "Resumen."), ("docstring_source", "javadoc")));
        await g.NodeAsync("Method", "plain", "demo.A.plain", "src/A.cs", 5, 7, Props(("docstring", "// nota"), ("docstring_source", "comment")));
        await g.NodeAsync("Method", "cbm", "demo.B.cbm", "src/B.java", 5, 7, Props(("docstring", "// anotación de cbm")));
        await g.NodeAsync("Method", "none", "demo.A.none", "src/A.cs", 9, 11);
        // Un lenguaje sin enriquecedor propio conserva el docstring de cbm como documentación.
        await g.NodeAsync("Method", "py", "demo.m.py", "src/m.py", 1, 3, Props(("docstring", "Docstring de Python.")));

        var facets = await FacetsAsync(g.DatabasePath);

        var method = Rows(facets.DocCoverageMetrics, "doc_coverage_metrics").Single(r => Text(r, "label") == "Method");
        Assert.Equal(("6", "3", "1", "1"), (Text(method, "total"), Text(method, "documented"), Text(method, "documented_as_comment"), Text(method, "cbm_docstring_unverified")));
    }

    // ------------------------------------------------------------------ duplicación

    [Fact]
    public async Task Duplicate_candidates_come_from_similar_to_edges_with_both_locations_and_skip_tiny_or_weak_pairs()
    {
        using var g = await GraphAsync();
        var a = await g.NodeAsync("Method", "format", "demo.A.format", "src/A.cs", 10, 20, Props(("lines", 11)));
        var b = await g.NodeAsync("Method", "format", "demo.B.format", "src/B.cs", 40, 50, Props(("lines", 11)));
        var smallA = await g.NodeAsync("Method", "getX", "demo.A.getX", "src/A.cs", 30, 32, Props(("lines", 3)));
        var smallB = await g.NodeAsync("Method", "getY", "demo.B.getY", "src/B.cs", 60, 62, Props(("lines", 3)));
        var weakA = await g.NodeAsync("Method", "w1", "demo.A.w1", "src/A.cs", 70, 90, Props(("lines", 21)));
        var weakB = await g.NodeAsync("Method", "w2", "demo.C.w2", "src/C.cs", 5, 25, Props(("lines", 21)));
        await g.EdgeAsync(a, b, "SIMILAR_TO", "{\"jaccard\":1.000,\"same_file\":false}");
        await g.EdgeAsync(smallA, smallB, "SIMILAR_TO", "{\"jaccard\":1.000,\"same_file\":false}");
        await g.EdgeAsync(weakA, weakB, "SIMILAR_TO", "{\"jaccard\":0.600,\"same_file\":false}");

        var facets = await FacetsAsync(g.DatabasePath);

        var pair = Assert.Single(Rows(facets.DuplicateCandidates, "duplicate_candidates"));
        Assert.Equal(("format", "src/A.cs", "10", "20"), (Text(pair, "name_a"), Text(pair, "file_a"), Text(pair, "start_line_a"), Text(pair, "end_line_a")));
        Assert.Equal(("format", "src/B.cs", "40", "50"), (Text(pair, "name_b"), Text(pair, "file_b"), Text(pair, "start_line_b"), Text(pair, "end_line_b")));
        Assert.Equal("1", Text(pair, "similarity"));
        var summary = Json(facets.DuplicateSummary, "duplicate_summary");
        Assert.Equal(1, summary.GetProperty("total_pairs").GetInt32());
    }

    [Fact]
    public async Task Without_similar_to_edges_the_duplicate_facets_are_empty_not_an_error()
    {
        using var g = await GraphAsync();
        await g.NodeAsync("Method", "m", "demo.A.m", "src/A.cs", 1, 9);

        var facets = await FacetsAsync(g.DatabasePath);

        Assert.Empty(Rows(facets.DuplicateCandidates, "duplicate_candidates"));
        Assert.Equal(0, Json(facets.DuplicateSummary, "duplicate_summary").GetProperty("total_pairs").GetInt32());
    }

    // ------------------------------------------------------------------ plantillas

    [Fact]
    public void The_templates_receive_the_new_facets_and_say_they_are_candidates_to_verify()
    {
        var templates = AnalysisPipelineFactory.CreateDefaultTemplateProvider();
        var facets = new ReportFacetInput
        {
            DetectedIssuesSummary = "MARCA-RESUMEN-HALLAZGOS",
            SuspectPatterns = "MARCA-SOSPECHOSOS",
            DeadCodeSummary = "MARCA-RESUMEN-MUERTO",
            DeadPrivateMethods = "MARCA-PRIVADOS",
            UnreadFieldsSummary = "MARCA-RESUMEN-CAMPOS",
            WriteOnlyFields = "MARCA-SOLO-ESCRITOS",
            CaptiveDependencies = "MARCA-CAUTIVAS",
            DiSummary = "MARCA-RESUMEN-DI",
            RouteAuthSummary = "MARCA-RESUMEN-AUTH",
            TodoMarkers = "MARCA-TODO",
            CommentedCode = "MARCA-COMENTADO",
            DuplicateCandidates = "MARCA-DUPLICADOS",
            DuplicateSummary = "MARCA-RESUMEN-DUPLICADOS",
            ResolvedSecrets = "MARCA-SECRETOS-RESUELTOS",
            ReflectionTargets = "MARCA-OBJETIVOS-REFLEXION"
        };

        string Render(string name) => templates.Render(name, facets)!;

        var errors = Render("report_errores_prompt");
        foreach (var mark in new[] { "MARCA-RESUMEN-HALLAZGOS", "MARCA-SOSPECHOSOS", "MARCA-RESUMEN-AUTH" })
            Assert.Contains(mark, errors);
        Assert.Contains("ruta:línea", errors);

        var dead = Render("report_codigomuerto_prompt");
        foreach (var mark in new[] { "MARCA-RESUMEN-MUERTO", "MARCA-PRIVADOS", "MARCA-RESUMEN-CAMPOS", "MARCA-SOLO-ESCRITOS" })
            Assert.Contains(mark, dead);

        var di = Render("report_di_prompt");
        foreach (var mark in new[] { "MARCA-RESUMEN-DI", "MARCA-CAUTIVAS" })
            Assert.Contains(mark, di);

        var apis = Render("report_apis_prompt");
        Assert.Contains("MARCA-RESUMEN-AUTH", apis);
        Assert.Contains("MARCA-SECRETOS-RESUELTOS", apis);
        Assert.Contains("MARCA-SECRETOS-RESUELTOS", errors);
        Assert.Contains("MARCA-OBJETIVOS-REFLEXION", dead);

        var debt = Render("report_deuda_prompt");
        foreach (var mark in new[] { "MARCA-TODO", "MARCA-COMENTADO" })
            Assert.Contains(mark, debt);

        var smells = Render("report_codesmells_prompt");
        foreach (var mark in new[] { "MARCA-RESUMEN-CAMPOS", "MARCA-SOLO-ESCRITOS", "MARCA-DUPLICADOS", "MARCA-RESUMEN-DUPLICADOS" })
            Assert.Contains(mark, smells);
        // Cada componente examinado, también el cohesivo, es un hallazgo con identificador y cita con rango de líneas.
        Assert.Contains("02-CAT-nnn", smells);
    }
}
