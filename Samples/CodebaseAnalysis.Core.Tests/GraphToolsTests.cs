using AITaskAgent.Core.Models;
using CodebaseAnalysis.Core.Chat;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tests.Enrichment;
using CodebaseAnalysis.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Las tools del grafo que ve el chat: <c>graph_neighbors</c> sobre el grafo sintético de <see cref="JavaGraphFixture"/>
/// (con aristas del analizador y de Struts añadidas) y las propiedades de <c>graph_search</c>. El de extremo a extremo
/// enriquece <see cref="JavaTestRepo"/> con el JDK de los tests.
/// </summary>
public sealed class GraphToolsTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static readonly string ServicioClass = JavaGraphFixture.Qn(JavaTestRepo.ServicioPath, "Servicio");
    private static readonly string BaseClass = JavaGraphFixture.Qn(JavaTestRepo.BasePath, "Base");
    private static readonly string BaseLog = JavaGraphFixture.Qn(JavaTestRepo.BasePath, "Base.log");
    private static readonly string Execute = JavaGraphFixture.Qn(JavaTestRepo.ConsultarActionPath, "ConsultarAction.execute");
    private static readonly string DaoInterface = JavaGraphFixture.Qn(JavaTestRepo.DaoPath, "Dao");
    private static readonly string GetId = JavaGraphFixture.Qn(JavaTestRepo.BeanPath, "Bean.getId");
    private const string RouteQn = "__route__ANY__/consultar";

    private static readonly int LogLine = JavaTestRepo.LineOf(JavaTestRepo.ServicioPath, "log(\"x\")");
    private static readonly int TwoArgsLine = JavaTestRepo.LineOf(JavaTestRepo.ServicioPath, "1L)");
    private static readonly int OneArgLine = JavaTestRepo.LineOf(JavaTestRepo.ServicioPath, "d.consultar(new java.util.Hashtable());");

    private static PipelineContext ContextFor(string databasePath)
    {
        var context = new PipelineContext();
        context.Set(AnalysisContextKeys.CbmDbPath, databasePath);
        return context;
    }

    private async Task<string> RunAsync(AITaskAgent.LLM.Tools.Abstractions.ITool tool, string args, string databasePath)
    {
        var result = await tool.ExecuteAsync(args, ContextFor(databasePath), "ChatAssistant", NullLogger.Instance);
        output.WriteLine($"--- {tool.Name} {args}\n{result}");
        return result;
    }

    private Task<string> NeighborsAsync(string databasePath, string args) => RunAsync(new GraphNeighborsTool(), args, databasePath);

    /// <summary>
    /// El grafo de cbm de <see cref="JavaGraphFixture"/> con lo que añaden el analizador (CALLS con líneas, una a un método de
    /// interfaz, IMPLEMENTS, todas con su marca <c>enriched_by</c>) y el plugin Struts (Route, Jsp, TilesDefinition y sus
    /// aristas). Las CALLS heurísticas de cbm del fixture siguen ahí, sin marca.
    /// </summary>
    private async Task<CbmTestGraph> GraphAsync()
    {
        var g = await JavaGraphFixture.CreateGraphAsync(Path.Combine(_temp.Path, "codebase.db"));
        async Task<long> Id(string qn) => (await g.NodeIdAsync(qn))!.Value;

        var ejecutar = await Id(JavaGraphFixture.Ejecutar);
        await g.EdgeAsync(ejecutar, await Id(BaseLog), "CALLS", $"{{\"enriched_by\":[\"JavaAnalyzer\"],\"lines\":[{LogLine}],\"count\":1,\"kinds\":[\"call\"]}}");
        await g.EdgeAsync(ejecutar, await Id(JavaGraphFixture.DaoConsultar), "CALLS",
            $"{{\"enriched_by\":[\"JavaAnalyzer\"],\"lines\":[{TwoArgsLine},{OneArgLine}],\"count\":2,\"kinds\":[\"call\"]}}");
        await g.EdgeAsync(await Id(JavaGraphFixture.Qn(JavaTestRepo.DaoImplPath, "DaoImpl")), await Id(DaoInterface), "IMPLEMENTS",
            "{\"enriched_by\":[\"JavaAnalyzer\"]}");
        // Una arista sin propiedades: sale sin detalle. (Una con JSON inválido no se puede insertar: la columna generada
        // url_path_gen de cbm la rechaza.)
        await g.EdgeAsync(await Id(Execute), await Id(BaseLog), "CALLS");

        var route = await g.NodeAsync("Route", "/consultar", RouteQn, JavaTestRepo.StrutsConfigPath, 14, 17,
            "{\"framework\":\"struts\",\"route_path\":\"/consultar\",\"http_method\":\"ANY\",\"action_type\":\"com.acme.web.ConsultarAction\",\"form_bean\":\"consultaForm\"}");
        var resultado = await g.NodeAsync("Jsp", JavaTestRepo.ResultadoJspPath, $"{JavaGraphFixture.Project}.WebContent.pages.resultado.__jsp__",
            JavaTestRepo.ResultadoJspPath, 1, 1);
        var tile = await g.NodeAsync("TilesDefinition", "consulta.def", $"{JavaGraphFixture.Project}.__tiles__.consulta.def", JavaTestRepo.TilesDefsPath, 3, 5);
        var layout = await g.NodeAsync("Jsp", JavaTestRepo.LayoutJspPath, $"{JavaGraphFixture.Project}.WebContent.pages.layout.__jsp__", JavaTestRepo.LayoutJspPath, 1, 6);
        await g.EdgeAsync(await Id(JavaGraphFixture.Qn(JavaTestRepo.ConsultarActionPath, "ConsultarAction")), route, "HANDLES",
            "{\"framework\":\"struts\",\"http_method\":\"ANY\",\"route_path\":\"/consultar\"}");
        await g.EdgeAsync(route, resultado, "FORWARDS_TO", "{\"forward_name\":\"ok\",\"forward_names\":[\"ok\"],\"redirect\":false,\"lines\":[15]}");
        await g.EdgeAsync(route, tile, "FORWARDS_TO", "{\"forward_name\":\"tile\",\"forward_names\":[\"tile\"],\"redirect\":false,\"lines\":[16]}");
        await g.EdgeAsync(tile, layout, "RENDERS", "{\"role\":\"template\",\"roles\":[\"template\"]}");
        return g;
    }

    /// <summary>La sección de un tipo de arista y dirección, hasta la siguiente o el final.</summary>
    private static string Section(string text, string header)
    {
        var start = text.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Falta la sección «{header}»");
        var next = text.IndexOf("\n## ", start + header.Length, StringComparison.Ordinal);
        return next < 0 ? text[start..] : text[start..next];
    }

    private static string Row(string text, string qn) =>
        text.Split('\n').Single(l => l.StartsWith("| ", StringComparison.Ordinal) && l.Contains($"`{qn}`", StringComparison.Ordinal));

    [Fact]
    public async Task Neighbors_follow_the_requested_direction_with_the_default_edge_types()
    {
        using var g = await GraphAsync();

        var outgoing = await NeighborsAsync(g.DatabasePath, """{"qualified_name":"Servicio.ejecutar","direction":"out"}""");
        Assert.StartsWith($"# Vecinos en el grafo de `{JavaGraphFixture.Ejecutar}`", outgoing);
        var calls = Section(outgoing, "## `CALLS` salientes");
        Assert.Contains($"`{BaseLog}`", calls);
        Assert.Contains($"`{JavaGraphFixture.DaoConsultar}`", calls);
        Assert.Contains($"`{JavaGraphFixture.DaoImplConsultar}`", calls);
        Assert.Contains($"`{GetId}`", calls);
        Assert.DoesNotContain("entrantes", outgoing);
        Assert.DoesNotContain("USAGE", outgoing);

        var incoming = await NeighborsAsync(g.DatabasePath, """{"qualified_name":"Servicio.ejecutar","direction":"in"}""");
        Assert.Contains($"`{Execute}`", Section(incoming, "## `CALLS` entrantes"));
        Assert.Contains($"`{ServicioClass}`", Section(incoming, "## `DEFINES_METHOD` entrantes"));
        Assert.DoesNotContain("salientes", incoming);
        // TESTS no está entre los tipos por defecto.
        Assert.DoesNotContain("ServicioTest", incoming);

        var both = await NeighborsAsync(g.DatabasePath, """{"qualified_name":"Servicio.ejecutar"}""");
        Assert.Contains("## `CALLS` salientes", both);
        Assert.Contains("## `CALLS` entrantes", both);
        Assert.True(both.IndexOf("salientes", StringComparison.Ordinal) < both.IndexOf("entrantes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Edge_types_filter_the_neighbors_as_a_list_or_as_text()
    {
        using var g = await GraphAsync();

        var defines = await NeighborsAsync(g.DatabasePath, """{"qualified_name":"Servicio.ejecutar","direction":"in","edge_types":["defines_method"]}""");
        Assert.Contains($"`{ServicioClass}`", Section(defines, "## `DEFINES_METHOD` entrantes"));
        Assert.DoesNotContain("CALLS", defines);

        var tests = await NeighborsAsync(g.DatabasePath, """{"qualified_name":"Servicio.ejecutar","direction":"in","edge_types":"TESTS"}""");
        Assert.Contains("ServicioTest.probar", Section(tests, "## `TESTS` entrantes"));
        Assert.DoesNotContain("DEFINES_METHOD", tests);
    }

    [Fact]
    public async Task An_ambiguous_name_lists_the_candidates_without_mixing_neighbors()
    {
        using var g = await GraphAsync();

        var text = await NeighborsAsync(g.DatabasePath, """{"qualified_name":"consultar"}""");

        Assert.Contains("`consultar` casa con 3 nodos. No se muestran vecinos", text);
        Assert.Contains($"`{JavaGraphFixture.DaoConsultar}`", text);
        Assert.Contains($"`{JavaGraphFixture.DaoFunction}`", text);
        Assert.Contains($"`{JavaGraphFixture.DaoImplConsultar}`", text);
        Assert.DoesNotContain("## ", text);

        // El QN exacto de uno de ellos lo resuelve; «Base» elige la clase y no el módulo del fichero, que se llama por su ruta.
        Assert.Contains("## `CALLS` entrantes", await NeighborsAsync(g.DatabasePath, $"{{\"qualified_name\":\"{JavaGraphFixture.DaoConsultar}\"}}"));
        Assert.StartsWith($"# Vecinos en el grafo de `{BaseClass}`", await NeighborsAsync(g.DatabasePath, """{"qualified_name":"Base","direction":"in"}"""));

        var missing = await NeighborsAsync(g.DatabasePath, """{"qualified_name":"NoExiste"}""");
        Assert.Contains("Ningún nodo tiene un Qualified Name que coincida", missing);
    }

    [Fact]
    public async Task The_limit_truncates_and_says_so()
    {
        using var g = await CbmTestGraph.CreateAsync(Path.Combine(_temp.Path, "codebase.db"), "demo");
        var hub = await g.NodeAsync("Method", "hub", "demo.Hub.hub", "src/Hub.java", 3, 5);
        for (var i = 1; i <= 5; i++)
            await g.EdgeAsync(await g.NodeAsync("Method", $"c{i}", $"demo.C{i}.c{i}", $"src/C{i}.java", i, i), hub, "CALLS");

        var text = await NeighborsAsync(g.DatabasePath, """{"qualified_name":"demo.Hub.hub","limit":3}""");

        Assert.Contains("## `CALLS` entrantes (vecino → `Hub.hub`): 3 de 5", text);
        Assert.Equal(3, text.Split('\n').Count(l => l.StartsWith("| `Method`", StringComparison.Ordinal)));
        Assert.Contains("> Salida truncada: se muestran 3 de 5 aristas (límite 3).", text);

        var whole = await NeighborsAsync(g.DatabasePath, """{"qualified_name":"demo.Hub.hub","limit":5000}""");
        Assert.Contains("): 5\n", whole.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("truncada", whole);
    }

    [Fact]
    public async Task Nodes_with_invalid_json_properties_and_edges_without_properties_still_show_up()
    {
        using var g = await GraphAsync();

        // Servicio tiene un properties con un salto de línea sin escapar.
        Assert.Equal(0, await g.CountAsync("SELECT json_valid(properties) FROM nodes WHERE qualified_name = @qn", ("@qn", ServicioClass)));
        var servicio = await NeighborsAsync(g.DatabasePath, $"{{\"qualified_name\":\"{ServicioClass}\",\"direction\":\"out\"}}");
        Assert.Contains($"`{JavaGraphFixture.Ejecutar}`", Section(servicio, "## `DEFINES_METHOD` salientes"));
        Assert.Contains($"`{BaseClass}`", Section(servicio, "## `INHERITS` salientes"));

        var execute = await NeighborsAsync(g.DatabasePath, $"{{\"qualified_name\":\"{Execute}\",\"direction\":\"out\",\"edge_types\":[\"CALLS\"]}}");
        Assert.EndsWith("|  |", Row(execute, BaseLog).TrimEnd());
    }

    [Fact]
    public async Task Calls_show_their_lines_in_the_calling_file_and_point_interface_calls_to_implements()
    {
        using var g = await GraphAsync();
        const string servicio = JavaTestRepo.ServicioPath;

        var outgoing = await NeighborsAsync(g.DatabasePath, """{"qualified_name":"Servicio.ejecutar","direction":"out"}""");
        Assert.Contains($"| 1 llamada · línea {LogLine} de `{servicio}` |", Row(outgoing, BaseLog));
        Assert.Contains($"`{JavaTestRepo.BasePath}:4`", Row(outgoing, BaseLog));
        Assert.Contains($"| 2 llamadas · líneas {TwoArgsLine}, {OneArgLine} de `{servicio}` · método de interfaz |", Row(outgoing, JavaGraphFixture.DaoConsultar));
        Assert.Contains($"> Llamada a un método de la interfaz `{DaoInterface}`: las implementaciones se ven con `graph_neighbors` sobre `{DaoInterface}`, "
            + "`direction: in`, `edge_types: [\"IMPLEMENTS\"]`.", outgoing);

        // Entrantes: las líneas son del fichero del llamante, no del nodo.
        var incoming = await NeighborsAsync(g.DatabasePath, $"{{\"qualified_name\":\"{BaseLog}\",\"direction\":\"in\",\"edge_types\":[\"CALLS\"]}}");
        Assert.Contains($"línea {LogLine} de `{servicio}`", Row(incoming, JavaGraphFixture.Ejecutar));

        // El propio método de interfaz lo avisa, y su interfaz lleva a la implementación.
        var iface = await NeighborsAsync(g.DatabasePath, $"{{\"qualified_name\":\"{JavaGraphFixture.DaoConsultar}\",\"direction\":\"in\"}}");
        Assert.Contains($"> Es un método de la interfaz `{DaoInterface}`", iface);
        var impl = await NeighborsAsync(g.DatabasePath, $"{{\"qualified_name\":\"{DaoInterface}\",\"direction\":\"in\",\"edge_types\":[\"IMPLEMENTS\"]}}");
        Assert.Contains(JavaGraphFixture.Qn(JavaTestRepo.DaoImplPath, "DaoImpl"), Section(impl, "## `IMPLEMENTS` entrantes"));
    }

    /// <summary>
    /// Procedencia (tanda K): cada arista dice si la resolvió un enriquecedor o es heurística de cbm, con su estrategia y su
    /// confianza, y la cabecera cuenta las de cada clase y avisa si hay alguna sin resolver.
    /// </summary>
    [Fact]
    public async Task Each_edge_says_whether_it_is_resolved_or_a_cbm_heuristic_and_the_header_warns()
    {
        using var g = await GraphAsync();

        var calls = await NeighborsAsync(g.DatabasePath, """{"qualified_name":"Servicio.ejecutar","direction":"out","edge_types":["CALLS"]}""");
        Assert.Contains("| resuelta · JavaAnalyzer | 1 llamada · ", Row(calls, BaseLog));
        Assert.Contains("| resuelta · JavaAnalyzer | 2 llamadas · ", Row(calls, JavaGraphFixture.DaoConsultar));
        Assert.Contains("| heurística cbm · `suffix_match` · confianza 0.33 |", Row(calls, GetId));
        Assert.Contains("| heurística cbm · `unique_name` |", Row(calls, JavaGraphFixture.DaoImplConsultar));
        Assert.Contains("- Procedencia: 2 resueltas por enriquecedor (JavaAnalyzer) · 2 heurísticas de cbm.", calls);
        Assert.Contains("> Aviso: 2 aristas no están resueltas (heurística de cbm, que decide el destino por nombre, o estimación del LLM). "
            + "Son pistas: confírmalas en el código antes de afirmarlas.", calls);

        // Una arista de cbm sin propiedades también es heurística; la pertenencia (DEFINES_METHOD) es estructura y no avisa.
        var execute = await NeighborsAsync(g.DatabasePath, $"{{\"qualified_name\":\"{Execute}\",\"direction\":\"out\",\"edge_types\":[\"CALLS\"]}}");
        Assert.Contains("| heurística cbm |  |", Row(execute, BaseLog));
        var members = await NeighborsAsync(g.DatabasePath, $"{{\"qualified_name\":\"{ServicioClass}\",\"direction\":\"out\",\"edge_types\":[\"DEFINES_METHOD\"]}}");
        Assert.Contains("| estructura cbm |", Row(members, JavaGraphFixture.Ejecutar));
        Assert.Contains("- Procedencia: 2 de estructura de cbm.", members);
        Assert.DoesNotContain("> Aviso", members);

        // Lo que escribió el fallback del LLM no pasa por resuelto.
        var dao = (await g.NodeIdAsync(DaoInterface))!.Value;
        await g.EdgeAsync(dao, (await g.NodeIdAsync(BaseClass))!.Value, "INHERITS", "{\"enriched_by\":[\"SemanticEnricherAgent\"]}");
        var llm = await NeighborsAsync(g.DatabasePath, $"{{\"qualified_name\":\"{DaoInterface}\",\"direction\":\"out\",\"edge_types\":[\"INHERITS\"]}}");
        Assert.Contains("| estimada por LLM · SemanticEnricherAgent |", Row(llm, BaseClass));
        Assert.Contains("- Procedencia: 1 estimada por el LLM.", llm);
        Assert.Contains("> Aviso: 1 arista no está resuelta", llm);
    }

    [Fact]
    public async Task Struts_edges_show_the_forward_and_the_role()
    {
        using var g = await GraphAsync();

        var route = await NeighborsAsync(g.DatabasePath, $"{{\"qualified_name\":\"{RouteQn}\"}}");
        var forwards = Section(route, "## `FORWARDS_TO` salientes");
        Assert.Contains($"forward `ok` · línea 15 de `{JavaTestRepo.StrutsConfigPath}`", forwards);
        Assert.Contains("forward `tile`", forwards);
        Assert.Contains("| `ANY` · `/consultar` |", Section(route, "## `HANDLES` entrantes"));

        var tile = await NeighborsAsync(g.DatabasePath, """{"qualified_name":"consulta.def","direction":"out"}""");
        Assert.Contains("| rol `template` |", Section(tile, "## `RENDERS` salientes"));
    }

    [Fact]
    public async Task Graph_search_adds_the_useful_properties_and_survives_invalid_json()
    {
        using var g = await CbmTestGraph.CreateAsync(Path.Combine(_temp.Path, "codebase.db"), "demo");
        var longDoc = string.Join(' ', Enumerable.Repeat("palabra", 60));
        await g.NodeAsync("Method", "ejecutar", "demo.Servicio.ejecutar", "src/Servicio.java", 10, 20,
            "{\"signature\":\"void ejecutar()\",\"param_types\":[],\"return_type_fqn\":\"void\",\"accessibility\":\"public\",\"is_static\":false,"
            + "\"cyclomatic\":3,\"docstring\":\"/**\\n * Añade la acción.\\n */\",\"code\":\"secreto del snapshot\",\"otra\":\"no se enseña\"}");
        await g.NodeAsync("Class", "Servicio", "demo.Servicio", "src/Servicio.java", 6, 25,
            "{\"fqn\":\"com.acme.Servicio\",\"super_fqn\":\"com.acme.Base\",\"interfaces_fqn\":[\"java.io.Serializable\"],\"docstring\":\"" + longDoc + "\"}");
        await g.NodeAsync("Class", "Roto", "demo.Roto", "src/Roto.java", 1, 3, "{\"base_classes\":[\"extends\nBase\"]}");
        var imports = string.Join(',', Enumerable.Range(1, 15).Select(i => $"\"com.acme.p{i}.*\""));
        await g.NodeAsync("Jsp", "WebContent/a.jsp", "demo.WebContent.a.__jsp__", "WebContent/a.jsp", 1, 9,
            "{\"suspicious\":true,\"suspicious_reasons\":[\"sql\"],\"scriptlet_count\":2,\"page_imports\":[" + imports + "],\"has_sql\":true,\"session_access\":false}");

        var text = await RunAsync(new GraphSearchTool(), """{"qn_pattern":"demo."}""", g.DatabasePath);

        Assert.Contains("# Búsqueda en Grafo (4 resultados)", text);
        Assert.Contains("| `Class` | **Roto** | `demo.Roto` | `src/Roto.java` | 1 |", text);
        var properties = text[text.IndexOf("### Propiedades", StringComparison.Ordinal)..];
        Assert.Contains("- **ejecutar** `demo.Servicio.ejecutar`: signature: `void ejecutar()` · return_type_fqn: `void` · accessibility: `public` · "
            + "is_static: `false` · cyclomatic: `3` · docstring: «Añade la acción.»", properties);
        Assert.Contains("fqn: `com.acme.Servicio` · super_fqn: `com.acme.Base` · interfaces_fqn: `java.io.Serializable`", properties);
        Assert.Contains($"docstring: «{longDoc[..GraphSearchTool.MaxDocstringChars].TrimEnd()}…»", properties);
        Assert.Contains("- **Roto** `demo.Roto`: *(properties no es JSON válido en el grafo; no se pueden leer)*", properties);
        Assert.Contains("suspicious: `true` · suspicious_reasons: `sql` · scriptlet_count: `2` · page_imports: `com.acme.p1.*`", properties);
        Assert.Contains("`com.acme.p10.*` (+5) · has_sql: `true` · session_access: `false`", properties);
        Assert.DoesNotContain("no se enseña", text);
        Assert.DoesNotContain("secreto del snapshot", text);
        Assert.DoesNotContain("\n####", text);

        var bare = await RunAsync(new GraphSearchTool(), """{"qn_pattern":"demo.","include_properties":false}""", g.DatabasePath);
        Assert.DoesNotContain("### Propiedades", bare);
        Assert.Contains("| `Class` | **Roto** |", bare);
    }

    [Fact]
    public void The_chat_has_graph_neighbors_and_graph_domain()
    {
        Assert.Contains(GraphNeighborsTool.ToolName, ChatAgent.ToolNames);
        Assert.Contains("graph_domain", ChatAgent.ToolNames);
        Assert.Equal("graph_neighbors", new GraphNeighborsTool().Name);
        Assert.True(new GraphNeighborsTool().IsReadOnly);
    }

    [JdkFact]
    public async Task After_the_java_enrichment_neighbors_show_the_resolved_calls_and_the_inheritance()
    {
        var repo = JavaTestRepo.WriteTo(Path.Combine(_temp.Path, "repo"));
        Directory.CreateDirectory(Path.Combine(_temp.Path, "db"));
        using var graph = await JavaGraphFixture.CreateGraphAsync(Path.Combine(_temp.Path, "db", "codebase.db"));
        var configuration = TestConfig.FromPairs(("JavaAnalyzer:JdkPath", TestJdk.Info!.JdkPath), ("JavaAnalyzer:JvmOptions:0", "-Xmx1g"));
        var context = EnrichmentStepContext.Create(configuration, new CapturingReporter(), repo, graph.DatabasePath);
        var input = new GraphStatusResult { DatabasePath = graph.DatabasePath, ExistedBefore = true, IsEnriched = true };
        await new LanguageEnricherStep().ExecuteAsync(input, context, 1, null, CancellationToken.None);

        var outgoing = await NeighborsAsync(graph.DatabasePath, """{"qualified_name":"Servicio.ejecutar","direction":"out"}""");
        var calls = Section(outgoing, "## `CALLS` salientes");
        Assert.Contains($"línea {OneArgLine} de `{JavaTestRepo.ServicioPath}` · método de interfaz", Row(calls, JavaGraphFixture.DaoConsultar));
        Assert.Contains($"línea {LogLine} de `{JavaTestRepo.ServicioPath}`", Row(calls, BaseLog));
        Assert.Contains($"> Llamada a un método de la interfaz `{DaoInterface}`", outgoing);
        // Las CALLS del analizador llevan su marca: salen como resueltas.
        Assert.Contains("| resuelta · JavaAnalyzer |", Row(calls, BaseLog));
        Assert.Contains("resueltas por enriquecedor (JavaAnalyzer)", outgoing);

        var incoming = await NeighborsAsync(graph.DatabasePath, """{"qualified_name":"Base","direction":"in"}""");
        Assert.StartsWith($"# Vecinos en el grafo de `{BaseClass}`", incoming);
        Assert.Contains($"`{ServicioClass}`", Section(incoming, "## `INHERITS` entrantes"));
    }
}
