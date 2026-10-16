using System.Text;
using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Enrichment.Java;
using CodebaseAnalysis.Core.Enrichment.Web;

namespace CodebaseAnalysis.Core.Tests.Enrichment;

/// <summary>
/// Plugin Struts/JSP (P14) sobre <see cref="JavaTestRepo"/> y el grafo sintético de <see cref="JavaGraphFixture"/>, que tiene
/// la clase <c>ConsultarAction</c> tal como la deja cbm, sin que haya corrido el analizador Java.
/// </summary>
public class StrutsJspTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly string RouteQn = "__route__ANY__/consultar";

    private static string JspQn(string path) => $"{JavaGraphFixture.Project}.{path[..path.LastIndexOf('.')].Replace('/', '.')}.__jsp__";

    private static string TilesQn(string name) => $"{JavaGraphFixture.Project}.__tiles__.{name}";

    private static void Write(string root, string relative, string text, Encoding? encoding = null)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, (encoding ?? new UTF8Encoding(false)).GetBytes(text.ReplaceLineEndings("\n")));
    }

    private sealed class Run : IDisposable
    {
        public required TempDirectory Temp { get; init; }
        public required string Repo { get; init; }
        public required RepositoryScanResult Scan { get; init; }
        public required CbmTestGraph Graph { get; init; }
        public required StrutsJspWriteResult Result { get; set; }

        public Task<StrutsJspWriteResult> ApplyAgainAsync() =>
            StrutsJspWriter.ApplyAsync(Graph.DatabasePath, Scan, new StrutsJspOptions(), Encoding.Latin1, "cfg", StartedAt, CancellationToken.None);

        public void Dispose()
        {
            Graph.Dispose();
            Temp.Dispose();
        }
    }

    /// <param name="prepare">Cambios en el repositorio antes del recorrido.</param>
    /// <param name="afterScan">Cambios en el repositorio después del recorrido y antes del plugin.</param>
    private static async Task<Run> ApplyAsync(Action<string>? prepare = null, Action<string>? afterScan = null)
    {
        var temp = new TempDirectory();
        var repo = JavaTestRepo.WriteTo(Path.Combine(temp.Path, "repo"));
        prepare?.Invoke(repo);
        var scan = RepositoryScan.Scan(repo, TestRoots.Resolve(repo));
        afterScan?.Invoke(repo);

        var graph = await JavaGraphFixture.CreateGraphAsync(Path.Combine(temp.Path, "codebase.db"));
        // Otra clase con el mismo nombre simple en otro paquete: no debe recibir HANDLES.
        await graph.NodeAsync("Class", "ConsultarAction", JavaGraphFixture.Qn("src/com/otro/web/ConsultarAction.java", "ConsultarAction"),
            "src/com/otro/web/ConsultarAction.java", 3, 7);

        var result = await StrutsJspWriter.ApplyAsync(graph.DatabasePath, scan, new StrutsJspOptions(), Encoding.Latin1, "cfg", StartedAt,
            CancellationToken.None);
        return new Run { Temp = temp, Repo = repo, Scan = scan, Graph = graph, Result = result };
    }

    private static async Task<JsonObject> PropertiesAsync(CbmTestGraph graph, string qn) =>
        JsonNode.Parse((await graph.TextAsync("SELECT properties FROM nodes WHERE qualified_name = @qn", ("@qn", qn)))!)!.AsObject();

    /// <summary>Aristas de un tipo como «origen -> destino» por QN, con sus propiedades.</summary>
    private static Task<List<(string Source, string Target, JsonObject Properties)>> EdgesAsync(CbmTestGraph graph, string type) =>
        graph.ListAsync("""
            SELECT s.qualified_name, t.qualified_name, e.properties FROM edges e
            JOIN nodes s ON s.id = e.source_id JOIN nodes t ON t.id = e.target_id
            WHERE e.type = @type ORDER BY s.qualified_name, t.qualified_name
            """, r => (r.GetString(0), r.GetString(1), JsonNode.Parse(r.GetString(2))!.AsObject()), ("@type", type));

    private static string[] Strings(JsonNode? node) => node!.AsArray().Select(v => v!.GetValue<string>()).ToArray();

    private static int[] Ints(JsonNode? node) => node!.AsArray().Select(v => v!.GetValue<int>()).ToArray();

    [Fact]
    public async Task The_action_class_is_found_by_the_fqn_javac_wrote_before_falling_back_to_the_simple_name_and_path_suffix()
    {
        using var temp = new TempDirectory();
        var repo = JavaTestRepo.WriteTo(Path.Combine(temp.Path, "repo"));
        var scan = RepositoryScan.Scan(repo, TestRoots.Resolve(repo));
        using var graph = await JavaGraphFixture.CreateGraphAsync(Path.Combine(temp.Path, "codebase.db"));

        // El nodo cuya ruta termina en com/acme/web/ConsultarAction.java lo declara javac en otro paquete (la ruta no fue prueba de nada)…
        var byPath = JavaGraphFixture.Qn(JavaTestRepo.ConsultarActionPath, "ConsultarAction");
        await graph.Connection.ExecuteAsync("UPDATE nodes SET properties = '{\"fqn\":\"com.otro.web.ConsultarAction\"}' WHERE qualified_name = @qn", ("@qn", byPath));
        // …y la clase de la acción vive en una carpeta que no sigue el paquete, pero su fqn es el de struts-config.
        var moved = JavaGraphFixture.Qn("legacy/ConsultarAction.java", "ConsultarAction");
        await graph.NodeAsync("Class", "ConsultarAction", moved, "legacy/ConsultarAction.java", 3, 7, "{\"fqn\":\"com.acme.web.ConsultarAction\"}");

        await StrutsJspWriter.ApplyAsync(graph.DatabasePath, scan, new StrutsJspOptions(), Encoding.Latin1, "cfg", StartedAt, CancellationToken.None);

        Assert.Equal(moved, Assert.Single(await EdgesAsync(graph, "HANDLES")).Source);
    }

    [Fact]
    public async Task Without_an_fqn_the_action_class_is_still_found_by_simple_name_and_package_path()
    {
        using var temp = new TempDirectory();
        var repo = JavaTestRepo.WriteTo(Path.Combine(temp.Path, "repo"));
        var scan = RepositoryScan.Scan(repo, TestRoots.Resolve(repo));
        using var graph = await JavaGraphFixture.CreateGraphAsync(Path.Combine(temp.Path, "codebase.db"));

        await StrutsJspWriter.ApplyAsync(graph.DatabasePath, scan, new StrutsJspOptions(), Encoding.Latin1, "cfg", StartedAt, CancellationToken.None);

        Assert.Equal(JavaGraphFixture.Qn(JavaTestRepo.ConsultarActionPath, "ConsultarAction"), Assert.Single(await EdgesAsync(graph, "HANDLES")).Source);
    }

    [Fact]
    public async Task Each_action_is_a_route_handled_by_its_class_found_by_package_path()
    {
        using var run = await ApplyAsync();
        var graph = run.Graph;

        var line = JavaTestRepo.LineOf(JavaTestRepo.StrutsConfigPath, "<action path=\"/consultar\"");
        var row = Assert.Single(await graph.ListAsync("""
            SELECT label, name, file_path, file, start_line, line_start, end_line, line_end FROM nodes WHERE qualified_name = @qn
            """, r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7)),
            ("@qn", RouteQn)));
        Assert.Equal(("Route", "/consultar", JavaTestRepo.StrutsConfigPath, JavaTestRepo.StrutsConfigPath, line, line, line, line), row);

        var route = await PropertiesAsync(graph, RouteQn);
        Assert.Equal("struts", route["framework"]!.GetValue<string>());
        Assert.Equal("/consultar", route["route_path"]!.GetValue<string>());
        Assert.Equal("ANY", route["http_verb"]!.GetValue<string>());
        Assert.Equal("ANY", route["http_method"]!.GetValue<string>());
        Assert.Equal("com.acme.web.ConsultarAction", route["action_type"]!.GetValue<string>());
        Assert.Equal("consultaForm", route["form_bean"]!.GetValue<string>());
        Assert.Equal("com.acme.web.ConsultaForm", route["form_type"]!.GetValue<string>());
        Assert.Equal("request", route["scope"]!.GetValue<string>());
        Assert.Equal("/pages/consulta.jsp", route["input"]!.GetValue<string>());
        Assert.Equal(JavaTestRepo.StrutsConfigPath, route["config_file"]!.GetValue<string>());
        Assert.False(route.ContainsKey("parameter"));
        Assert.Equal("enricher", route["origin"]!.GetValue<string>());
        Assert.Equal("[\"StrutsJspPlugin\"]", route["enriched_by"]!.ToJsonString());

        var handles = Assert.Single(await EdgesAsync(graph, "HANDLES"));
        Assert.Equal(JavaGraphFixture.Qn(JavaTestRepo.ConsultarActionPath, "ConsultarAction"), handles.Source);
        Assert.Equal(RouteQn, handles.Target);
        Assert.Equal("ANY", handles.Properties["http_method"]!.GetValue<string>());
        Assert.Equal("/consultar", handles.Properties["route_path"]!.GetValue<string>());
        Assert.Equal("[\"StrutsJspPlugin\"]", handles.Properties["enriched_by"]!.ToJsonString());
        Assert.Equal(0, run.Result.Stats.ActionClassesUnresolved);
    }

    [Fact]
    public async Task Forwards_and_input_go_to_jsps_and_tiles_definitions_and_global_forwards_are_only_counted()
    {
        using var run = await ApplyAsync();

        var forwards = await EdgesAsync(run.Graph, "FORWARDS_TO");
        Assert.Equal(3, forwards.Count);
        Assert.All(forwards, f => Assert.Equal(RouteQn, f.Source));
        var byTarget = forwards.ToDictionary(f => f.Target, f => f.Properties);
        Assert.Equal("input", byTarget[JspQn(JavaTestRepo.ConsultaJspPath)]["forward_name"]!.GetValue<string>());
        Assert.Equal("ok", byTarget[JspQn(JavaTestRepo.ResultadoJspPath)]["forward_name"]!.GetValue<string>());
        Assert.False(byTarget[JspQn(JavaTestRepo.ResultadoJspPath)]["redirect"]!.GetValue<bool>());
        Assert.Equal([JavaTestRepo.LineOf(JavaTestRepo.StrutsConfigPath, "name=\"ok\"")], Ints(byTarget[JspQn(JavaTestRepo.ResultadoJspPath)]["lines"]));
        Assert.Equal("tile", byTarget[TilesQn("consulta.def")]["forward_name"]!.GetValue<string>());

        var stats = run.Result.Stats;
        Assert.Equal(1, stats.Actions);
        Assert.Equal(3, stats.ForwardsResolved);
        Assert.Equal(0, stats.ForwardsUnresolved);
        Assert.Equal(1, stats.GlobalForwards);
    }

    [Fact]
    public async Task Every_jsp_is_a_node_and_the_one_with_dao_imports_and_sql_is_suspicious()
    {
        using var run = await ApplyAsync();
        var graph = run.Graph;

        Assert.Equal(4, await graph.CountAsync("SELECT COUNT(1) FROM nodes WHERE label = 'Jsp'"));
        var consultaQn = JspQn(JavaTestRepo.ConsultaJspPath);
        var row = Assert.Single(await graph.ListAsync("""
            SELECT name, file_path, file, start_line, line_start, end_line, line_end FROM nodes WHERE qualified_name = @qn AND label = 'Jsp'
            """, r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6)), ("@qn", consultaQn)));
        var lineCount = JavaTestRepo.LineCount(JavaTestRepo.ConsultaJspPath);
        Assert.Equal((JavaTestRepo.ConsultaJspPath, JavaTestRepo.ConsultaJspPath, JavaTestRepo.ConsultaJspPath, 1, 1, lineCount, lineCount), row);

        var consulta = await PropertiesAsync(graph, consultaQn);
        var sqlLine = JavaTestRepo.LineOf(JavaTestRepo.ConsultaJspPath, "SELECT * FROM T");
        Assert.Equal(1, consulta["scriptlet_count"]!.GetValue<int>());
        Assert.Equal([JavaTestRepo.LineOf(JavaTestRepo.ConsultaJspPath, "<%", 4)], Ints(consulta["scriptlet_lines"]));
        Assert.Equal(1, consulta["expression_count"]!.GetValue<int>());
        Assert.Equal(0, consulta["declaration_count"]!.GetValue<int>());
        Assert.Equal(["com.acme.dao.DaoImpl", "java.sql.*"], Strings(consulta["page_imports"]));
        Assert.Equal("[{\"prefix\":\"html\",\"uri\":\"/WEB-INF/struts-html.tld\"}]", consulta["taglibs"]!.ToJsonString());
        Assert.True(consulta["has_sql"]!.GetValue<bool>());
        // El comentario <%-- SELECT oculto FROM X --%> no cuenta: solo la línea del scriptlet.
        Assert.Equal([sqlLine], Ints(consulta["sql_lines"]));
        Assert.True(consulta["session_access"]!.GetValue<bool>());
        Assert.True(consulta["suspicious"]!.GetValue<bool>());
        Assert.Equal(["import:com.acme.dao.DaoImpl", "import:java.sql.*", $"sql:{sqlLine}"], Strings(consulta["suspicious_reasons"]));
        Assert.Equal("utf-8", consulta["encoding"]!.GetValue<string>());
        Assert.Equal("[\"StrutsJspPlugin\"]", consulta["enriched_by"]!.ToJsonString());

        var resultado = await PropertiesAsync(graph, JspQn(JavaTestRepo.ResultadoJspPath));
        Assert.False(resultado["suspicious"]!.GetValue<bool>());
        Assert.False(resultado["has_sql"]!.GetValue<bool>());
        Assert.False(resultado["session_access"]!.GetValue<bool>());
        Assert.Equal(0, resultado["scriptlet_count"]!.GetValue<int>());
        Assert.Empty(Strings(resultado["suspicious_reasons"]));

        Assert.Equal(4, run.Result.Stats.Jsps);
        Assert.Equal(1, run.Result.Stats.Suspicious);
        Assert.Equal(1, run.Result.Stats.JspsWithScriptlets);
    }

    [Fact]
    public async Task Forms_submit_to_the_action_and_includes_link_jsps()
    {
        using var run = await ApplyAsync();

        var submit = Assert.Single(await EdgesAsync(run.Graph, "SUBMITS_TO"));
        Assert.Equal((JspQn(JavaTestRepo.ConsultaJspPath), RouteQn), (submit.Source, submit.Target));
        Assert.Equal([JavaTestRepo.LineOf(JavaTestRepo.ConsultaJspPath, "<html:form")], Ints(submit.Properties["lines"]));

        var includes = await EdgesAsync(run.Graph, "INCLUDES");
        Assert.Equal(2, includes.Count);
        var cabecera = JspQn(JavaTestRepo.CabeceraJspPath);
        var directive = includes.Single(i => i.Source == JspQn(JavaTestRepo.ConsultaJspPath));
        Assert.Equal(cabecera, directive.Target);
        Assert.Equal(["include"], Strings(directive.Properties["kinds"]));
        Assert.Equal([JavaTestRepo.LineOf(JavaTestRepo.ConsultaJspPath, "<%@ include")], Ints(directive.Properties["lines"]));
        var dynamic = includes.Single(i => i.Source == JspQn(JavaTestRepo.LayoutJspPath));
        Assert.Equal(cabecera, dynamic.Target);
        Assert.Equal(["jsp:include"], Strings(dynamic.Properties["kinds"]));
    }

    [Fact]
    public async Task Tiles_definitions_render_their_template_and_puts_and_extend_their_parent()
    {
        using var run = await ApplyAsync();
        var graph = run.Graph;

        var definition = await PropertiesAsync(graph, TilesQn("consulta.def"));
        Assert.Equal("/pages/layout.jsp", definition["template"]!.GetValue<string>());
        Assert.Equal("{\"body\":\"/pages/resultado.jsp\"}", definition["puts"]!.ToJsonString());
        Assert.Equal(JavaTestRepo.TilesDefsPath, definition["config_file"]!.GetValue<string>());
        Assert.Equal(JavaTestRepo.LineOf(JavaTestRepo.TilesDefsPath, "name=\"consulta.def\""),
            await graph.CountAsync("SELECT line_start FROM nodes WHERE qualified_name = @qn", ("@qn", TilesQn("consulta.def"))));
        Assert.Equal("consulta.def", (await PropertiesAsync(graph, TilesQn("consulta.hija")))["extends"]!.GetValue<string>());

        var renders = (await EdgesAsync(graph, "RENDERS")).ToDictionary(r => r.Target, r => r);
        Assert.Equal(2, renders.Count);
        Assert.Equal(TilesQn("consulta.def"), renders[JspQn(JavaTestRepo.LayoutJspPath)].Source);
        Assert.Equal("template", renders[JspQn(JavaTestRepo.LayoutJspPath)].Properties["role"]!.GetValue<string>());
        Assert.Equal("put:body", renders[JspQn(JavaTestRepo.ResultadoJspPath)].Properties["role"]!.GetValue<string>());

        var extends = Assert.Single(await EdgesAsync(graph, "EXTENDS_DEFINITION"));
        Assert.Equal((TilesQn("consulta.hija"), TilesQn("consulta.def")), (extends.Source, extends.Target));
        Assert.Equal(2, run.Result.Stats.TilesDefinitions);
        Assert.Equal(0, run.Result.Stats.TilesReferencesUnresolved);
    }

    [Fact]
    public async Task Created_nodes_fill_file_and_line_columns()
    {
        using var run = await ApplyAsync();

        Assert.Equal(7, await run.Graph.CountAsync($"SELECT COUNT(1) FROM nodes WHERE {EnrichmentMarks.CreatedByPredicate("'StrutsJspPlugin'")}"));
        Assert.Equal(0, await run.Graph.CountAsync($"""
            SELECT COUNT(1) FROM nodes
            WHERE {EnrichmentMarks.CreatedByPredicate("'StrutsJspPlugin'")}
              AND (file IS NULL OR file = '' OR file <> file_path OR line_start < 1 OR line_start <> start_line
                   OR line_end < line_start OR line_end <> end_line OR project <> @project)
            """, ("@project", JavaGraphFixture.Project)));
    }

    [Fact]
    public async Task The_pass_leaves_a_complete_enrichment_runs_row()
    {
        using var run = await ApplyAsync();

        var row = Assert.Single(await EnrichmentRuns.ListAsync(run.Graph.Connection, CancellationToken.None));
        Assert.Equal(run.Result.Outcome.RunId, row.Id);
        Assert.Equal(StrutsJspWriter.EnricherName, row.Enricher);
        Assert.Equal(StrutsJspWriter.EnricherVersion, row.EnricherVersion);
        Assert.Equal($"{JavaGraphFixture.Project}|{JavaGraphFixture.IndexedAt}", row.IndexFingerprint);
        Assert.Equal("cfg", row.ConfigFingerprint);
        Assert.Equal(StartedAt, row.StartedAt);
        Assert.Equal(EnrichmentRuns.StatusOk, row.Status);

        var outcome = row.Outcome!;
        Assert.Equal(6, outcome.FilesAnalyzed);
        Assert.Equal(7, outcome.NodesCreated);
        Assert.Equal(10, outcome.EdgesCreated);
        Assert.Equal(outcome with { RunId = row.Id }, run.Result.Outcome);
        var details = JsonNode.Parse(outcome.Details!)!;
        Assert.Equal(1, details["global_forwards"]!.GetValue<int>());
        Assert.Equal(3, details["edges"]!["FORWARDS_TO"]!.GetValue<int>());
        Assert.Equal(0, JsonNode.Parse(outcome.Unresolved!)!["forwards"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_second_pass_leaves_an_identical_graph()
    {
        using var run = await ApplyAsync();
        var graph = run.Graph;

        async Task<List<string>> SnapshotAsync() => [
            .. await graph.ListAsync("""
                SELECT label || ' ' || qualified_name || ' ' || file || ' ' || line_start || '-' || line_end || ' ' || properties
                FROM nodes ORDER BY 1
                """, r => r.GetString(0)),
            .. await graph.ListAsync("""
                SELECT e.type || ' ' || s.qualified_name || ' -> ' || t.qualified_name || ' ' || e.properties
                FROM edges e JOIN nodes s ON s.id = e.source_id JOIN nodes t ON t.id = e.target_id ORDER BY 1
                """, r => r.GetString(0))
        ];

        var before = await SnapshotAsync();
        var second = await run.ApplyAgainAsync();

        Assert.Equal(before, await SnapshotAsync());
        var first = run.Result.Outcome;
        Assert.Equal((first.FilesAnalyzed, first.NodesCreated, first.EdgesCreated, first.Unresolved, first.Diagnostics),
            (second.Outcome.FilesAnalyzed, second.Outcome.NodesCreated, second.Outcome.EdgesCreated, second.Outcome.Unresolved, second.Outcome.Diagnostics));
        Assert.Equal(run.Result.Outcome.NodesCreated, second.Stats.PreviousNodesRemoved);
        Assert.Equal(run.Result.Outcome.EdgesCreated, second.Stats.PreviousEdgesRemoved);
        Assert.Equal(2, (await EnrichmentRuns.ListAsync(graph.Connection, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Missing_forward_targets_are_counted_without_edges()
    {
        using var run = await ApplyAsync(prepare: repo => Write(repo, "WebContent/WEB-INF/struts-config2.xml", """
            <?xml version="1.0" encoding="ISO-8859-1"?>
            <struts-config>
              <action-mappings>
                <action path="/otra" type="org.apache.struts.actions.DispatchAction" parameter="metodo">
                  <forward name="falta" path="/pages/noexiste.jsp"/>
                  <forward name="def" path="def.noexiste"/>
                  <forward name="encadena" path="/consultar.do?x=1" redirect="true"/>
                </action>
                <action path="/consultar" type="com.acme.web.Otra"/>
              </action-mappings>
            </struts-config>
            """, Encoding.Latin1));

        var stats = run.Result.Stats;
        Assert.Equal(2, stats.ForwardsUnresolved);
        Assert.Equal(4, stats.ForwardsResolved);
        Assert.Equal(1, stats.DuplicateActions);
        Assert.Equal(1, stats.ActionClassesUnresolved);
        Assert.Equal(2, stats.Actions);

        var fromOtra = (await EdgesAsync(run.Graph, "FORWARDS_TO")).Where(f => f.Source == "__route__ANY__/otra").ToList();
        var chained = Assert.Single(fromOtra);
        Assert.Equal(RouteQn, chained.Target);
        Assert.True(chained.Properties["redirect"]!.GetValue<bool>());
        Assert.Equal("metodo", (await PropertiesAsync(run.Graph, "__route__ANY__/otra"))["parameter"]!.GetValue<string>());

        var sample = JsonNode.Parse(run.Result.Outcome.Details!)!["unresolved_forwards_sample"]!.AsArray();
        Assert.Equal(["/pages/noexiste.jsp", "def.noexiste"], sample.Select(s => s!["path"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Unreadable_config_and_jsp_are_counted_and_the_pass_goes_on()
    {
        using var run = await ApplyAsync(
            prepare: repo => Write(repo, "WebContent/WEB-INF/struts-config-roto.xml", "<struts-config><action-mappings><action path=\"/x\">"),
            afterScan: repo => File.Delete(Path.Combine(repo, JavaTestRepo.CabeceraJspPath.Replace('/', Path.DirectorySeparatorChar))));

        var stats = run.Result.Stats;
        Assert.Equal(1, stats.UnreadableConfigs);
        Assert.Equal(1, stats.UnreadableJsps);
        Assert.Equal(1, stats.Actions);
        Assert.Equal(3, stats.Jsps);
        Assert.Equal(2, stats.IncludesUnresolved);
        Assert.Empty(await EdgesAsync(run.Graph, "INCLUDES"));

        var diagnostics = JsonNode.Parse(run.Result.Outcome.Diagnostics!)!;
        Assert.StartsWith("WebContent/WEB-INF/struts-config-roto.xml: ", diagnostics["unreadable_configs"]![0]!.GetValue<string>());
        Assert.StartsWith(JavaTestRepo.CabeceraJspPath + ": ", diagnostics["unreadable_jsps"]![0]!.GetValue<string>());
        Assert.Single(await EnrichmentRuns.ListAsync(run.Graph.Connection, CancellationToken.None));
    }

    [Fact]
    public async Task Coexists_with_the_java_analyzer_output()
    {
        using var run = await ApplyAsync();
        var java = await JavaGraphWriter.ApplyAsync(JavaGraphFixture.Request(run.Graph.DatabasePath),
            JavaGraphFixture.ResultLines().Select(l => JavaAnalysisReader.Parse(l)!), CancellationToken.None);
        var javaCreated = await run.Graph.CountAsync($"SELECT COUNT(1) FROM nodes WHERE {EnrichmentMarks.CreatedByPredicate("'JavaAnalyzer'")}");

        await run.ApplyAgainAsync();

        Assert.Equal(java.Outcome.NodesCreated, javaCreated);
        Assert.Equal(javaCreated, await run.Graph.CountAsync($"SELECT COUNT(1) FROM nodes WHERE {EnrichmentMarks.CreatedByPredicate("'JavaAnalyzer'")}"));
        Assert.Single(await EdgesAsync(run.Graph, "HANDLES"));
        Assert.Equal(7, await run.Graph.CountAsync($"SELECT COUNT(1) FROM nodes WHERE {EnrichmentMarks.CreatedByPredicate("'StrutsJspPlugin'")}"));
    }

    [Fact]
    public async Task Never_writes_to_the_cbm_cache()
    {
        using var temp = new TempDirectory();
        var repo = JavaTestRepo.WriteTo(temp.Path);
        var cacheDb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "codebase-memory-mcp", $"no-existe-{Guid.NewGuid():N}.db");

        await Assert.ThrowsAsync<InvalidOperationException>(() => StrutsJspWriter.ApplyAsync(cacheDb, RepositoryScan.Scan(repo, []),
            new StrutsJspOptions(), Encoding.UTF8, "cfg", StartedAt, CancellationToken.None));
        Assert.False(File.Exists(cacheDb));
    }

    [Fact]
    public void Applicability_and_fingerprint_follow_the_options()
    {
        var scan = new RepositoryScanResult { Root = "C:/r", StrutsConfigs = ["WEB-INF/struts-config.xml"] };
        Assert.True(StrutsJspWriter.IsApplicable(scan, new StrutsJspOptions()));
        Assert.False(StrutsJspWriter.IsApplicable(scan, new StrutsJspOptions { Enabled = false }));
        Assert.False(StrutsJspWriter.IsApplicable(scan with { StrutsConfigs = [] }, new StrutsJspOptions()));

        var defaults = StrutsJspWriter.ConfigFingerprint(new StrutsJspOptions(), Encoding.Latin1);
        Assert.Equal(defaults, StrutsJspWriter.ConfigFingerprint(new StrutsJspOptions(), Encoding.Latin1));
        Assert.NotEqual(defaults, StrutsJspWriter.ConfigFingerprint(new StrutsJspOptions(), Encoding.UTF8));
        Assert.NotEqual(defaults, StrutsJspWriter.ConfigFingerprint(new StrutsJspOptions { SuspiciousImportPatterns = ["x"] }, Encoding.Latin1));
        Assert.NotEqual(defaults, StrutsJspWriter.ConfigFingerprint(new StrutsJspOptions { BusinessImportPatterns = ["x"] }, Encoding.Latin1));
        // El mismo patrón como fuerte o como de negocio no da la misma huella.
        Assert.NotEqual(
            StrutsJspWriter.ConfigFingerprint(new StrutsJspOptions { SuspiciousImportPatterns = ["x"], BusinessImportPatterns = [] }, Encoding.Latin1),
            StrutsJspWriter.ConfigFingerprint(new StrutsJspOptions { SuspiciousImportPatterns = [], BusinessImportPatterns = ["x"] }, Encoding.Latin1));

        // Una pasada nueva del analizador Java cambia la huella: el plugin se repite sobre el grafo reescrito.
        Assert.Equal(defaults, StrutsJspWriter.ConfigFingerprint(new StrutsJspOptions(), Encoding.Latin1, null));
        Assert.NotEqual(defaults, StrutsJspWriter.ConfigFingerprint(new StrutsJspOptions(), Encoding.Latin1, 7));
        Assert.NotEqual(StrutsJspWriter.ConfigFingerprint(new StrutsJspOptions(), Encoding.Latin1, 7),
            StrutsJspWriter.ConfigFingerprint(new StrutsJspOptions(), Encoding.Latin1, 8));
    }

    [Fact]
    public async Task A_business_import_only_marks_the_jsp_when_a_scriptlet_uses_it()
    {
        const string expressionOnly = "WebContent/pages/solo_expresion.jsp";
        const string inScriptlet = "WebContent/pages/en_scriptlet.jsp";
        using var run = await ApplyAsync(prepare: repo =>
        {
            Write(repo, expressionOnly, """
                <%@ page import="com.acme.negocio.Servicio" %>
                <p><%= Servicio.class.getName() %></p>

                """);
            Write(repo, inScriptlet, """
                <%@ page import="com.acme.negocio.Servicio" %>
                <% new Servicio().ejecutar(); %>

                """);
        });

        var clean = await PropertiesAsync(run.Graph, JspQn(expressionOnly));
        Assert.False(clean["suspicious"]!.GetValue<bool>());
        Assert.Empty(Strings(clean["suspicious_reasons"]));
        Assert.Equal(["com.acme.negocio.Servicio"], Strings(clean["business_imports"]));

        var suspicious = await PropertiesAsync(run.Graph, JspQn(inScriptlet));
        Assert.True(suspicious["suspicious"]!.GetValue<bool>());
        Assert.Equal(["business-code-in-scriptlet:com.acme.negocio.Servicio"], Strings(suspicious["suspicious_reasons"]));
        Assert.Equal(["com.acme.negocio.Servicio"], Strings(suspicious["business_imports"]));

        // consulta.jsp (imports de DAO y SQL) y en_scriptlet.jsp.
        Assert.Equal(2, run.Result.Stats.Suspicious);
    }
}

public class JspScannerTests
{
    private static readonly JspScanner Scanner = new(new StrutsJspOptions().SuspiciousImportPatterns, new StrutsJspOptions().BusinessImportPatterns);

    [Fact]
    public void Sql_inside_a_jsp_comment_does_not_count()
    {
        var result = Scanner.Scan("""
            <%-- SELECT a FROM b --%>
            <%--
              <% String s = "DELETE FROM T"; session.invalidate(); %>
            --%>
            <p>Sin código</p>

            """);

        Assert.False(result.HasSql);
        Assert.False(result.SessionAccess);
        Assert.False(result.Suspicious);
        Assert.Equal(0, result.ScriptletCount);
        Assert.Equal(5, result.LineCount);
    }

    [Fact]
    public void Elements_are_counted_by_kind_with_their_lines()
    {
        var result = Scanner.Scan("""
            <%@ page import="java.util.List" %>
            <%! private int n = 0; %>
            <% n++; %>
            <%= n %>
            <%
              String q = "UPDATE CUENTAS " +
                         "SET SALDO = 0";
            %>
            """);

        Assert.Equal(2, result.ScriptletCount);
        Assert.Equal([3, 5], result.ScriptletLines);
        Assert.Equal(1, result.ExpressionCount);
        Assert.Equal(1, result.DeclarationCount);
        Assert.Equal(["java.util.List"], result.PageImports);
        Assert.Equal([6], result.SqlLines);
        Assert.Equal(["sql:6"], result.SuspiciousReasons);
    }

    [Fact]
    public void Custom_prefixes_and_dynamic_targets()
    {
        var result = Scanner.Scan("""
            <%@ taglib uri="http://struts.apache.org/tags-html" prefix="h" %>
            <%@ taglib uri="/WEB-INF/struts-tiles.tld" prefix="t" %>
            <h:form action="/buscar.do?modo=1" onsubmit="return check('a > b')">
            <html:form action="<%= destino %>">
            <jsp:include page="${pagina}"/>
            <t:insert page="/pie.jsp"/>
            <t:insert attribute="cuerpo"/>
            <%@ include file="../comun/cabecera.jspf" %>
            """);

        Assert.Equal([new JspReference(JspScanner.KindForm, "/buscar.do?modo=1", 3)], result.Forms);
        Assert.Equal([
            new JspReference(JspScanner.KindIncludeDirective, "../comun/cabecera.jspf", 8),
            new JspReference(JspScanner.KindTilesInsert, "/pie.jsp", 6)
        ], result.Includes.OrderBy(i => i.Kind, StringComparer.Ordinal));
        Assert.Equal(2, result.DynamicReferences);
        Assert.Equal(["h", "t"], result.Taglibs.Select(t => t.Prefix));
    }

    [Fact]
    public void Suspicious_imports_follow_the_configured_patterns()
    {
        var result = Scanner.Scan("""<%@ page import="geo.negocio.utilidades.Formateo, java.util.Map, geo.DAO.acciones.DAOAcciones" %>""");

        // El import de negocio no se usa en ningún scriptlet: queda anotado, pero no marca la JSP.
        Assert.Equal(["import:geo.DAO.acciones.DAOAcciones"], result.SuspiciousReasons);
        Assert.Equal(["geo.negocio.utilidades.Formateo"], result.BusinessImports);
        Assert.Throws<InvalidOperationException>(() => new JspScanner(["("]));
        Assert.Throws<InvalidOperationException>(() => new JspScanner([], ["("]));
    }

    [Theory]
    [InlineData("<p><%= Servicio.nombre() %></p>", false)]
    [InlineData("<% String s = \"Servicio.nombre()\"; // Servicio.x()\n %>", false)]
    [InlineData("<% Servicio.ejecutar(); %>", true)]
    [InlineData("<% Servicio s = null; %>", true)]
    [InlineData("<%! private Servicio servicio; %>", true)]
    public void A_class_import_of_business_code_marks_only_when_used_in_a_scriptlet_or_declaration(string body, bool suspicious)
    {
        var result = Scanner.Scan("<%@ page import=\"com.acme.negocio.Servicio\" %>\n" + body);

        Assert.Equal(suspicious, result.Suspicious);
        Assert.Equal(suspicious ? new[] { "business-code-in-scriptlet:com.acme.negocio.Servicio" } : [], result.SuspiciousReasons);
        Assert.Equal(["com.acme.negocio.Servicio"], result.BusinessImports);
    }

    [Theory]
    [InlineData("<% String s = String.valueOf(1); List l = null; %>", false)]
    [InlineData("<%= Formateo.fecha(x) %>", false)]
    [InlineData("<% Lista.crear(); %>", false)]
    [InlineData("<% String f = Formateo.fecha(x); %>", true)]
    [InlineData("<% new Calculadora(); %>", true)]
    public void A_wildcard_business_import_marks_when_a_scriptlet_uses_a_class_not_imported_elsewhere(string body, bool suspicious)
    {
        var result = Scanner.Scan("<%@ page import=\"geo.negocio.utilidades.*, java.util.List, com.acme.Lista\" %>\n" + body);

        Assert.Equal(suspicious, result.Suspicious);
        Assert.Equal(suspicious ? new[] { "business-code-in-scriptlet:geo.negocio.utilidades.*" } : [], result.SuspiciousReasons);
    }

    [Fact]
    public void Declared_encoding_wins_and_undeclared_follows_the_source_rule()
    {
        var utf8Bytes = new UTF8Encoding(false).GetBytes("<%@ page pageEncoding=\"ISO-8859-1\" %>año");
        var (declaredText, declared) = JspScanner.Decode(utf8Bytes, Encoding.UTF8);
        Assert.Equal("iso-8859-1", declared.WebName);
        Assert.EndsWith("aÃ±o", declaredText);

        var contentType = Encoding.Latin1.GetBytes("<%@ page contentType=\"text/html; charset=UTF-8\" %>x");
        Assert.Equal("utf-8", JspScanner.Decode(contentType, Encoding.Latin1).Encoding.WebName);

        var latin1 = Encoding.Latin1.GetBytes("<p>año</p>");
        var (text, fallback) = JspScanner.Decode(latin1, Encoding.Latin1);
        Assert.Equal("iso-8859-1", fallback.WebName);
        Assert.Equal("<p>año</p>", text);
        Assert.Equal("utf-8", JspScanner.Decode(new UTF8Encoding(false).GetBytes("<p>año</p>"), Encoding.Latin1).Encoding.WebName);
    }
}

public class StrutsConfigParserTests
{
    [Fact]
    public void Doctype_is_ignored_and_lines_come_from_the_xml()
    {
        var text = JavaTestRepo.TextOf(JavaTestRepo.StrutsConfigPath);
        using var stream = new MemoryStream(Encoding.Latin1.GetBytes(text));

        var config = StrutsConfigParser.Parse(JavaTestRepo.StrutsConfigPath, stream);

        Assert.Equal("com.acme.web.ConsultaForm", config.FormBeans["consultaForm"]);
        Assert.Equal([new StrutsForward("inicio", "/pages/consulta.jsp", false, JavaTestRepo.LineOf(JavaTestRepo.StrutsConfigPath, "name=\"inicio\""))],
            config.GlobalForwards);
        var action = Assert.Single(config.Actions);
        Assert.Equal(JavaTestRepo.LineOf(JavaTestRepo.StrutsConfigPath, "<action path"), action.Line);
        Assert.Equal(["ok", "tile"], action.Forwards.Select(f => f.Name));
    }

    [Fact]
    public void Tiles_aliases_and_put_bodies_are_read()
    {
        using var stream = new MemoryStream(Encoding.Latin1.GetBytes("""
            <?xml version="1.0" encoding="ISO-8859-1"?>
            <!DOCTYPE tiles-definitions PUBLIC "-//Apache Software Foundation//DTD Tiles Configuration 1.1//EN" "http://jakarta.apache.org/struts/dtds/tiles-config_1_1.dtd">
            <tiles-definitions>
              <definition name="antigua" page="/plantilla.jsp">
                <put name="titulo" value="Menú" type="string"/>
                <put name="cuerpo">/cuerpo.jsp</put>
              </definition>
            </tiles-definitions>
            """));

        var definition = Assert.Single(TilesDefinitionsParser.Parse("tiles.xml", stream).Definitions);

        Assert.Equal("/plantilla.jsp", definition.Template);
        Assert.Equal(4, definition.Line);
        Assert.Equal([new TilesPut("titulo", "Menú", "string", 5), new TilesPut("cuerpo", "/cuerpo.jsp", null, 6)], definition.Puts);
    }
}
