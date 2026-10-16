using System.Text.Json;
using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Enrichment.Java;
using CodebaseAnalysis.Core.Support;

namespace CodebaseAnalysis.Core.Tests.Enrichment;

/// <summary>
/// <see cref="JavaReachabilityWriter"/> sobre un grafo hecho a mano con las propiedades que deja <see cref="JavaGraphWriter"/>
/// (<c>fqn</c>, <c>kind</c>, <c>owner_fqn</c>, <c>is_override</c>…): raíces (<c>main</c>, anotaciones, Struts, XML y literales de
/// configuración, JSP), propagación, métodos privados sin uso, dudosos por reflexión o por librería y marcas anteriores.
/// </summary>
public class JavaReachabilityWriterTests : IDisposable
{
    private const string Project = "R";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static string Path(string name) => $"src/r/{name}.java";

    private static string ClassQn(string name) => $"{Project}.src.r.{name}.{name}";

    private static string MethodQn(string owner, string name) => $"{Project}.src.r.{owner}.{name}";

    private static string Props(Dictionary<string, object?> values) => JsonSerializer.Serialize(values);

    private static string TypeProps(string fqn, string kind = "class", string access = "public", string[]? annotations = null, string? super = null,
        string[]? interfaces = null, string? outer = null) => Props(new()
    {
        ["fqn"] = fqn, ["kind"] = kind, ["accessibility"] = access, ["annotations"] = annotations ?? [], ["super_fqn"] = super,
        ["interfaces_fqn"] = interfaces ?? [], ["outer_fqn"] = outer, ["is_abstract"] = kind == "interface"
    });

    private static string MethodProps(string owner, string access = "public", bool isStatic = false, string[]? paramTypes = null,
        string[]? annotations = null, bool isOverride = false, bool ctor = false) => Props(new()
    {
        ["owner_fqn"] = owner, ["accessibility"] = access, ["is_static"] = isStatic, ["param_types"] = paramTypes ?? [],
        ["annotations"] = annotations ?? [], ["is_override"] = isOverride, ["is_constructor"] = ctor, ["is_abstract"] = false
    });

    /// <summary>Un tipo de cbm con su QN de fichero y las propiedades que escribe <see cref="JavaGraphWriter"/>.</summary>
    private static async Task<long> TypeAsync(CbmTestGraph g, string name, string props)
    {
        var label = JsonNode.Parse(props)!["kind"]!.GetValue<string>() switch { "interface" => "Interface", "enum" => "Enum", _ => "Class" };
        return await g.NodeAsync(label, name, ClassQn(name), Path(name), 3, 40, props);
    }

    private static Task<long> MethodAsync(CbmTestGraph g, string owner, string name, string props) =>
        g.NodeAsync("Method", name, MethodQn(owner, name), Path(owner), 5, 7, props);

    private static async Task<JsonObject> PropsAsync(CbmTestGraph g, string qn) =>
        JsonNode.Parse((await g.TextAsync("SELECT properties FROM nodes WHERE qualified_name = @qn", ("@qn", qn)))!)!.AsObject();

    private static bool? Reachable(JsonObject p) => p["is_reachable"]?.GetValue<bool>();

    private static (string? Confidence, string? Reason) Dead(JsonObject p) =>
        (p["dead_code_confidence"]?.GetValue<string>(), p["dead_code_reason"]?.GetValue<string>());

    private sealed class Built
    {
        public required CbmTestGraph Graph { get; init; }
        public required RepositoryScanResult Scan { get; init; }
        public required DynamicInstantiationScan Reflection { get; init; }
    }

    private async Task<Built> BuildAsync()
    {
        var repo = System.IO.Path.Combine(_temp.Path, "repo");
        Directory.CreateDirectory(System.IO.Path.Combine(repo, "WebContent"));
        File.WriteAllText(System.IO.Path.Combine(repo, "WebContent", "a.jsp"), """
            <%@ page import="r.JspUsed, r.web.*, java.util.*" %>
            <% JspWild w = new JspWild(); %>
            """);
        File.WriteAllText(System.IO.Path.Combine(repo, "WebContent", "b.jsp"), "<p>sin clases</p>");
        var scan = RepositoryScan.Scan(repo, []);

        var g = await CbmTestGraph.CreateAsync(System.IO.Path.Combine(_temp.Path, "codebase.db"), Project);
        var ids = new Dictionary<string, long>();

        async Task Type(string name, string props) => ids[name] = await TypeAsync(g, name, props);
        async Task Method(string owner, string name, string props) => ids[$"{owner}#{name}"] = await MethodAsync(g, owner, name, props);

        // Raíces: main, anotación, Struts (action, su superclase y su formulario), XML, literal y JSP.
        await Type("Main", TypeProps("r.Main"));
        await Method("Main", "main", MethodProps("r.Main", isStatic: true, paramTypes: ["java.lang.String[]"]));
        await Type("Service", TypeProps("r.Service"));
        await Method("Service", "run", MethodProps("r.Service"));
        await Method("Service", "used", MethodProps("r.Service", "private"));
        await Method("Service", "unused", MethodProps("r.Service", "private"));
        await Method("Service", "hooked", MethodProps("r.Service", "private", annotations: ["PostConstruct"]));
        await Method("Service", "readObject", MethodProps("r.Service", "private"));
        await Method("Service", "pub", MethodProps("r.Service"));
        await Type("Annotated", TypeProps("r.Annotated", annotations: ["Component"]));
        await Type("BaseAction", TypeProps("r.BaseAction"));
        await Type("Action", TypeProps("r.Action", super: "r.BaseAction"));
        await Method("Action", "execute", MethodProps("r.Action", isOverride: true));
        await Type("Form", TypeProps("r.Form"));
        await Type("FromXml", TypeProps("r.FromXml"));
        await Type("Literal", TypeProps("r.Literal"));
        await Type("JspUsed", TypeProps("r.JspUsed"));
        await Type("JspWild", TypeProps("r.web.JspWild"));
        await Type("Other", TypeProps("r.web.Other"));

        // Propagación: interfaz usada ⇒ su implementación; un tipo anidado vivo ⇒ el que lo contiene; una clase que solo usa otra por un campo.
        await Type("Iface", TypeProps("r.Iface", "interface"));
        await Method("Iface", "m", MethodProps("r.Iface"));
        await Type("Impl", TypeProps("r.Impl", interfaces: ["r.Iface"]));
        await Method("Impl", "m", MethodProps("r.Impl", isOverride: true));
        await Type("Outer", TypeProps("r.Outer"));
        await Type("Inner", TypeProps("r.Outer.Inner", outer: "r.Outer"));
        await Type("Orphan", TypeProps("r.Outer.Orphan", outer: "r.Outer"));
        await Type("Holder", TypeProps("r.Holder"));
        await Type("Helper", TypeProps("r.Helper"));
        await Method("Helper", "init", MethodProps("r.Helper"));
        await Type("Color", TypeProps("r.Color", "enum"));
        await Type("UnusedEnum", TypeProps("r.UnusedEnum", "enum"));

        // Muertos.
        await Type("Dead", TypeProps("r.Dead", access: "package"));
        await Method("Dead", "x", MethodProps("r.Dead", "package"));
        await Type("DeadPublic", TypeProps("r.DeadPublic"));
        await Method("DeadPublic", "work", MethodProps("r.DeadPublic"));
        await Method("DeadPublic", "toString", MethodProps("r.DeadPublic", isOverride: true));
        await Type("Reflective", TypeProps("r.plugins.Reflective"));
        await Type("LibChild", TypeProps("r.LibChild", super: "javax.servlet.http.HttpServlet"));
        await Method("LibChild", "doGet", MethodProps("r.LibChild", isOverride: true));

        // Aristas de javac y de Struts.
        await g.EdgeAsync(ids["Main#main"], ids["Service#run"], "CALLS");
        await g.EdgeAsync(ids["Service#run"], ids["Service#used"], "CALLS");
        await g.EdgeAsync(ids["Main#main"], ids["Iface"], "USES_TYPE");
        await g.EdgeAsync(ids["Impl"], ids["Iface"], "IMPLEMENTS");
        await g.EdgeAsync(ids["Main#main"], ids["Inner"], "USES_TYPE");
        await g.EdgeAsync(ids["Main#main"], ids["Holder"], "USES_TYPE");
        await g.EdgeAsync(ids["Holder"], ids["Helper#init"], "CALLS"); // un inicializador llama desde el tipo
        await g.EdgeAsync(ids["Main#main"], ids["Color"], "USES_TYPE");
        await g.EdgeAsync(ids["Action"], ids["BaseAction"], "INHERITS");
        await g.EdgeAsync(ids["Dead"], ids["DeadPublic"], "USES_TYPE"); // lo que solo usa código muerto, muerto

        var route = await g.NodeAsync("Route", "/x", "__route__ANY__/x", "WebContent/WEB-INF/struts-config.xml", 3, 3,
            Props(new() { ["action_type"] = "r.Action", ["form_type"] = "r.Form" }));
        await g.EdgeAsync(ids["Action"], route, "HANDLES");

        var reflection = new DynamicInstantiationScan
        {
            Sites = [new InstantiationSite("src/r/Factory.java", 12, "Class.forName", "Class.forName(P + n)", "")],
            LiteralsBySiteFile = new Dictionary<string, IReadOnlyList<string>> { ["src/r/Factory.java"] = ["r.plugins.", "Impl", "x.y"] },
            NamedClasses =
            [
                new NamedClass("r.FromXml", "WebContent/WEB-INF/web.xml", 3, DynamicInstantiationScan.SourceXml),
                new NamedClass("r.Literal", "src/r/Factory.java", 5, DynamicInstantiationScan.SourceLiteral)
            ]
        };
        return new Built { Graph = g, Scan = scan, Reflection = reflection };
    }

    [Fact]
    public async Task Roots_and_propagation_keep_alive_what_the_code_the_framework_and_the_views_reach()
    {
        var b = await BuildAsync();
        using var g = b.Graph;
        var stats = await JavaReachabilityWriter.ApplyAsync(g.DatabasePath, b.Scan, b.Reflection, CancellationToken.None);

        foreach (var live in new[]
                 {
                     ClassQn("Main"), ClassQn("Service"), ClassQn("Annotated"), ClassQn("BaseAction"), ClassQn("Action"), ClassQn("Form"),
                     ClassQn("FromXml"), ClassQn("Literal"), ClassQn("JspUsed"), ClassQn("JspWild"), ClassQn("Impl"), ClassQn("Outer"),
                     ClassQn("Inner"), ClassQn("Holder"), ClassQn("Helper"), ClassQn("Color"),
                     MethodQn("Main", "main"), MethodQn("Service", "run"), MethodQn("Service", "used"), MethodQn("Service", "pub"),
                     MethodQn("Service", "hooked"), MethodQn("Service", "readObject"), MethodQn("Action", "execute"), MethodQn("Helper", "init"),
                     MethodQn("Impl", "m")
                 })
        {
            var p = await PropsAsync(g, live);
            Assert.True(Reachable(p), $"{live} debería estar vivo");
            Assert.False(p.ContainsKey("dead_code_candidate"), live);
        }

        // Un método privado anotado o de serialización no es candidato aunque nadie lo llame; uno corriente sin llamadas, sí.
        Assert.Equal((JavaReachabilityWriter.HighConfidence, "método privado sin ninguna referencia desde código vivo del proyecto (referencias exactas de javac; solo la reflexión podría llamarlo)"),
            Dead(await PropsAsync(g, MethodQn("Service", "unused"))));
        Assert.Equal(1, stats.DeadPrivateMethods);

        // Quién mantiene vivo a quién queda dicho en la raíz: XML y literal de configuración llevan su marca.
        Assert.Equal("config-xml", (await PropsAsync(g, ClassQn("FromXml")))["dead_code_suppressed_by"]!.GetValue<string>());
        Assert.Equal("class-literal", (await PropsAsync(g, ClassQn("Literal")))["dead_code_suppressed_by"]!.GetValue<string>());
        Assert.False((await PropsAsync(g, ClassQn("Main"))).ContainsKey("dead_code_suppressed_by"));

        Assert.True(stats.Roots.ContainsKey("main"));
        Assert.True(stats.Roots.ContainsKey("struts"));
        Assert.True(stats.Roots.ContainsKey("jsp"));
        Assert.True(stats.Roots.ContainsKey("annotation"));
    }

    [Fact]
    public async Task Dead_components_get_a_confidence_by_visibility_and_methods_follow_their_class()
    {
        var b = await BuildAsync();
        using var g = b.Graph;
        var stats = await JavaReachabilityWriter.ApplyAsync(g.DatabasePath, b.Scan, b.Reflection, CancellationToken.None);

        // Clase del paquete sin usos: alta; pública: media. Sus métodos, igual; uno que sobrescribe, baja (contrato base).
        var dead = await PropsAsync(g, ClassQn("Dead"));
        Assert.False(Reachable(dead));
        Assert.True(dead["dead_code_candidate"]!.GetValue<bool>());
        Assert.Equal(JavaReachabilityWriter.HighConfidence, Dead(dead).Confidence);
        Assert.Contains("clase package sin conexión", Dead(dead).Reason);
        Assert.Equal(JavaReachabilityWriter.HighConfidence, Dead(await PropsAsync(g, MethodQn("Dead", "x"))).Confidence);

        var deadPublic = await PropsAsync(g, ClassQn("DeadPublic"));
        Assert.Equal(JavaReachabilityWriter.MediumConfidence, Dead(deadPublic).Confidence);
        Assert.Equal(JavaReachabilityWriter.MediumConfidence, Dead(await PropsAsync(g, MethodQn("DeadPublic", "work"))).Confidence);
        Assert.Equal(JavaReachabilityWriter.LowConfidence, Dead(await PropsAsync(g, MethodQn("DeadPublic", "toString"))).Confidence);

        // Un tipo anidado sin referencias y una clase que solo usa el código muerto también lo están; un enum sin usos, igual.
        Assert.False(Reachable(await PropsAsync(g, ClassQn("Orphan"))));
        Assert.False(Reachable(await PropsAsync(g, ClassQn("UnusedEnum"))));
        // r.web.Other está en el paquete que la JSP importa con .*, pero la JSP no la nombra.
        Assert.False(Reachable(await PropsAsync(g, ClassQn("Other"))));

        // Los tipos de interfaz no se marcan, y los métodos de una interfaz viva tampoco son candidatos.
        Assert.False((await PropsAsync(g, ClassQn("Iface"))).ContainsKey("is_reachable"));
        Assert.False((await PropsAsync(g, MethodQn("Iface", "m"))).ContainsKey("dead_code_candidate"));

        Assert.True(stats.DeadTotal >= 8);
        Assert.Equal(stats.DeadHigh + stats.DeadMedium + stats.DeadLow, stats.DeadTotal);
    }

    [Fact]
    public async Task What_a_framework_or_a_dynamic_site_may_instantiate_is_kept_as_low_confidence()
    {
        var b = await BuildAsync();
        using var g = b.Graph;
        var stats = await JavaReachabilityWriter.ApplyAsync(g.DatabasePath, b.Scan, b.Reflection, CancellationToken.None);

        // r.plugins.Reflective empieza por el literal de paquete «r.plugins.» de un Class.forName con el nombre compuesto.
        var reflective = await PropsAsync(g, ClassQn("Reflective"));
        Assert.Equal(JavaReachabilityWriter.LowConfidence, Dead(reflective).Confidence);
        Assert.Contains("reflexión: src/r/Factory.java:12 (Class.forName)", Dead(reflective).Reason);
        Assert.Contains("'r.plugins.'", Dead(reflective).Reason);
        Assert.Equal(1, stats.KeptForReflection);

        // Un tipo de una librería (javax.servlet) como padre: lo puede registrar el contenedor; sus métodos también salen como dudosos.
        var lib = await PropsAsync(g, ClassQn("LibChild"));
        Assert.Equal(JavaReachabilityWriter.LowConfidence, Dead(lib).Confidence);
        Assert.Contains("javax.servlet.http.HttpServlet", Dead(lib).Reason);
        Assert.Equal(JavaReachabilityWriter.LowConfidence, Dead(await PropsAsync(g, MethodQn("LibChild", "doGet"))).Confidence);

        // Una clase ajena al prefijo sigue su regla normal.
        Assert.Equal(JavaReachabilityWriter.MediumConfidence, Dead(await PropsAsync(g, ClassQn("DeadPublic"))).Confidence);
    }

    [Fact]
    public async Task Classes_a_resolved_reflection_target_can_name_are_alive_with_their_own_mark_and_the_rest_stay_candidates()
    {
        var b = await BuildAsync();
        using var g = b.Graph;
        // El compilador resolvió Class.forName(PAQUETE + nombre + "Impl") y Class.forName("r.DeadPublic") en Service.run.
        await g.Connection.ExecuteAsync(
            "UPDATE nodes SET properties = json_set(properties, '$.reflection_targets', json(@targets)) WHERE qualified_name = @qn",
            ("@targets", "[\"r.plugins.*\",\"r.DeadPublic\",\"r.*Missing\"]"), ("@qn", MethodQn("Service", "run")));

        var stats = await JavaReachabilityWriter.ApplyAsync(g.DatabasePath, b.Scan, null, CancellationToken.None);

        foreach (var name in new[] { "Reflective", "DeadPublic" })
        {
            var node = await PropsAsync(g, ClassQn(name));
            Assert.True(Reachable(node), name);
            Assert.Null(node["dead_code_candidate"]);
            Assert.Equal("reflection-target", node["dead_code_suppressed_by"]!.GetValue<string>());
        }
        // Un patrón que no casa con nada no mantiene vivo a nadie, y lo que cuelga de un tipo vivo sigue su regla.
        Assert.False(Reachable(await PropsAsync(g, ClassQn("Dead"))));
        Assert.Equal(0, stats.KeptForReflection);
    }

    [Fact]
    public async Task Without_the_reflection_scan_nothing_is_kept_as_doubtful_by_a_literal()
    {
        var b = await BuildAsync();
        using var g = b.Graph;
        await JavaReachabilityWriter.ApplyAsync(g.DatabasePath, b.Scan, null, CancellationToken.None);

        Assert.Equal(JavaReachabilityWriter.MediumConfidence, Dead(await PropsAsync(g, ClassQn("Reflective"))).Confidence);
        // Sin el XML y sin el literal, FromXml y Literal ya no tienen quien los mantenga.
        Assert.False(Reachable(await PropsAsync(g, ClassQn("FromXml"))));
        Assert.False(Reachable(await PropsAsync(g, ClassQn("Literal"))));
    }

    [Fact]
    public async Task A_second_pass_replaces_the_marks_of_the_first_and_leaves_no_contradiction()
    {
        var b = await BuildAsync();
        using var g = b.Graph;
        // Una marca que dejó una pasada anterior en algo que ahora está vivo.
        await g.Connection.ExecuteAsync("""UPDATE nodes SET properties = json_set(properties, '$.dead_code_candidate', json('true'), '$.dead_code_confidence', 'high') WHERE qualified_name = @qn""",
            ("@qn", ClassQn("Main")));

        await JavaReachabilityWriter.ApplyAsync(g.DatabasePath, b.Scan, b.Reflection, CancellationToken.None);
        var first = await g.ListAsync("SELECT qualified_name, properties FROM nodes ORDER BY qualified_name", r => r.GetString(0) + "=" + r.GetString(1));
        Assert.False((await PropsAsync(g, ClassQn("Main"))).ContainsKey("dead_code_candidate"));

        await JavaReachabilityWriter.ApplyAsync(g.DatabasePath, b.Scan, b.Reflection, CancellationToken.None);
        Assert.Equal(first.Count, (await g.ListAsync("SELECT qualified_name FROM nodes", r => r.GetString(0))).Count);
        Assert.Equal(0, await g.CountAsync("""
            SELECT COUNT(1) FROM nodes WHERE json_extract(properties, '$.is_reachable') = 1 AND json_extract(properties, '$.dead_code_candidate') = 1
            """));
    }

    [Fact]
    public async Task Nothing_outside_java_files_is_touched_and_no_node_or_edge_is_created()
    {
        var b = await BuildAsync();
        using var g = b.Graph;
        var other = await g.NodeAsync("Method", "Foo", "R.other.Foo", "src/Other.cs", 1, 2, "{\"param_types\":[],\"accessibility\":\"private\"}");
        var nodes = await g.CountAsync("SELECT COUNT(1) FROM nodes");
        var edges = await g.CountAsync("SELECT COUNT(1) FROM edges");

        await JavaReachabilityWriter.ApplyAsync(g.DatabasePath, b.Scan, b.Reflection, CancellationToken.None);

        Assert.Equal(nodes, await g.CountAsync("SELECT COUNT(1) FROM nodes"));
        Assert.Equal(edges, await g.CountAsync("SELECT COUNT(1) FROM edges"));
        Assert.False((await PropsAsync(g, "R.other.Foo")).ContainsKey("is_reachable"));
        // No deja fila en enrichment_runs: ni siquiera crea la tabla.
        Assert.Equal(0, await g.CountAsync("SELECT COUNT(1) FROM sqlite_master WHERE name = 'enrichment_runs'"));
        Assert.NotEqual(0, other);
    }

    [Fact]
    public async Task It_refuses_the_cbm_cache()
    {
        var cache = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codebase-memory-mcp", "x.db");
        await Assert.ThrowsAsync<InvalidOperationException>(() => JavaReachabilityWriter.ApplyAsync(cache, null, null, CancellationToken.None));
    }
}

internal static class ReachabilityTestSql
{
    public static async Task ExecuteAsync(this Microsoft.Data.Sqlite.SqliteConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
