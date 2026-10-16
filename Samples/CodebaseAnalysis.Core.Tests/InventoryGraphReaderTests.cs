using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Tests.Enrichment;
using Microsoft.Data.Sqlite;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="InventoryGraphReader"/> (T5): lo que el inventario lee del grafo: puntos de entrada, tablas, ficheros con nodos,
/// estado de cada enriquecedor y la deriva de cbm. Sobre grafos sintéticos de <see cref="CbmTestGraph"/>, incluidos los que no
/// tienen <c>enrichment_runs</c> (índice sin enriquecer) o llevan nodos con JSON inválido, que es lo que deja cbm en algunos.
/// </summary>
public sealed class InventoryGraphReaderTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private Task<CbmTestGraph> NewGraphAsync(string indexedAt = "2026-09-28T10:00:00Z") =>
        CbmTestGraph.CreateAsync(Path.Combine(_temp.Path, $"{Guid.NewGuid():N}.db"), "demo", indexedAt);

    private static Task<InventoryGraphData> ReadAsync(CbmTestGraph graph, int maxEntryPoints = 500, int maxTables = 200) =>
        InventoryGraphReader.ReadAsync(graph.DatabasePath, maxEntryPoints, maxTables, CancellationToken.None);

    [Fact]
    public async Task An_unenriched_graph_without_enrichment_runs_reads_without_failing()
    {
        using var graph = await NewGraphAsync();
        await graph.NodeAsync("Class", "A", "demo.A", "src/A.java", 1, 10);

        var data = await ReadAsync(graph);

        Assert.Empty(data.EnricherRuns);
        Assert.Null(data.CbmMatch);
        Assert.Empty(data.EntryPoints);
        Assert.Empty(data.Tables);
        Assert.Empty(data.Warnings);
        Assert.Equal(["src/A.java"], data.GraphFiles);
        Assert.Equal("demo|2026-09-28T10:00:00Z", data.IndexFingerprint);
    }

    [Fact]
    public async Task A_node_with_invalid_json_does_not_break_the_queries()
    {
        using var graph = await NewGraphAsync();
        await graph.NodeAsync("Method", "main", "demo.App.main", "src/App.java", 3, 5, "{\"is_entry_point\":true}");
        await graph.NodeAsync("Method", "broken", "demo.App.broken", "src/Broken.java", 1, 2, "{\"docstring\":\"abc\ndef\",\"is_entry_point\":tru");
        await graph.NodeAsync("Route", "/x", "__route__ANY__/x", "WEB-INF/struts-config.xml", 9, 9, "{\"framework\":\"struts\",\"route_path\":\"/x\"");
        await graph.NodeAsync("Table", "ORDERS", "table.ORDERS", "", 0, 0, "no json");

        var data = await ReadAsync(graph);

        Assert.Empty(data.Warnings);
        // Una ruta es una ruta aunque sus propiedades no se puedan leer: sin «framework» cuenta como ruta genérica.
        Assert.Equal([("main", "demo.App.main"), ("route", "/x")], data.EntryPoints.Select(e => (e.Kind, e.Name)));
        Assert.Equal(["ORDERS"], data.Tables.Select(t => t.Name));
        Assert.Contains("src/Broken.java", data.GraphFiles);
    }

    [Fact]
    public async Task Entry_points_come_from_struts_routes_main_scheduled_consumers_and_marked_nodes_once_each()
    {
        using var graph = await NewGraphAsync();
        var handler = await graph.NodeAsync("Class", "PayAction", "demo.web.PayAction", "src/web/PayAction.java", 10, 80);
        var strutsRoute = await graph.NodeAsync("Route", "/pay", "__route__ANY__/pay", "WEB-INF/struts-config.xml", 20, 22,
            "{\"framework\":\"struts\",\"route_path\":\"/pay.do\",\"http_method\":\"ANY\"}");
        await graph.EdgeAsync(handler, strutsRoute, "HANDLES");
        await graph.NodeAsync("Route", "/orphan", "__route__ANY__/orphan", "WEB-INF/struts-config.xml", 30, 31,
            "{\"framework\":\"struts\",\"route_path\":\"/orphan.do\"}");
        await graph.NodeAsync("Route", "GET /api/items", "__route__GET__/api/items", "src/Api.cs", 7, 7,
            "{\"route_path\":\"/api/items\",\"http_verb\":\"GET\"}");
        await graph.NodeAsync("Method", "main", "demo.App.main", "src/App.java", 3, 9, "{\"is_entry_point\":true}");
        await graph.NodeAsync("Function", "Main", "demo.Program.Main", "src/Program.cs", 1, 4);
        await graph.NodeAsync("Method", "nightly", "demo.jobs.Cleanup.nightly", "src/jobs/Cleanup.java", 12, 20,
            "{\"annotations\":[\"Scheduled(cron = \\\"0 0 3 * * *\\\")\"]}");
        await graph.NodeAsync("Method", "onMessage", "demo.mq.Consumer.onMessage", "src/mq/Consumer.java", 8, 16,
            "{\"annotations\":[\"JmsListener\"]}");
        await graph.NodeAsync("Method", "weekly", "demo.jobs.Report.weekly", "src/jobs/Report.cs", 4, 9,
            "{\"attributes\":[\"Scheduled\"]}");
        await graph.NodeAsync("Method", "boot", "demo.Boot.boot", "src/Boot.java", 5, 6, "{\"is_entry_point\":true}");
        // El texto «Scheduled» en un docstring no es una anotación.
        await graph.NodeAsync("Method", "notAJob", "demo.Doc.notAJob", "src/Doc.java", 1, 3, "{\"docstring\":\"runs Scheduled by hand\"}");
        // Un test que se llama main no es un punto de entrada.
        await graph.NodeAsync("Method", "main", "demo.AppTest.main", "src/test/AppTest.java", 1, 3, "{\"is_test\":true}");

        var data = await ReadAsync(graph);

        Assert.Equal(
            [
                ("main", "demo.App.main", "src/App.java", 3),
                ("main", "demo.Program.Main", "src/Program.cs", 1),
                ("marked_entry_point", "demo.Boot.boot", "src/Boot.java", 5),
                ("message_consumer", "demo.mq.Consumer.onMessage", "src/mq/Consumer.java", 8),
                ("route", "GET /api/items", "src/Api.cs", 7),
                ("scheduled", "demo.jobs.Cleanup.nightly", "src/jobs/Cleanup.java", 12),
                ("scheduled", "demo.jobs.Report.weekly", "src/jobs/Report.cs", 4),
                ("struts_action", "/orphan.do", "WEB-INF/struts-config.xml", 30),
                ("struts_action", "/pay.do", "src/web/PayAction.java", 10)
            ],
            data.EntryPoints.Select(e => (e.Kind, e.Name, e.File, e.Line)));
        Assert.Equal(9, data.EntryPointsTotal);
    }

    [Fact]
    public async Task The_entry_point_list_is_capped_but_the_total_is_kept()
    {
        using var graph = await NewGraphAsync();
        for (var i = 0; i < 12; i++)
            await graph.NodeAsync("Method", "main", $"demo.App{i:00}.main", $"src/App{i:00}.java", 1, 2);

        var data = await ReadAsync(graph, maxEntryPoints: 5);

        Assert.Equal(12, data.EntryPointsTotal);
        Assert.Equal(["demo.App00.main", "demo.App01.main", "demo.App02.main", "demo.App03.main", "demo.App04.main"], data.EntryPoints.Select(e => e.Name));
    }

    [Fact]
    public async Task Tables_are_counted_by_incoming_references_then_by_name()
    {
        using var graph = await NewGraphAsync();
        var orders = await graph.NodeAsync("Table", "ORDERS", "table.ORDERS");
        var items = await graph.NodeAsync("AccessTable", "ITEMS", "table.ITEMS");
        await graph.NodeAsync("Table", "UNUSED", "table.UNUSED");
        var dao1 = await graph.NodeAsync("Class", "OrderDao", "demo.OrderDao", "src/OrderDao.java");
        var dao2 = await graph.NodeAsync("Class", "ItemDao", "demo.ItemDao", "src/ItemDao.java");
        await graph.EdgeAsync(dao1, orders, "READS");
        await graph.EdgeAsync(dao2, orders, "WRITES");
        await graph.EdgeAsync(dao2, items, "READS");

        var data = await ReadAsync(graph, maxTables: 2);

        Assert.Equal([("ORDERS", 2), ("ITEMS", 1)], data.Tables.Select(t => (t.Name, t.References)));
        Assert.Equal(3, data.TablesTotal);
    }

    [Fact]
    public async Task Graph_files_are_the_distinct_normalized_paths_of_nodes_with_a_file()
    {
        using var graph = await NewGraphAsync();
        await graph.NodeAsync("Class", "A", "demo.A", "src/A.java");
        await graph.NodeAsync("Method", "m", "demo.A.m", "src/A.java");
        await graph.NodeAsync("Class", "B", "demo.B", "src\\B.java");
        await graph.NodeAsync("Table", "T", "table.T", "");

        var data = await ReadAsync(graph);

        Assert.Equal(["src/A.java", "src/B.java"], data.GraphFiles.OrderBy(f => f, StringComparer.Ordinal));
        Assert.Contains("SRC/a.JAVA", data.GraphFiles);
    }

    private static async Task InsertRunAsync(CbmTestGraph graph, string enricher, string status, string? index = null, string? details = null, string? message = null)
    {
        await EnrichmentRuns.InsertAsync(graph.Connection, new EnrichmentRun
        {
            Enricher = enricher,
            EnricherVersion = "1",
            IndexFingerprint = index ?? (await EnrichmentRuns.IndexFingerprintAsync(graph.Connection, CancellationToken.None))!,
            ConfigFingerprint = "c",
            StartedAt = DateTimeOffset.UtcNow,
            Status = status,
            Outcome = details == null ? null : new EnrichmentOutcome { Details = details },
            Message = message
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_state_of_each_enricher_is_its_last_run_on_the_current_index()
    {
        using var graph = await NewGraphAsync();
        await InsertRunAsync(graph, "JavaAnalyzer", EnrichmentRuns.StatusFailed, message: "boom");
        await InsertRunAsync(graph, "JavaAnalyzer", EnrichmentRuns.StatusOk);
        await InsertRunAsync(graph, "DotNetEnricher", EnrichmentRuns.StatusOk);
        await InsertRunAsync(graph, "DotNetEnricher", EnrichmentRuns.StatusFailed, message: "sin solución");
        await InsertRunAsync(graph, "StrutsJspPlugin", EnrichmentRuns.StatusSkipped, message: "sin struts-config");
        await InsertRunAsync(graph, "OtroEnriquecedor", EnrichmentRuns.StatusOk, index: "demo|otra-indexacion");

        var data = await ReadAsync(graph);

        Assert.Equal(
            [
                ("DotNetEnricher", "failed", "sin solución"),
                ("JavaAnalyzer", "ok", ""),
                ("OtroEnriquecedor", InventoryGraphReader.StaleStatus, ""),
                ("StrutsJspPlugin", "skipped", "sin struts-config")
            ],
            data.EnricherRuns.Select(r => (r.Enricher, r.Status, r.Message)));
        Assert.Equal(2L, data.EnricherRuns.First(r => r.Enricher == "JavaAnalyzer").RunId);
        Assert.Equal(4L, data.EnricherRuns.First(r => r.Enricher == "DotNetEnricher").RunId);
    }

    [Fact]
    public async Task The_cbm_drift_is_read_from_the_last_ok_java_run_and_missing_fields_default()
    {
        using var graph = await NewGraphAsync();
        await InsertRunAsync(graph, "JavaAnalyzer", EnrichmentRuns.StatusOk,
            details: "{\"cbm_match\":{\"types_unmatched_ratio\":0.0123,\"methods_unmatched_ratio\":0.2,\"unindexed_files\":14,\"unindexed_files_long_path\":3,\"drift_suspected\":true}}");

        var data = await ReadAsync(graph);

        Assert.NotNull(data.CbmMatch);
        Assert.Equal(14, data.CbmMatch.UnindexedFiles);
        Assert.Equal(3, data.CbmMatch.UnindexedFilesLongPath);
        Assert.Equal(0.0123, data.CbmMatch.TypesUnmatchedRatio);
        Assert.Equal(0.2, data.CbmMatch.MethodsUnmatchedRatio);
        Assert.True(data.CbmMatch.DriftSuspected);

        using var other = await NewGraphAsync();
        await InsertRunAsync(other, "JavaAnalyzer", EnrichmentRuns.StatusOk, details: "{\"edges\":{}}");
        await InsertRunAsync(other, "JavaAnalyzer", EnrichmentRuns.StatusFailed, details: "{\"cbm_match\":{\"unindexed_files\":99}}");
        Assert.Null((await ReadAsync(other)).CbmMatch);
    }

    [Fact]
    public async Task Details_that_are_not_json_are_ignored()
    {
        using var graph = await NewGraphAsync();
        await InsertRunAsync(graph, "JavaAnalyzer", EnrichmentRuns.StatusOk, details: "no es json");

        var data = await ReadAsync(graph);

        Assert.Null(data.CbmMatch);
        Assert.Single(data.EnricherRuns);
        Assert.Empty(data.Warnings);
    }

    [Fact]
    public async Task A_missing_database_is_a_warning_with_empty_data()
    {
        var data = await InventoryGraphReader.ReadAsync(Path.Combine(_temp.Path, "no-existe.db"), 10, 10, CancellationToken.None);

        Assert.Single(data.Warnings);
        Assert.Empty(data.EntryPoints);
        Assert.Empty(data.GraphFiles);
        Assert.Null(data.IndexFingerprint);
    }

    [Fact]
    public async Task The_reader_does_not_write_to_the_database()
    {
        using var graph = await NewGraphAsync();
        await graph.NodeAsync("Method", "main", "demo.App.main", "src/App.java");
        var before = File.GetLastWriteTimeUtc(graph.DatabasePath);
        var nodes = await graph.CountAsync("SELECT COUNT(1) FROM nodes");

        await ReadAsync(graph);

        Assert.Equal(nodes, await graph.CountAsync("SELECT COUNT(1) FROM nodes"));
        Assert.Equal(before, File.GetLastWriteTimeUtc(graph.DatabasePath));
        SqliteConnection.ClearAllPools();
    }
}
