using AITaskAgent.Core.Models;
using CodebaseAnalysis.Core.Enrichment.AnalysisGraph;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tests.Enrichment;
using CodebaseAnalysis.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// E1-E3 del plan 2026-10-06: subsistemas, tablas del SQL de las cadenas y notas de investigación escritos en la copia del grafo, la
/// resolución <c>ruta:línea</c> → nodo, y las vistas <c>subsystems</c> y <c>tables</c> de <c>graph_domain</c>. Sin LLM: un grafo
/// sintético con la forma de cbm, con dos subsistemas genéricos (pedidos y facturación).
/// </summary>
public sealed class AnalysisGraphTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private sealed record Ids(long Service, long Place, long Dao, long Find, long Save, long Invoice, long Total, long Route, long Invoices);

    private async Task<(CbmTestGraph Graph, Ids Ids)> GraphAsync()
    {
        var g = await CbmTestGraph.CreateAsync(Path.Combine(_temp.Path, "codebase.db"), "demo");
        var service = await g.NodeAsync("Class", "OrderService", "demo.src.orders.OrderService.OrderService", "src/orders/OrderService.java", 1, 50);
        var place = await g.NodeAsync("Method", "place", "demo.src.orders.OrderService.OrderService.place", "src/orders/OrderService.java", 5, 20);
        var cancel = await g.NodeAsync("Method", "cancel", "demo.src.orders.OrderService.OrderService.cancel", "src/orders/OrderService.java", 22, 30);
        var dao = await g.NodeAsync("Class", "OrderDao", "demo.src.orders.OrderDao.OrderDao", "src/orders/OrderDao.java", 1, 60, "{\"fan_in\":3}");
        var find = await g.NodeAsync("Method", "find", "demo.src.orders.OrderDao.OrderDao.find", "src/orders/OrderDao.java", 5, 12);
        var save = await g.NodeAsync("Method", "save", "demo.src.orders.OrderDao.OrderDao.save", "src/orders/OrderDao.java", 14, 25);
        var invoice = await g.NodeAsync("Class", "Invoice", "demo.src.billing.Invoice.Invoice", "src/billing/Invoice.java", 1, 30);
        var total = await g.NodeAsync("Method", "total", "demo.src.billing.Invoice.Invoice.total", "src/billing/Invoice.java", 3, 10);
        await g.NodeAsync("Jsp", "orders.jsp", "web.orders.jsp", "web/orders.jsp");
        await g.NodeAsync("File", "billing.js", "demo.scripts.billing.js", "scripts/billing.js");
        var route = await g.NodeAsync("Route", "/orders/place", "route./orders/place", "WEB-INF/struts-config.xml", 10, 12);
        var invoices = await g.NodeAsync("Table", "INVOICES", "table.INVOICES");

        await g.EdgeAsync(service, place, "DEFINES_METHOD");
        await g.EdgeAsync(service, cancel, "DEFINES_METHOD");
        await g.EdgeAsync(dao, find, "DEFINES_METHOD");
        await g.EdgeAsync(dao, save, "DEFINES_METHOD");
        await g.EdgeAsync(invoice, total, "DEFINES_METHOD");
        await g.EdgeAsync(place, find, "CALLS");
        await g.EdgeAsync(place, save, "CALLS");
        await g.EdgeAsync(place, total, "CALLS");
        await g.EdgeAsync(total, dao, "USES_TYPE");
        await g.EdgeAsync(service, route, "HANDLES");
        return (g, new Ids(service, place, dao, find, save, invoice, total, route, invoices));
    }

    private static DomainMap Map(bool withBilling = true) => new()
    {
        GeneratedAt = DateTimeOffset.Parse("2026-10-06T10:00:00Z"),
        GraphFingerprint = "fp",
        Subsystems =
        [
            new Subsystem
            {
                Id = "orders", Name = "Pedidos", Description = "Gestión de pedidos", NameOrigin = SubsystemNameOrigins.Llm,
                Roots = ["src/orders"], DominantTerm = "order",
                Files = ["src/orders/OrderDao.java", "src/orders/OrderService.java", "web/orders.jsp"],
                KeyTypes = ["OrderService"], Tables = ["ORDERS"],
                EntryPoints = [new InventoryEntryPoint { Kind = "struts_action", Name = "/orders/place", File = "src/orders/OrderService.java", Line = 5 }],
                Metrics = new SubsystemMetrics { Files = 3, Loc = 900 }
            },
            .. withBilling
                ? new[]
                {
                    new Subsystem
                    {
                        Id = "billing", Name = "Facturación", Files = ["scripts/billing.js", "src/billing/Invoice.java"],
                        Metrics = new SubsystemMetrics { Files = 2, Loc = 300 }
                    }
                }
                : []
        ]
    };

    private static IReadOnlyList<SqlTableReference> References(bool withCustomers = true, bool extraFind = false) =>
    [
        new("ORDERS", null, "src/orders/OrderDao.java", 8, "SELECT"),
        .. extraFind ? new[] { new SqlTableReference("orders", "app", "src/orders/OrderDao.java", 9, "SELECT") } : [],
        new("ORDERS", null, "src/orders/OrderDao.java", 16, "INSERT", Writes: true),
        .. withCustomers ? new[] { new SqlTableReference("customers", null, "src/orders/OrderDao.java", 16, "INSERT") } : [],
        new("ORDERS", null, "src/orders/OrderDao.java", 40, "SELECT"),
        new("invoices", null, "src/billing/Invoice.java", 5, "UPDATE", Writes: true),
        new("ORDERS", null, "missing/Gone.java", 3, "SELECT")
    ];

    private const string NoteText = """
        [R1]
        OrderDao.find consulta la tabla `src/orders/OrderDao.java:8`.
        El estado se fija en `OrderService.java:6-9` al colocar el pedido.
        Con ruta absoluta C:/repo/src/orders/OrderDao.java:16 se guarda.
        Sin nodo: `missing/Gone.java:3`; dentro de la clase: `src/orders/OrderDao.java:55`.
        """;

    private static InvestigationNoteRecord Note(string text = NoteText) => new()
    {
        Report = "Funcional", SubsystemId = "orders", SubsystemName = "Pedidos", Text = text,
        RunStartedAt = DateTimeOffset.Parse("2026-10-06T09:00:00Z")
    };

    private static Task<long> CountEdgesAsync(CbmTestGraph g, string type) =>
        g.CountAsync("SELECT COUNT(1) FROM edges WHERE type = @t", ("@t", type));

    private async Task<string> DomainAsync(string args, string databasePath)
    {
        var context = new PipelineContext();
        context.Set(AnalysisContextKeys.CbmDbPath, databasePath);
        var result = await new GraphDomainTool().ExecuteAsync(args, context, "ChatAssistant", NullLogger.Instance);
        output.WriteLine($"--- graph_domain {args}\n{result}");
        return result;
    }

    // ───────────────────────────── E1 ─────────────────────────────

    [Fact]
    public async Task Subsystems_are_written_with_contains_and_derived_dependencies()
    {
        var (g, ids) = await GraphAsync();
        using var _ = g;

        var stats = await SubsystemGraphWriter.WriteAsync(g.DatabasePath, Map(), CancellationToken.None);

        Assert.Equal(2, stats.Subsystems);
        Assert.Equal(4, stats.ContainsMembers); // OrderService, OrderDao, orders.jsp, Invoice (los métodos no)
        Assert.Equal(1, stats.ContainsRoutes);  // /orders/place, por la HANDLES de OrderService
        Assert.Equal(1, stats.ContainsFiles);   // billing.js no tiene tipos: su nodo File
        Assert.Equal(2, stats.DependsOn);
        var orders = (await g.NodeIdAsync("subsystem:orders"))!.Value;
        var billing = (await g.NodeIdAsync("subsystem:billing"))!.Value;
        Assert.Equal(1, await g.CountAsync("SELECT COUNT(1) FROM edges WHERE source_id = @s AND target_id = @t AND type = 'CONTAINS'", ("@s", orders), ("@t", ids.Route)));
        Assert.Equal(1, await g.CountAsync("SELECT COUNT(1) FROM edges WHERE source_id = @s AND target_id = @t AND type = 'CONTAINS'", ("@s", billing), ("@t", ids.Invoice)));
        // place CALLS total (pedidos → facturación) y total USES_TYPE OrderDao (facturación → pedidos).
        Assert.Equal("{\"CALLS\":1}", await g.TextAsync(
            "SELECT json_extract(properties, '$.via') FROM edges WHERE source_id = @s AND target_id = @t AND type = 'DEPENDS_ON'", ("@s", orders), ("@t", billing)));
        Assert.Equal(1, await g.CountAsync(
            "SELECT json_extract(properties, '$.weight') FROM edges WHERE source_id = @s AND target_id = @t AND type = 'DEPENDS_ON'", ("@s", billing), ("@t", orders)));
        Assert.Equal("Gestión de pedidos", await g.TextAsync("SELECT json_extract(properties, '$.description') FROM nodes WHERE id = @id", ("@id", orders)));
        Assert.Equal(900, await g.CountAsync("SELECT json_extract(properties, '$.loc') FROM nodes WHERE id = @id", ("@id", orders)));
    }

    [Fact]
    public async Task Rewriting_subsystems_is_idempotent_keeps_ids_and_drops_the_missing_ones()
    {
        var (g, _) = await GraphAsync();
        using var __ = g;

        await SubsystemGraphWriter.WriteAsync(g.DatabasePath, Map(), CancellationToken.None);
        var ordersBefore = await g.NodeIdAsync("subsystem:orders");
        var containsBefore = await CountEdgesAsync(g, "CONTAINS");
        await SubsystemGraphWriter.WriteAsync(g.DatabasePath, Map(), CancellationToken.None);

        Assert.Equal(ordersBefore, await g.NodeIdAsync("subsystem:orders"));
        Assert.Equal(2, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE label = 'Subsystem'"));
        Assert.Equal(containsBefore, await CountEdgesAsync(g, "CONTAINS"));
        Assert.Equal(2, await CountEdgesAsync(g, "DEPENDS_ON"));

        var stats = await SubsystemGraphWriter.WriteAsync(g.DatabasePath, Map(withBilling: false), CancellationToken.None);
        Assert.Equal(1, stats.PreviousNodesRemoved);
        Assert.Null(await g.NodeIdAsync("subsystem:billing"));
        Assert.Equal(0, await CountEdgesAsync(g, "DEPENDS_ON"));
    }

    // ───────────────────────────── E2 ─────────────────────────────

    [Fact]
    public async Task Sql_tables_are_linked_from_the_most_specific_node_by_verb()
    {
        var (g, ids) = await GraphAsync();
        using var _ = g;

        var stats = await SqlTableGraphWriter.WriteAsync(g.DatabasePath, References(), CancellationToken.None);

        Assert.Equal(2, stats.TablesCreated);  // ORDERS y CUSTOMERS
        Assert.Equal(1, stats.TablesReused);   // INVOICES ya era un Table del grafo
        Assert.Equal(3, stats.QueriesEdges);   // find→ORDERS, save→CUSTOMERS, OrderDao→ORDERS (línea 40, fuera de un método)
        Assert.Equal(2, stats.ModifiesEdges);  // save→ORDERS, total→INVOICES
        Assert.Equal(1, stats.ReferencesUnresolved);
        var orders = (await g.NodeIdAsync("sqltable:ORDERS"))!.Value;
        Assert.Equal(1, await g.CountAsync("SELECT COUNT(1) FROM edges WHERE source_id = @s AND target_id = @t AND type = 'QUERIES'", ("@s", ids.Find), ("@t", orders)));
        Assert.Equal(1, await g.CountAsync("SELECT COUNT(1) FROM edges WHERE source_id = @s AND target_id = @t AND type = 'MODIFIES'", ("@s", ids.Save), ("@t", orders)));
        Assert.Equal(1, await g.CountAsync("SELECT COUNT(1) FROM edges WHERE source_id = @s AND target_id = @t AND type = 'QUERIES'", ("@s", ids.Dao), ("@t", orders)));
        Assert.Equal(1, await g.CountAsync("SELECT COUNT(1) FROM edges WHERE source_id = @s AND target_id = @t AND type = 'MODIFIES'", ("@s", ids.Total), ("@t", ids.Invoices)));
        Assert.Equal("src/orders/OrderDao.java", await g.TextAsync(
            "SELECT json_extract(properties, '$.file') FROM edges WHERE source_id = @s AND type = 'MODIFIES'", ("@s", ids.Save)));
        Assert.Equal(16, await g.CountAsync("SELECT json_extract(properties, '$.line') FROM edges WHERE source_id = @s AND type = 'MODIFIES'", ("@s", ids.Save)));
    }

    [Fact]
    public async Task Rewriting_sql_tables_is_idempotent_and_keeps_the_table_nodes()
    {
        var (g, ids) = await GraphAsync();
        using var _ = g;

        await SqlTableGraphWriter.WriteAsync(g.DatabasePath, References(), CancellationToken.None);
        var orders = await g.NodeIdAsync("sqltable:ORDERS");
        var stats = await SqlTableGraphWriter.WriteAsync(g.DatabasePath, References(withCustomers: false, extraFind: true), CancellationToken.None);

        Assert.Equal(orders, await g.NodeIdAsync("sqltable:ORDERS"));
        Assert.Null(await g.NodeIdAsync("sqltable:CUSTOMERS"));
        Assert.Equal(1, stats.PreviousNodesRemoved);
        Assert.Equal(2, await CountEdgesAsync(g, "QUERIES")); // find→ORDERS (líneas 8 y 9 en una arista) y OrderDao→ORDERS
        Assert.Equal("[8,9]", await g.TextAsync("SELECT json_extract(properties, '$.lines') FROM edges WHERE source_id = @s AND type = 'QUERIES'", ("@s", ids.Find)));
        Assert.Equal(2, await CountEdgesAsync(g, "MODIFIES"));
        Assert.Equal(1, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE label = 'Table' AND name = 'INVOICES'"));
    }

    [Fact]
    public async Task The_inventory_does_not_count_the_tables_written_by_E2()
    {
        var (g, _) = await GraphAsync();
        using var __ = g;
        await SqlTableGraphWriter.WriteAsync(g.DatabasePath, References(), CancellationToken.None);

        var data = await InventoryGraphReader.ReadAsync(g.DatabasePath, 100, 100, CancellationToken.None);

        var table = Assert.Single(data.Tables);
        Assert.Equal("INVOICES", table.Name);
        Assert.Equal(0, table.References); // la MODIFIES de E2 hacia el Table de cbm tampoco cuenta
    }

    [Fact]
    public void The_scanner_marks_the_written_table_of_each_statement()
    {
        static Dictionary<string, bool> Writes(string sql) =>
            SqlTableScanner.ScanText("src/A.java", $"class A {{ String s = \"{sql}\"; }}", java: true).ToDictionary(r => r.Name, r => r.Writes);

        Assert.Equal(new Dictionary<string, bool> { ["target"] = true, ["source"] = false }, Writes("INSERT INTO target (a) SELECT a FROM source"));
        Assert.Equal(new Dictionary<string, bool> { ["t"] = true, ["u"] = false }, Writes("UPDATE t SET a = 1 WHERE id IN (SELECT id FROM u)"));
        Assert.Equal(new Dictionary<string, bool> { ["t"] = true, ["u"] = false }, Writes("DELETE FROM t WHERE id IN (SELECT id FROM u)"));
        Assert.Equal(new Dictionary<string, bool> { ["orders"] = false, ["items"] = false }, Writes("SELECT x FROM orders o JOIN items i ON o.id = i.id"));
        Assert.Equal(new Dictionary<string, bool> { ["t"] = true }, Writes("TRUNCATE TABLE t"));
    }

    // ───────────────────────────── E3 ─────────────────────────────

    [Fact]
    public void Citations_are_parsed_with_their_first_line_and_excerpt()
    {
        var citations = InvestigationNoteGraphWriter.ParseCitations(NoteText);

        Assert.Equal(
            ["src/orders/OrderDao.java:8", "OrderService.java:6-9", "C:/repo/src/orders/OrderDao.java:16", "missing/Gone.java:3", "src/orders/OrderDao.java:55"],
            citations.Select(c => c.Reference));
        Assert.Equal(6, citations[1].Line);
        Assert.Equal("El estado se fija en `OrderService.java:6-9` al colocar el pedido.", citations[1].Excerpt);
        Assert.Empty(InvestigationNoteGraphWriter.ParseCitations("Ver http://host:8080 y la versión 1.2:3 sin ruta"));
    }

    [Fact]
    public async Task A_note_cites_the_most_specific_node_of_each_reference_and_ignores_the_unresolved()
    {
        var (g, ids) = await GraphAsync();
        using var _ = g;

        var stats = await InvestigationNoteGraphWriter.WriteAsync(g.DatabasePath, Note(), CancellationToken.None);

        Assert.Equal(5, stats.Citations);
        Assert.Equal(4, stats.CitationsResolved);
        Assert.Equal(1, stats.CitationsUnresolved);
        Assert.Equal(4, stats.CitesEdges);
        Assert.False(stats.LinkedToSubsystem); // aún no hay nodos Subsystem
        foreach (var target in new[] { ids.Find, ids.Place, ids.Save, ids.Dao })
            Assert.Equal(1, await g.CountAsync("SELECT COUNT(1) FROM edges WHERE source_id = @n AND target_id = @t AND type = 'CITES'", ("@n", stats.NoteId), ("@t", target)));
        Assert.Equal("[\"OrderService.java:6-9\"]", await g.TextAsync(
            "SELECT json_extract(properties, '$.refs') FROM edges WHERE source_id = @n AND target_id = @t", ("@n", stats.NoteId), ("@t", ids.Place)));
        Assert.Equal("Funcional", await g.TextAsync("SELECT json_extract(properties, '$.report') FROM nodes WHERE id = @id", ("@id", stats.NoteId)));
    }

    [Fact]
    public async Task Notes_are_idempotent_per_report_and_subsystem_and_get_linked_to_their_subsystem()
    {
        var (g, _) = await GraphAsync();
        using var __ = g;

        await InvestigationNoteGraphWriter.WriteAsync(g.DatabasePath, Note(), CancellationToken.None);
        var subsystems = await SubsystemGraphWriter.WriteAsync(g.DatabasePath, Map(), CancellationToken.None);
        Assert.Equal(1, subsystems.NotesRelinked);

        var again = await InvestigationNoteGraphWriter.WriteAsync(g.DatabasePath, Note("Solo `src/orders/OrderDao.java:8`."), CancellationToken.None);
        await InvestigationNoteGraphWriter.WriteAsync(g.DatabasePath, Note() with { Report = "Flujos" }, CancellationToken.None);

        Assert.Equal(1, again.PreviousRemoved);
        Assert.True(again.LinkedToSubsystem);
        Assert.Equal(2, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE label = 'InvestigationNote'"));
        Assert.Equal(1 + 4, await CountEdgesAsync(g, "CITES"));
        Assert.Equal(2, await CountEdgesAsync(g, "ABOUT"));

        // Reescribir los subsistemas conserva su id y con él las ABOUT.
        await SubsystemGraphWriter.WriteAsync(g.DatabasePath, Map(), CancellationToken.None);
        Assert.Equal(2, await CountEdgesAsync(g, "ABOUT"));
    }

    // ───────────────────────────── Vistas del chat ─────────────────────────────

    private async Task<CbmTestGraph> FullGraphAsync()
    {
        var (g, _) = await GraphAsync();
        await SubsystemGraphWriter.WriteAsync(g.DatabasePath, Map(), CancellationToken.None);
        await SqlTableGraphWriter.WriteAsync(g.DatabasePath, References(), CancellationToken.None);
        await InvestigationNoteGraphWriter.WriteAsync(g.DatabasePath, Note(), CancellationToken.None);
        return g;
    }

    [Fact]
    public async Task Subsystems_view_lists_them_and_details_one_with_members_tables_dependencies_and_notes()
    {
        using var g = await FullGraphAsync();

        var list = await DomainAsync("""{"view":"subsystems"}""", g.DatabasePath);
        Assert.Contains("# Subsistemas del análisis (2)", list);
        Assert.Contains("| **Pedidos** | `orders` | capability | 3 | 900 | 1 | 1 / 1 | 1 | Gestión de pedidos |", list);
        Assert.True(list.IndexOf("Pedidos", StringComparison.Ordinal) < list.IndexOf("Facturación", StringComparison.Ordinal)); // por LOC

        var detail = await DomainAsync("""{"view":"subsystems","name":"pedidos"}""", g.DatabasePath);
        Assert.Contains("# Subsistema **Pedidos**", detail);
        Assert.Contains("struts_action /orders/place · src/orders/OrderService.java:5", detail);
        Assert.Contains("Rutas que atienden sus clases: `/orders/place`", detail);
        Assert.Contains("| `Class` | **OrderDao** |", detail);
        Assert.Contains("| `ORDERS` | 2 | 1 |", detail);
        Assert.Contains("| `CUSTOMERS` | 1 | 0 |", detail);
        Assert.Contains("## Depende de (1)", detail);
        Assert.Contains("| **Facturación** | 1 | CALLS 1 |", detail);
        Assert.Contains("OrderDao.find consulta la tabla", detail);

        var byId = await DomainAsync("""{"view":"subsystems","name":"billing"}""", g.DatabasePath);
        Assert.Contains("# Subsistema **Facturación**", byId);
        Assert.Contains("| `INVOICES` | 0 | 1 |", byId);
    }

    [Fact]
    public async Task Tables_view_lists_them_and_says_who_reads_and_who_writes_with_file_and_line()
    {
        using var g = await FullGraphAsync();

        var list = await DomainAsync("""{"view":"tables"}""", g.DatabasePath);
        Assert.Contains("| `ORDERS` | - | 2 | 1 | SQL en el código |", list);
        Assert.Contains("| `INVOICES` | - | 0 | 1 | grafo (Table) |", list);

        var detail = await DomainAsync("""{"view":"tables","name":"app.orders"}""", g.DatabasePath);
        Assert.Contains("## Escriben (MODIFIES): 1", detail);
        Assert.Contains("| `Method` | **save** | `demo.src.orders.OrderDao.OrderDao.save` | `src/orders/OrderDao.java:16` | INSERT | Pedidos |", detail);
        Assert.Contains("## Leen (QUERIES): 2", detail);
        Assert.Contains("`src/orders/OrderDao.java:8`", detail);

        var missing = await DomainAsync("""{"view":"tables","name":"NOPE"}""", g.DatabasePath);
        Assert.Contains("No hay ninguna tabla con ese nombre", missing);
    }

    [Fact]
    public async Task Neighbors_of_a_method_show_its_tables_and_the_notes_that_cite_it()
    {
        using var g = await FullGraphAsync();
        var context = new PipelineContext();
        context.Set(AnalysisContextKeys.CbmDbPath, g.DatabasePath);

        var text = await new GraphNeighborsTool().ExecuteAsync(
            """{"qualified_name":"demo.src.orders.OrderDao.OrderDao.find"}""", context, "ChatAssistant", NullLogger.Instance);
        output.WriteLine(text);

        Assert.Contains("## `QUERIES` salientes", text);
        Assert.Contains("lee la tabla · SELECT", text);
        Assert.Contains("## `CITES` entrantes", text);
        Assert.Contains("nota de investigación (LLM)", text);
        Assert.Contains("cita `src/orders/OrderDao.java:8`", text);
        Assert.Contains("«OrderDao.find consulta la tabla `src/orders/OrderDao.java:8`.»", text);
    }
}
