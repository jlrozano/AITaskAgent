using System.Text;
using System.Text.Json;
using AITaskAgent.Configuration;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Enrichment.Web;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tests.Enrichment;
using CodebaseAnalysis.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Facetas de <see cref="SqlFacetExtractorStep"/> tras la auditoría del 2026-09-29 (H-1, H-2): lo desconocido no se presenta
/// como negativo, cada <c>LIMIT</c> tiene un orden determinista, las métricas que faltan se calculan del grafo con su
/// procedencia, y el material Struts/JSP llega a los informes. Sobre grafos sintéticos de <see cref="CbmTestGraph"/>, el de
/// <see cref="JavaGraphFixture"/> con el plugin Struts/JSP aplicado y, con JDK, el del analizador Java.
/// </summary>
public sealed class FacetExtractionTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private async Task<ReportFacetInput> FacetsAsync(string databasePath, string? repository = null)
    {
        var context = new PipelineContext();
        context.Set(AnalysisContextKeys.PipelineInput, new AnalysisPipelineInput
        {
            RepositoryPath = repository ?? _temp.Path,
            DatabasePath = databasePath,
            OutputReportsPath = Path.Combine(_temp.Path, "reports")
        });
        context.Set(AnalysisContextKeys.Reporter, new CapturingReporter());
        return await new SqlFacetExtractorStep().ExecuteAsync(new GraphStatusResult { DatabasePath = databasePath }, context, 1, null, CancellationToken.None);
    }

    private List<JsonElement> Rows(string facet, string name)
    {
        output.WriteLine($"--- {name}\n{facet}");
        Assert.DoesNotContain("Faceta no disponible", facet);
        using var doc = JsonDocument.Parse(facet);
        return [.. doc.RootElement.EnumerateArray().Select(e => e.Clone())];
    }

    private static string? Text(JsonElement row, string property) =>
        row.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;

    private static JsonElement Row(List<JsonElement> rows, string qualifiedName) =>
        Assert.Single(rows, r => Text(r, "qualified_name") == qualifiedName);

    [Fact]
    public async Task Unknown_fan_in_and_auth_are_not_presented_as_zero_or_unprotected()
    {
        using var g = await CbmTestGraph.CreateAsync(Path.Combine(_temp.Path, "codebase.db"), "demo");
        await g.NodeAsync("Class", "SinUso", "demo.SinUso", "src/SinUso.cs", 1, 40, "{\"fan_in\":0}");
        await g.NodeAsync("Class", "SinDato", "demo.SinDato", "src/SinDato.cs", 1, 90);
        await g.NodeAsync("Route", "/abierta", "__route__GET__/abierta", "src/Api.cs", 3, 3, "{\"route_path\":\"/abierta\",\"http_verb\":\"GET\",\"auth_required\":false}");
        await g.NodeAsync("Route", "/protegida", "__route__GET__/protegida", "src/Api.cs", 5, 5, "{\"route_path\":\"/protegida\",\"http_verb\":\"GET\",\"auth_required\":true}");
        await g.NodeAsync("Route", "/sin-dato", "__route__ANY__/sin-dato", "WEB-INF/struts-config.xml", 7, 7, "{\"route_path\":\"/sin-dato\",\"http_method\":\"ANY\"}");

        var facets = await FacetsAsync(g.DatabasePath);

        var zero = Rows(facets.ZeroFaninClasses, "zero_fanin_classes");
        Assert.Equal(["demo.SinUso"], zero.Select(r => Text(r, "qualified_name")));
        Assert.Equal(SqlFacetExtractorStep.SourceGraph, Text(zero[0], "fan_in_source"));

        Assert.Equal(["__route__GET__/abierta"], Rows(facets.UnvalidatedRoutes, "unvalidated_routes").Select(r => Text(r, "qualified_name")));
        var matrix = Rows(facets.RoutesSecurityMatrix, "routes_security_matrix");
        Assert.Equal("desconocido", Text(Row(matrix, "__route__ANY__/sin-dato"), "auth_required"));
        Assert.Equal("0", Text(Row(matrix, "__route__GET__/abierta"), "auth_required"));
        // El verbo sale de la propiedad (http_method si no hay http_verb); 'QUERY' ya no se inventa para una ruta.
        Assert.Equal("ANY", Text(Row(Rows(facets.RoutesSummary, "routes_summary"), "__route__ANY__/sin-dato"), "verb"));
    }

    /// <summary>
    /// <c>obsolete_elements</c> ve el <c>@Deprecated</c> de Java, que el analizador escribe en <c>annotations</c> con el nombre
    /// simple, además del <c>[Obsolete]</c> de C# en <c>attributes</c>. Una anotación que solo contiene el nombre no cuenta.
    /// </summary>
    [Fact]
    public async Task Obsolete_elements_include_java_deprecated_annotations()
    {
        using var g = await CbmTestGraph.CreateAsync(Path.Combine(_temp.Path, "codebase.db"), "demo");
        await g.NodeAsync("Method", "viejo", "demo.Svc.viejo", "src/Svc.java", 3, 5, "{\"annotations\":[\"Override\",\"Deprecated\"]}");
        await g.NodeAsync("Class", "Antigua", "demo.Antigua", "src/Antigua.java", 1, 9, "{\"annotations\":[\"Deprecated\"]}");
        await g.NodeAsync("Method", "actual", "demo.Svc.actual", "src/Svc.java", 7, 9, "{\"annotations\":[\"Override\",\"NotDeprecatedYet\"]}");
        await g.NodeAsync("Method", "Legado", "demo.Api.Legado", "src/Api.cs", 1, 4, "{\"attributes\":[{\"name\":\"Obsolete\"}]}");
        await g.NodeAsync("Method", "sinNada", "demo.Svc.sinNada", "src/Svc.java", 11, 12, "{\"annotations\":[]}");

        var facets = await FacetsAsync(g.DatabasePath);

        Assert.Equal(["demo.Antigua", "demo.Api.Legado", "demo.Svc.viejo"],
            Rows(facets.ObsoleteElements, "obsolete_elements").Select(r => Text(r, "qualified_name")));
    }

    [Fact]
    public async Task Limited_facets_are_ordered_by_their_metric_and_then_by_name_whatever_the_insertion_order()
    {
        using var g = await CbmTestGraph.CreateAsync(Path.Combine(_temp.Path, "codebase.db"), "demo");
        // Insertadas al revés del orden esperado.
        await g.NodeAsync("Route", "/c", "__route__GET__/c", "src/Api.cs", 9, 9, "{\"route_path\":\"/c\"}");
        await g.NodeAsync("Route", "/a", "__route__GET__/a", "src/Api.cs", 3, 3, "{\"route_path\":\"/a\"}");
        await g.NodeAsync("Route", "/b", "__route__GET__/b", "src/Api.cs", 5, 5, "{\"route_path\":\"/b\"}");
        await g.NodeAsync("Method", "m2", "demo.B.m2", "src/B.cs", 1, 5, "{\"cyclomatic\":4}");
        await g.NodeAsync("Method", "m1", "demo.A.m1", "src/A.cs", 1, 5, "{\"cyclomatic\":4}");
        await g.NodeAsync("Method", "m0", "demo.C.m0", "src/C.cs", 1, 5, "{\"cyclomatic\":9}");

        var facets = await FacetsAsync(g.DatabasePath);

        Assert.Equal(["/a", "/b", "/c"], Rows(facets.RoutesSummary, "routes_summary").Select(r => Text(r, "path")));
        Assert.Equal(["demo.C.m0", "demo.A.m1", "demo.B.m2"],
            Rows(facets.HighCyclomaticMethods, "high_cyclomatic_methods").Select(r => Text(r, "qualified_name")));
    }

    /// <summary>
    /// Sin <c>fan_in</c>, <c>fan_out</c>, <c>loc_total</c>, <c>method_count</c> ni <c>cyclomatic_total</c> en las propiedades,
    /// las facetas los calculan de las aristas y dicen de dónde salen. Las CALLS heurísticas de cbm no cuentan, las llamadas
    /// dentro del propio tipo tampoco, y un tipo sin la marca del enriquecedor que resolvió las llamadas queda sin dato.
    /// </summary>
    [Fact]
    public async Task Missing_metrics_are_computed_from_resolved_calls_members_and_line_ranges_and_say_so()
    {
        using var g = await CbmTestGraph.CreateAsync(Path.Combine(_temp.Path, "codebase.db"), "demo");
        const string marked = "\"enriched_by\":[\"JavaAnalyzer\"]";
        const string resolved = "{\"enriched_by\":[\"JavaAnalyzer\"],\"count\":1}";

        var svc = await g.NodeAsync("Class", "Svc", "demo.Svc", "src/Svc.java", 1, 30, $"{{{marked}}}");
        var run = await g.NodeAsync("Method", "run", "demo.Svc.run", "src/Svc.java", 3, 12, $"{{{marked},\"cyclomatic\":3}}");
        var helper = await g.NodeAsync("Method", "helper", "demo.Svc.helper", "src/Svc.java", 14, 20, $"{{{marked},\"cyclomatic\":2}}");
        var dao = await g.NodeAsync("Class", "Dao", "demo.Dao", "src/Dao.java", 1, 10, $"{{{marked}}}");
        var daoCtor = await g.NodeAsync("Method", "Dao", "demo.Dao.Dao", "src/Dao.java", 2, 2, $"{{{marked},\"is_constructor\":true,\"cyclomatic\":1}}");
        var find = await g.NodeAsync("Method", "find", "demo.Dao.find", "src/Dao.java", 4, 8, $"{{{marked},\"cyclomatic\":1}}");
        var unused = await g.NodeAsync("Class", "Unused", "demo.Unused", "src/Unused.java", 1, 50, $"{{{marked}}}");
        var unusedCtor = await g.NodeAsync("Method", "Unused", "demo.Unused.Unused", "src/Unused.java", 2, 2, $"{{{marked},\"is_constructor\":true}}");
        var legacy = await g.NodeAsync("Class", "Legacy", "demo.Legacy", "src/Legacy.kt", 1, 20);
        var legacyRun = await g.NodeAsync("Method", "go", "demo.Legacy.go", "src/Legacy.kt", 2, 4);
        foreach (var (type, method) in new[] { (svc, run), (svc, helper), (dao, daoCtor), (dao, find), (unused, unusedCtor), (legacy, legacyRun) })
            await g.EdgeAsync(type, method, "DEFINES_METHOD", $"{{{marked}}}");

        await g.EdgeAsync(run, daoCtor, "CALLS", resolved);
        await g.EdgeAsync(run, find, "CALLS", resolved);
        await g.EdgeAsync(run, helper, "CALLS", resolved);
        // Heurística de cbm hacia Unused y llamada del LLM: no cuentan.
        await g.EdgeAsync(legacyRun, unusedCtor, "CALLS", "{\"strategy\":\"suffix_match\",\"confidence\":0.4}");
        await g.EdgeAsync(legacyRun, find, "CALLS", "{\"enriched_by\":[\"SemanticEnricherAgent\"]}");

        var facets = await FacetsAsync(g.DatabasePath);
        const string fromCalls = "calculado: aristas CALLS resueltas por JavaAnalyzer";

        var coupling = Rows(facets.CouplingMetrics, "coupling_metrics");
        var daoRow = Row(coupling, "demo.Dao");
        Assert.Equal("1", Text(daoRow, "fan_in"));
        Assert.Equal(fromCalls, Text(daoRow, "fan_in_source"));
        // Svc llama a dos métodos de Dao; la llamada a helper es interna.
        var svcRow = Row(coupling, "demo.Svc");
        Assert.Equal("0", Text(svcRow, "fan_in"));
        Assert.Equal("2", Text(svcRow, "fan_out"));
        Assert.Equal(fromCalls, Text(svcRow, "fan_out_source"));
        Assert.Equal("1", Text(Row(coupling, "demo.Svc.helper"), "fan_in"));
        Assert.Equal("1", Text(Row(coupling, "demo.Dao.find"), "fan_in"));
        Assert.DoesNotContain(coupling, r => Text(r, "qualified_name") is "demo.Legacy" or "demo.Legacy.go");

        var bloated = Rows(facets.BloatedClasses, "bloated_classes");
        var svcSize = Row(bloated, "demo.Svc");
        Assert.Equal("30", Text(svcSize, "loc_total"));
        Assert.Equal(SqlFacetExtractorStep.SourceLineRange, Text(svcSize, "loc_source"));
        Assert.Equal("2", Text(svcSize, "method_count"));
        Assert.Equal(SqlFacetExtractorStep.SourceDefinesMethod, Text(svcSize, "method_count_source"));
        Assert.Equal("demo.Unused", Text(bloated[0], "qualified_name"));

        var complex = Rows(facets.ComplexClasses, "complex_classes");
        Assert.Equal("5", Text(Row(complex, "demo.Svc"), "cyclomatic_total"));
        Assert.Equal(SqlFacetExtractorStep.SourceCyclomaticSum, Text(Row(complex, "demo.Svc"), "cyclomatic_total_source"));
        // Un método de Unused no tiene cyclomatic: la suma sería parcial y no se da.
        Assert.Null(Text(Row(complex, "demo.Unused"), "cyclomatic_total"));

        // Cero conocido y con constructor declarado: solo Unused (la heurística de cbm no cuenta). Svc tiene cero pero no
        // declara constructor, y Legacy no tiene dato.
        var zero = Assert.Single(Rows(facets.ZeroFaninClasses, "zero_fanin_classes"));
        Assert.Equal(("demo.Unused", "0", fromCalls), (Text(zero, "qualified_name"), Text(zero, "fan_in"), Text(zero, "fan_in_source")));
    }

    [Fact]
    public async Task A_zero_computed_fan_in_is_not_listed_for_types_without_a_declared_constructor_a_route_or_subtypes()
    {
        using var g = await CbmTestGraph.CreateAsync(Path.Combine(_temp.Path, "codebase.db"), "demo");
        const string marked = "{\"enriched_by\":[\"JavaAnalyzer\"]}";
        var caller = await g.NodeAsync("Method", "main", "demo.App.main", "src/App.java", 1, 3, marked);
        var target = await g.NodeAsync("Method", "x", "demo.Other.x", "src/Other.java", 1, 3, marked);
        await g.EdgeAsync(caller, target, "CALLS", marked);
        await g.NodeAsync("Class", "SinConstructor", "demo.SinConstructor", "src/SinConstructor.java", 1, 9, marked);
        var action = await g.NodeAsync("Class", "Accion", "demo.Accion", "src/Accion.java", 1, 9, marked);
        var route = await g.NodeAsync("Route", "/accion", "__route__ANY__/accion", "WEB-INF/struts-config.xml", 4, 4);
        await g.EdgeAsync(action, route, "HANDLES", marked);
        var baseType = await g.NodeAsync("Class", "Base", "demo.Base", "src/Base.java", 1, 9, marked);
        var sub = await g.NodeAsync("Class", "Sub", "demo.Sub", "src/Sub.java", 1, 9, marked);
        await g.EdgeAsync(sub, baseType, "INHERITS", marked);
        foreach (var (type, qn) in new[] { (action, "demo.Accion.Accion"), (baseType, "demo.Base.Base") })
        {
            var ctor = await g.NodeAsync("Method", qn[(qn.LastIndexOf('.') + 1)..], qn, "src/X.java", 2, 2, "{\"enriched_by\":[\"JavaAnalyzer\"],\"is_constructor\":true}");
            await g.EdgeAsync(type, ctor, "DEFINES_METHOD", marked);
        }

        var facets = await FacetsAsync(g.DatabasePath);

        Assert.Empty(Rows(facets.ZeroFaninClasses, "zero_fanin_classes"));
    }

    // ------------------------------------------------------------------ Struts/JSP

    private static void Write(string root, string relative, string text) =>
        File.WriteAllBytes(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), Encoding.Latin1.GetBytes(text.ReplaceLineEndings("\n")));

    /// <summary>
    /// <see cref="JavaTestRepo"/> con una segunda configuración Struts: <c>/otra</c> declara una clase que no está en el
    /// grafo y dos forwards sin destino, y <c>/solo</c> solo reenvía.
    /// </summary>
    private async Task<(CbmTestGraph Graph, string Repo)> StrutsGraphAsync()
    {
        var repo = JavaTestRepo.WriteTo(Path.Combine(_temp.Path, "repo"));
        Write(repo, "WebContent/WEB-INF/struts-config2.xml", """
            <?xml version="1.0" encoding="ISO-8859-1"?>
            <struts-config>
              <action-mappings>
                <action path="/otra" type="org.apache.struts.actions.DispatchAction" parameter="metodo">
                  <forward name="falta" path="/pages/noexiste.jsp"/>
                  <forward name="def" path="def.noexiste"/>
                </action>
                <action path="/solo" forward="/pages/resultado.jsp"/>
              </action-mappings>
            </struts-config>
            """);
        var graph = await JavaGraphFixture.CreateGraphAsync(Path.Combine(_temp.Path, "codebase.db"));
        await StrutsJspWriter.ApplyAsync(graph.DatabasePath, RepositoryScan.Scan(repo, TestRoots.Resolve(repo)), new StrutsJspOptions(),
            Encoding.Latin1, "cfg", DateTimeOffset.UnixEpoch, CancellationToken.None);
        return (graph, repo);
    }

    [Fact]
    public async Task Struts_and_jsp_material_reaches_the_facets_with_its_reasons()
    {
        var (graph, repo) = await StrutsGraphAsync();
        using var g = graph;

        var facets = await FacetsAsync(g.DatabasePath, repo);

        var jsp = Assert.Single(Rows(facets.SuspiciousJsps, "suspicious_jsps"));
        var sqlLine = JavaTestRepo.LineOf(JavaTestRepo.ConsultaJspPath, "SELECT * FROM T");
        Assert.Equal(JavaTestRepo.ConsultaJspPath, Text(jsp, "jsp"));
        Assert.Equal($"import:com.acme.dao.DaoImpl, import:java.sql.*, sql:{sqlLine}", Text(jsp, "reasons"));
        Assert.Equal("1", Text(jsp, "scriptlets"));
        Assert.Equal("1", Text(jsp, "session_access"));
        // Sin imports de negocio es una lista vacía, no un dato que falta.
        Assert.Equal("", Text(jsp, "business_imports"));

        // Primero la que declara una clase que no está en el grafo; /consultar sí tiene HANDLES.
        var actions = Rows(facets.StrutsActionsWithoutHandler, "struts_actions_without_handler");
        Assert.Equal(["/otra", "/solo"], actions.Select(a => Text(a, "action_path")));
        Assert.Equal("la clase del atributo type no está en el grafo (externa al repositorio o sin indexar)", Text(actions[0], "reason"));
        Assert.Equal("org.apache.struts.actions.DispatchAction", Text(actions[0], "action_type"));
        Assert.Equal("sin atributo type: la acción solo reenvía o incluye", Text(actions[1], "reason"));
        // Las tildes llegan al prompt tal cual, sin escapar.
        Assert.Contains("no está en el grafo", facets.StrutsActionsWithoutHandler);

        output.WriteLine($"--- unresolved_forwards\n{facets.UnresolvedForwards}");
        using (var forwards = JsonDocument.Parse(facets.UnresolvedForwards))
        {
            Assert.Equal(2, forwards.RootElement.GetProperty("total_unresolved").GetInt32());
            Assert.Equal(["/pages/noexiste.jsp", "def.noexiste"],
                forwards.RootElement.GetProperty("forwards").EnumerateArray().Select(f => f.GetProperty("path").GetString()));
            Assert.All(forwards.RootElement.GetProperty("forwards").EnumerateArray(), f => Assert.Equal("/otra", f.GetProperty("action").GetString()));
        }

        // Jerarquía: la INHERITS de cbm del fixture es heurística; la IMPLEMENTS la escribe el test como resuelta.
        await g.EdgeAsync((await g.NodeIdAsync(JavaGraphFixture.Qn(JavaTestRepo.DaoImplPath, "DaoImpl")))!.Value,
            (await g.NodeIdAsync(JavaGraphFixture.Qn(JavaTestRepo.DaoPath, "Dao")))!.Value, "IMPLEMENTS", "{\"enriched_by\":[\"JavaAnalyzer\"]}");
        var hierarchy = Rows((await FacetsAsync(g.DatabasePath, repo)).TypeHierarchy, "type_hierarchy");
        var inherits = Assert.Single(hierarchy, h => Text(h, "supertype") == JavaGraphFixture.Qn(JavaTestRepo.BasePath, "Base"));
        Assert.Equal(("INHERITS", "1", "0", "1", "Servicio"), (Text(inherits, "relation"), Text(inherits, "subtypes"), Text(inherits, "resolved"),
            Text(inherits, "heuristic_cbm"), Text(inherits, "sample_subtypes")));
        var implements = Assert.Single(hierarchy, h => Text(h, "relation") == "IMPLEMENTS");
        Assert.Equal(("1", "0"), (Text(implements, "resolved"), Text(implements, "heuristic_cbm")));
    }

    /// <summary>
    /// cbm corta algunas propiedades en 2047 bytes y no escapa los saltos de línea de <c>base_classes</c>. Con un solo nodo
    /// así, json_extract abortaba la extracción de facetas (análisis completo de delta/Online, 2026-09-29). El esquema lo
    /// repara al asegurarse, conservando el texto original.
    /// </summary>
    [Fact]
    public async Task Cbm_properties_that_are_not_json_are_kept_raw_and_do_not_break_the_facets()
    {
        using var g = await CbmTestGraph.CreateAsync(Path.Combine(_temp.Path, "codebase.db"), "demo");
        const string truncated = "{\"complexity\":0,\"param_types\":[\"Hashtable\",\"OrdenacionPaginaci\"";
        const string rawNewline = "{\"base_classes\":[\"implements\r\n\t\tDAOPeriodo\"]}";
        await g.NodeAsync("Function", "buscar", "demo.Dao.buscar", "src/Dao.java", 1, 5, truncated);
        await g.NodeAsync("Class", "DaoImpl", "demo.DaoImpl", "src/DaoImpl.java", 1, 50, rawNewline);
        await g.NodeAsync("Method", "m0", "demo.C.m0", "src/C.java", 1, 5, "{\"cyclomatic\":9}");
        await g.NodeAsync("Method", "vacio", "demo.C.vacio", "src/C.java", 6, 8, "");

        await GraphSchema.EnsureAsync(g.DatabasePath, CancellationToken.None);

        Assert.Equal(0, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE json_valid(properties) = 0"));
        Assert.Equal(truncated, await g.TextAsync(
            "SELECT json_extract(properties, '$.cbm_raw_properties') FROM nodes WHERE qualified_name = 'demo.Dao.buscar'"));
        Assert.Equal(rawNewline, await g.TextAsync(
            "SELECT json_extract(properties, '$.cbm_raw_properties') FROM nodes WHERE qualified_name = 'demo.DaoImpl'"));
        Assert.Equal("{\"cyclomatic\":9}", await g.TextAsync("SELECT properties FROM nodes WHERE qualified_name = 'demo.C.m0'"));

        var facets = await FacetsAsync(g.DatabasePath);

        Rows(facets.MetricsSummary, "metrics_summary");
        Assert.Equal(["demo.C.m0"], Rows(facets.HighCyclomaticMethods, "high_cyclomatic_methods").Select(r => Text(r, "qualified_name")));
    }

    /// <summary>
    /// cbm trae su propio <c>idx_nodes_label</c> sobre <c>(project, label)</c>, y con ese nombre el índice por label de
    /// <see cref="GraphSchema"/> no se creaba en la copia: las consultas por label sin <c>project</c> recorrían la tabla entera.
    /// Con nombre propio existe también ahí y las consultas por label lo usan.
    /// </summary>
    [Fact]
    public async Task The_label_index_is_created_even_on_a_cbm_copy_and_label_queries_use_it()
    {
        var db = Path.Combine(_temp.Path, "cbm.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE nodes (id INTEGER PRIMARY KEY AUTOINCREMENT, project TEXT NOT NULL, label TEXT NOT NULL, name TEXT NOT NULL,
                    qualified_name TEXT NOT NULL, file_path TEXT DEFAULT '', start_line INTEGER DEFAULT 0, end_line INTEGER DEFAULT 0,
                    properties TEXT DEFAULT '{}', UNIQUE(project, qualified_name));
                CREATE INDEX idx_nodes_label ON nodes(project, label);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await GraphSchema.EnsureAsync(db, CancellationToken.None);

        using var check = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}");
        await check.OpenAsync();
        using var plan = check.CreateCommand();
        plan.CommandText = "EXPLAIN QUERY PLAN SELECT name FROM nodes WHERE label = 'Route' ORDER BY qualified_name LIMIT 50";
        var steps = new List<string>();
        using (var reader = await plan.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                steps.Add(reader.GetString(3));
        Assert.Contains(steps, s => s.Contains("USING INDEX idx_graph_nodes_label", StringComparison.Ordinal));
    }

    /// <summary>El índice por label solo cambia el plan: las facetas salen idénticas con él y sin él.</summary>
    [Fact]
    public async Task The_facets_are_the_same_with_and_without_the_label_index()
    {
        var (graph, repo) = await StrutsGraphAsync();
        using var g = graph;
        await g.NodeAsync("DiRegistration", "IRepo", "demo.di.IRepo", "src/Startup.cs", 5, 5, "{\"di_lifetime\":\"Scoped\",\"fan_in\":0}");
        await g.NodeAsync("Flow", "Alta", "demo.Flow.Alta", "src/Svc.java", 1, 1, "{\"step_count\":3,\"entry_method\":\"demo.Svc.alta\"}");
        await g.NodeAsync("Route", "/api", "__route__GET__/api", "src/Api.cs", 3, 3, "{\"route_path\":\"/api\",\"http_verb\":\"GET\",\"auth_required\":false}");
        await g.NodeAsync("Class", "Vieja", "demo.Vieja", "src/Vieja.java", 1, 9, "{\"annotations\":[\"Deprecated\"],\"fan_in\":0}");
        Assert.Equal(1, await g.CountAsync("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'idx_graph_nodes_label'"));

        static string Snapshot(ReportFacetInput facets) => JsonSerializer.Serialize(facets);
        var withIndex = Snapshot(await FacetsAsync(g.DatabasePath, repo));
        using (var drop = g.Connection.CreateCommand())
        {
            drop.CommandText = "DROP INDEX idx_graph_nodes_label";
            await drop.ExecuteNonQueryAsync();
        }
        var withoutIndex = Snapshot(await FacetsAsync(g.DatabasePath, repo));

        Assert.DoesNotContain("Faceta no disponible", withIndex);
        Assert.Contains("demo.Vieja", withIndex);
        Assert.Equal(withoutIndex, withIndex);
    }

    [Fact]
    public async Task Without_a_struts_pass_the_struts_facets_are_empty()
    {
        using var g = await JavaGraphFixture.CreateGraphAsync(Path.Combine(_temp.Path, "codebase.db"));

        var facets = await FacetsAsync(g.DatabasePath);

        Assert.Equal("[]", facets.SuspiciousJsps);
        Assert.Equal("[]", facets.StrutsActionsWithoutHandler);
        Assert.Equal("[]", facets.UnresolvedForwards);
    }

    [JdkFact]
    public async Task After_the_java_enrichment_the_facets_compute_coupling_from_the_resolved_calls()
    {
        var repo = JavaTestRepo.WriteTo(Path.Combine(_temp.Path, "repo"));
        Directory.CreateDirectory(Path.Combine(_temp.Path, "db"));
        using var graph = await JavaGraphFixture.CreateGraphAsync(Path.Combine(_temp.Path, "db", "codebase.db"));
        var configuration = TestConfig.FromPairs(("JavaAnalyzer:JdkPath", TestJdk.Info!.JdkPath), ("JavaAnalyzer:JvmOptions:0", "-Xmx1g"));
        var context = EnrichmentStepContext.Create(configuration, new CapturingReporter(), repo, graph.DatabasePath);
        await new LanguageEnricherStep().ExecuteAsync(
            new GraphStatusResult { DatabasePath = graph.DatabasePath, ExistedBefore = true, IsEnriched = true }, context, 1, null, CancellationToken.None);

        var facets = await FacetsAsync(graph.DatabasePath, repo);
        const string fromCalls = "calculado: aristas CALLS resueltas por JavaAnalyzer";

        var coupling = Rows(facets.CouplingMetrics, "coupling_metrics");
        // Base.log lo llama Servicio.ejecutar: fan_in 1 de las CALLS del analizador, no de cbm.
        var log = Row(coupling, JavaGraphFixture.Qn(JavaTestRepo.BasePath, "Base.log"));
        Assert.Equal(("1", fromCalls), (Text(log, "fan_in"), Text(log, "fan_in_source")));
        var servicio = Row(coupling, JavaGraphFixture.Qn(JavaTestRepo.ServicioPath, "Servicio"));
        Assert.Equal(fromCalls, Text(servicio, "fan_out_source"));
        Assert.True(int.Parse(Text(servicio, "fan_out")!) >= 3);
        // Servicio solo se instancia con su constructor implícito: su cero no prueba nada y no sale como sin uso.
        Assert.DoesNotContain(Rows(facets.ZeroFaninClasses, "zero_fanin_classes"),
            r => Text(r, "qualified_name") == JavaGraphFixture.Qn(JavaTestRepo.ServicioPath, "Servicio"));
        Assert.Single(Rows(facets.SuspiciousJsps, "suspicious_jsps"));
    }

    // ------------------------------------------------------------------ auditores

    [Fact]
    public void The_auditors_have_the_graph_tools()
    {
        Assert.Contains(GraphNeighborsTool.ToolName, AnalysisPipelineFactory.AuditorToolNames);
        Assert.Contains(new GraphDomainTool().Name, AnalysisPipelineFactory.AuditorToolNames);
        Assert.Contains(new GraphSearchTool().Name, AnalysisPipelineFactory.AuditorToolNames);
        Assert.Contains("view_file", AnalysisPipelineFactory.AuditorToolNames);
        Assert.Contains(ReadDocumentTool.ToolName, AnalysisPipelineFactory.AuditorToolNames);
    }

    /// <summary>Todas las plantillas de auditor incluyen la regla del grafo; la síntesis, que no tiene tools, no.</summary>
    [Fact]
    public void Every_auditor_system_prompt_carries_the_graph_reliability_rule()
    {
        var templates = AnalysisPipelineFactory.CreateDefaultTemplateProvider();
        foreach (var type in Enum.GetValues<ReportType>())
        {
            var system = templates.Render($"{AnalysisPipelineInput.GetTemplatePrefix(type)}_system", new ReportFacetInput());
            Assert.NotNull(system);
            Assert.Contains("Una arista resuelta es un dato; una heurística es solo una pista", system);
            Assert.Contains("Código sin llamadas no es código muerto", system);
            Assert.DoesNotContain("@[_graph_rules.md]", system);
        }
        Assert.DoesNotContain("Código sin llamadas no es código muerto", templates.Render("report_sintesis_system", new ReportFacetInput()));
    }
}

/// <summary>
/// Las ramas de auditor del pipeline construido reciben las tools del grafo: <c>TemplateLlmStep</c> las resuelve por nombre
/// al construirse, así que hace falta registrarlas antes, como hace la app.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class AuditorToolsTests
{
    [Fact]
    public async Task Every_auditor_step_gets_graph_neighbors_and_graph_domain()
    {
        using var repo = new TempDirectory();
        var configuration = TestConfig.FromPairs(
            ("LlmConfiguration:DefaultProvider", "Fake"),
            ("LlmConfiguration:Providers:Fake:ServiceType", "fake"),
            ("LlmConfiguration:Providers:Fake:BaseUrl", "http://fake.invalid/"),
            ("LlmConfiguration:Providers:Fake:Model", "fake-model"));
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddKeyedSingleton<ILlmService>("fake", new CannedLlmService("{}"));
        services.AddAITaskAgent();
        await using var provider = services.BuildServiceProvider();
        provider.UseAITaskAgent();
        var roles = LlmRoleFactory.BuildRoles(configuration, provider, _ => null, _ => { });
        var input = AnalysisRunOptions.BuildInput(configuration, repo.Path, _ => { });
        AnalysisToolRegistration.Register(input);

        var pipeline = AnalysisPipelineFactory.CreatePipeline(AnalysisPipelineFactory.CreateDefaultTemplateProvider(), roles, input, provider);

        var auditors = pipeline.Steps.OfType<ParallelStep>().SelectMany(p => p.Branches)
            .Where(s => AnalysisPipelineFactory.TryParseAuditorStepName(s.Name, out _)).ToList();
        Assert.Equal(Enum.GetValues<ReportType>().Length, auditors.Count);
        Assert.All(auditors, a =>
        {
            var tools = a.GetType().GetProperty("Tools")!.GetValue(a) as IList<AITaskAgent.LLM.Tools.Abstractions.ITool>;
            Assert.NotNull(tools);
            Assert.Equal(AnalysisPipelineFactory.AuditorToolNames.Order(), tools.Select(t => t.Name).Order());
        });
    }
}
