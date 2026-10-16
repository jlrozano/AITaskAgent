using System.Text.Json;
using System.Text.RegularExpressions;
using AITaskAgent.Core.Models;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tests.Enrichment;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="RepositoryInventoryStep"/> (T6, criterio A1): inventario determinista sin LLM sobre un repositorio temporal y un
/// grafo sintético. Dos ejecuciones dan el mismo JSON salvo <c>generated_at</c>; un lenguaje sin enriquecedor se declara «sin
/// enriquecer»; y un fallo interno es un aviso y el input sale intacto.
/// </summary>
public sealed class RepositoryInventoryStepTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Repo => Path.Combine(_temp.Path, "repo");

    private string ReportsPath => Path.Combine(Repo, "reports");

    private void Write(string relative, string content)
    {
        var path = Path.Combine(Repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>Un repositorio Maven con Java, una JSP, un fichero JS y código de test y de qa.</summary>
    private void WriteMavenRepo()
    {
        Write("pom.xml", "<project/>");
        Write("src/main/java/com/acme/billing/Invoice.java", "package com.acme.billing;\nclass Invoice {\n  int total;\n}\n");
        Write("src/main/java/com/acme/billing/PayAction.java", "package com.acme.billing;\nclass PayAction {}\n");
        Write("src/main/java/com/acme/jobs/Cleanup.java", "class Cleanup {\n  void nightly() {}\n}\n");
        Write("src/main/resources/app.properties", "k=v\n");
        Write("src/main/webapp/WEB-INF/struts-config.xml", "<struts-config/>");
        Write("src/main/webapp/pay.jsp", "<html>\n</html>\n");
        Write("src/main/webapp/js/app.js", "var a;\nvar b;\n");
        Write("src/test/java/com/acme/billing/InvoiceTest.java", "class InvoiceTest {}\n");
        Write("src/test/resources/fixture.xml", "<x/>");
        Write("qa/Smoke.java", "class Smoke {}\n");
        Write("docs/readme.md", "# docs\n");
        Write("api/pom.xml", "<project/>");
        Write("api/src/main/java/com/acme/Api.java", "class Api {}\n");
    }

    private async Task<CbmTestGraph> NewGraphAsync(bool withFiles = true)
    {
        var graph = await CbmTestGraph.CreateAsync(Path.Combine(_temp.Path, "codebase.db"), "demo");
        if (withFiles)
        {
            await graph.NodeAsync("Class", "Invoice", "demo.billing.Invoice", "src/main/java/com/acme/billing/Invoice.java", 1, 4);
            var pay = await graph.NodeAsync("Class", "PayAction", "demo.billing.PayAction", "src/main/java/com/acme/billing/PayAction.java", 1, 2);
            var route = await graph.NodeAsync("Route", "/pay", "__route__ANY__/pay", "src/main/webapp/WEB-INF/struts-config.xml", 3, 3,
                "{\"framework\":\"struts\",\"route_path\":\"/pay.do\"}");
            await graph.EdgeAsync(pay, route, "HANDLES");
            await graph.NodeAsync("Method", "nightly", "demo.jobs.Cleanup.nightly", "src/main/java/com/acme/jobs/Cleanup.java", 2, 2,
                "{\"annotations\":[\"Scheduled\"]}");
            await graph.NodeAsync("Method", "main", "demo.qa.Smoke.main", "qa/Smoke.java", 1, 1);
            var table = await graph.NodeAsync("Table", "INVOICES", "table.INVOICES");
            await graph.EdgeAsync(pay, table, "READS");
        }
        return graph;
    }

    private (PipelineContext Context, CapturingReporter Reporter) NewContext(string database, params (string, string?)[] config)
    {
        var reporter = new CapturingReporter();
        var context = EnrichmentStepContext.Create(TestConfig.FromPairs(config), reporter, Repo, database);
        return (context, reporter);
    }

    private static Task<ReportFacetInput> RunAsync(PipelineContext context, ReportFacetInput? input = null) =>
        new RepositoryInventoryStep().ExecuteAsync(input ?? new ReportFacetInput(), context, 1, null, CancellationToken.None);

    private RepositoryInventory ReadArtifact()
    {
        var inventory = PreparationArtifacts.TryRead<RepositoryInventory>(ReportsPath, out var warning);
        Assert.Null(warning);
        return inventory!;
    }

    [Fact]
    public async Task The_artifact_caps_the_entry_points_but_the_context_keeps_all_of_them_for_the_domain_map()
    {
        WriteMavenRepo();
        using var graph = await NewGraphAsync();
        var total = RepositoryInventoryStep.MaxEntryPoints + 10;
        for (var i = 0; i < total; i++)
            await graph.NodeAsync("Method", "main", $"demo.tools.Tool{i:000}.main", $"src/main/java/com/acme/tools/Tool{i:000}.java", 1, 1);
        var (context, _) = NewContext(graph.DatabasePath);

        await RunAsync(context);

        var inventory = ReadArtifact();
        Assert.Equal(RepositoryInventoryStep.MaxEntryPoints, inventory.EntryPoints.Count);
        Assert.Equal(total + 2, inventory.EntryPointsTotal);
        var all = context.Get<IReadOnlyList<InventoryEntryPoint>>(AnalysisContextKeys.InventoryEntryPoints)!;
        Assert.Equal(inventory.EntryPointsTotal, all.Count);
        Assert.Contains(all, e => e.File == "src/main/java/com/acme/tools/Tool209.java");
    }

    [Fact]
    public async Task It_inventories_modules_source_sets_entries_languages_coverage_and_lines()
    {
        WriteMavenRepo();
        using var graph = await NewGraphAsync();
        var (context, _) = NewContext(graph.DatabasePath);
        var input = new ReportFacetInput();

        var result = await RunAsync(context, input);

        Assert.Same(input, result);
        var inventory = ReadArtifact();
        Assert.Same(inventory.GetType(), context.Get<RepositoryInventory>(AnalysisContextKeys.RepositoryInventory)!.GetType());
        Assert.Equal("demo|2026-09-28T10:00:00Z", inventory.GraphFingerprint);

        Assert.Equal([("", "maven", "pom.xml"), ("api", "maven", "pom.xml")], inventory.Modules.Select(m => (m.Path, m.Kind, m.BuildFile)));

        Assert.Equal(
            [("production", 6, 14L), ("test", 1, 1L), ("qa", 1, 1L)],
            inventory.SourceSets.Select(s => (s.SourceSet, s.Files, s.Lines)));

        Assert.Equal(14, inventory.ProductionLoc.Total);
        Assert.Equal(
            [("java", 4, 10L), ("javascript", 1, 2L), ("jsp", 1, 2L)],
            inventory.ProductionLoc.ByLanguage.Select(l => (l.Language, l.Files, l.Lines)));
        Assert.Equal(
            ["api/src/main/java/com/acme/Api.java", "src/main/java/com/acme/billing/Invoice.java", "src/main/java/com/acme/billing/PayAction.java",
                "src/main/java/com/acme/jobs/Cleanup.java", "src/main/webapp/js/app.js", "src/main/webapp/pay.jsp"],
            inventory.ProductionLoc.Files.Select(f => f.Path));

        // La entrada del Smoke (qa) no cuenta; la acción de Struts lleva el fichero de su clase.
        Assert.Equal(
            [("scheduled", "demo.jobs.Cleanup.nightly"), ("struts_action", "/pay.do")],
            inventory.EntryPoints.Select(e => (e.Kind, e.Name)));
        Assert.Equal(2, inventory.EntryPointsTotal);
        Assert.Equal("src/main/java/com/acme/billing/PayAction.java", inventory.EntryPoints.Single(e => e.Kind == "struts_action").File);

        Assert.Equal([("INVOICES", 1)], inventory.Persistence.Tables.Select(t => (t.Name, t.References)));
        Assert.Equal(["src/main/resources/app.properties", "src/main/webapp/WEB-INF/struts-config.xml"], inventory.Persistence.ConfigurationFiles);
        Assert.Equal(2, inventory.Persistence.ConfigurationFilesTotal);

        Assert.Equal(6, inventory.GraphCoverage.FilesOnDisk);
        Assert.Equal(3, inventory.GraphCoverage.FilesInGraph);
        Assert.Equal(3, inventory.GraphCoverage.FilesNotInGraph);
        Assert.Equal(["api/src/main/java/com/acme/Api.java", "src/main/webapp/js/app.js", "src/main/webapp/pay.jsp"], inventory.GraphCoverage.NotInGraphSample);
        Assert.Null(inventory.GraphCoverage.CbmMatch);
    }

    [Fact]
    public async Task Tables_written_as_sql_in_strings_join_the_graph_tables_in_the_inventory_and_the_context()
    {
        WriteMavenRepo();
        Write("src/main/java/com/acme/billing/InvoiceDao.java", """
            package com.acme.billing;
            class InvoiceDao {
                static final String COLUMNS = "SELECT id, total FROM invoices";
                void a() { run(COLUMNS + " WHERE id = ?"); run("INSERT INTO payment (id) VALUES (?)"); }
                void b() { run("UPDATE payment SET paid = 1"); }
            }
            """);
        Write("src/test/java/com/acme/billing/DaoTest.java", "class DaoTest { String s = \"SELECT * FROM only_in_tests\"; }");
        using var graph = await NewGraphAsync();
        var (context, _) = NewContext(graph.DatabasePath);

        await RunAsync(context);

        var inventory = ReadArtifact();
        // INVOICES viene del grafo (1 referencia) y no se repite por el SQL; `payment` solo está en las cadenas.
        Assert.Equal(
            [("payment", 2, "sql_literal"), ("INVOICES", 1, "graph")],
            inventory.Persistence.Tables.Select(t => (t.Name, t.References, t.Source)));
        Assert.Equal(2, inventory.Persistence.TablesTotal);
        Assert.DoesNotContain(inventory.Persistence.Tables, t => t.Name == "only_in_tests");
        var refs = context.Get<IReadOnlyList<SqlTableReference>>(AnalysisContextKeys.InventorySqlTables)!;
        Assert.Equal(["invoices", "invoices", "payment", "payment"], refs.Select(r => r.Name));
        Assert.All(refs, r => Assert.Equal("src/main/java/com/acme/billing/InvoiceDao.java", r.File));
    }

    [Fact]
    public async Task Without_table_nodes_in_the_graph_the_sql_tables_are_the_inventory_tables_and_the_compact_summary_carries_them()
    {
        WriteMavenRepo();
        Write("src/main/java/com/acme/billing/ReportDao.java", "class ReportDao { String s = \"SELECT * FROM app.report r JOIN ledger l ON l.id = r.id\"; }");
        using var graph = await NewGraphAsync(withFiles: false);
        var (context, _) = NewContext(graph.DatabasePath);
        var input = new ReportFacetInput();

        await RunAsync(context, input);

        var inventory = ReadArtifact();
        Assert.Equal(
            [("ledger", (string?)null, "sql_literal"), ("report", "app", "sql_literal")],
            inventory.Persistence.Tables.Select(t => (t.Name, t.Schema, t.Source)));
        using var summary = JsonDocument.Parse(input.RepositoryInventory);
        Assert.Equal(2, summary.RootElement.GetProperty("tables_total").GetInt32());
    }

    [Fact]
    public async Task A_scan_that_stops_early_is_a_warning_and_the_inventory_is_still_written()
    {
        WriteMavenRepo();
        Write("src/main/java/com/acme/billing/A.java", "class A { String s = \"SELECT * FROM first_t\"; }");
        Write("src/main/java/com/acme/billing/B.java", "class B { String s = \"SELECT * FROM second_t\"; }");
        using var graph = await NewGraphAsync();
        var (context, reporter) = NewContext(graph.DatabasePath);

        await new RepositoryInventoryStep(sqlScanMaxFiles: 2).ExecuteAsync(new ReportFacetInput(), context, 1, null, CancellationToken.None);

        var inventory = ReadArtifact();
        Assert.Contains(inventory.Warnings, w => w.Contains("tablas", StringComparison.Ordinal) && w.Contains("incompletas", StringComparison.Ordinal));
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Message.Contains("incompletas", StringComparison.Ordinal));
        Assert.Contains(inventory.Persistence.Tables, t => t.Name == "first_t");
        Assert.DoesNotContain(inventory.Persistence.Tables, t => t.Name == "second_t");
    }

    [Fact]
    public async Task Two_runs_over_the_same_graph_and_disk_give_the_same_json_except_generated_at()
    {
        WriteMavenRepo();
        using var graph = await NewGraphAsync();
        var (context, _) = NewContext(graph.DatabasePath);

        await RunAsync(context);
        var first = File.ReadAllText(Path.Combine(ReportsPath, RepositoryInventory.FileName));
        await Task.Delay(30);
        var (second, _) = NewContext(graph.DatabasePath);
        await RunAsync(second);
        var again = File.ReadAllText(Path.Combine(ReportsPath, RepositoryInventory.FileName));

        Assert.NotEqual(first, again);
        Assert.Equal(WithoutTimestamp(first), WithoutTimestamp(again));
    }

    private static string WithoutTimestamp(string json) => Regex.Replace(json, "\"generated_at\": \"[^\"]*\"", "\"generated_at\": \"\"");

    [Fact]
    public async Task A_language_without_an_enricher_is_declared_not_enriched_and_is_not_invented_edges_for()
    {
        WriteMavenRepo();
        using var graph = await NewGraphAsync();
        var (context, _) = NewContext(graph.DatabasePath);

        await RunAsync(context);

        var languages = ReadArtifact().Languages.ToDictionary(l => l.Language);
        var js = languages["javascript"];
        Assert.Equal(LanguageStatuses.NotEnriched, js.Status);
        Assert.Null(js.Enricher);
        Assert.Null(js.RunId);
        Assert.Equal(1, js.Files);
        Assert.Contains("enriquecedor", js.Detail);
        // Los que sí tienen enriquecedor y no se ejecutó ninguno quedan sin enriquecer y nombran cuál les tocaba.
        Assert.Equal(LanguageStatuses.NotEnriched, languages["java"].Status);
        Assert.Equal("JavaAnalyzer", languages["java"].Enricher);
        Assert.Equal("StrutsJspPlugin", languages["jsp"].Enricher);
        Assert.Equal(["java", "javascript", "jsp"], ReadArtifact().Languages.Select(l => l.Language));
    }

    [Fact]
    public async Task The_language_status_comes_from_the_enrichment_runs_of_the_current_index()
    {
        WriteMavenRepo();
        Write("Billing/Invoice.cs", "class Invoice {}\n");
        using var graph = await NewGraphAsync();
        var fingerprint = (await EnrichmentRuns.IndexFingerprintAsync(graph.Connection, CancellationToken.None))!;
        async Task Run(string enricher, string status, string? message = null) =>
            await EnrichmentRuns.InsertAsync(graph.Connection, new EnrichmentRun
            {
                Enricher = enricher, EnricherVersion = "1", IndexFingerprint = fingerprint, ConfigFingerprint = "c",
                StartedAt = DateTimeOffset.UtcNow, Status = status, Message = message,
                Outcome = enricher == "JavaAnalyzer" ? new EnrichmentOutcome { Details = "{\"cbm_match\":{\"unindexed_files\":2,\"drift_suspected\":false}}" } : null
            }, CancellationToken.None);
        await Run("JavaAnalyzer", EnrichmentRuns.StatusOk);
        await Run("DotNetEnricher", EnrichmentRuns.StatusFailed, "no carga la solución");
        var (context, _) = NewContext(graph.DatabasePath);

        await RunAsync(context);

        var inventory = ReadArtifact();
        var languages = inventory.Languages.ToDictionary(l => l.Language);
        Assert.Equal(LanguageStatuses.Enriched, languages["java"].Status);
        Assert.Equal(1L, languages["java"].RunId);
        Assert.Equal(LanguageStatuses.Failed, languages["csharp"].Status);
        Assert.Contains("no carga la solución", languages["csharp"].Detail);
        Assert.Equal(LanguageStatuses.NotEnriched, languages["jsp"].Status);
        Assert.Equal(2, inventory.GraphCoverage.CbmMatch?.UnindexedFiles);
    }

    [Fact]
    public async Task Without_a_build_file_there_is_a_single_root_module()
    {
        Write("Billing/Invoice.cs", "class Invoice {}\n");
        using var graph = await NewGraphAsync(withFiles: false);
        var (context, _) = NewContext(graph.DatabasePath);

        await RunAsync(context);

        var module = Assert.Single(ReadArtifact().Modules);
        Assert.Equal(("", "root", ""), (module.Path, module.Kind, module.BuildFile));
    }

    [Fact]
    public async Task Dotnet_and_other_build_files_define_modules()
    {
        Write("Billing/Billing.csproj", "<Project/>");
        Write("Billing/Invoice.cs", "class Invoice {}\n");
        Write("web/package.json", "{}");
        Write("web/app.ts", "let a;\n");
        Write("svc/build.gradle.kts", "");
        using var graph = await NewGraphAsync(withFiles: false);
        var (context, _) = NewContext(graph.DatabasePath);

        await RunAsync(context);

        Assert.Equal(
            [("Billing", "dotnet", "Billing.csproj"), ("svc", "gradle", "build.gradle.kts"), ("web", "node", "package.json")],
            ReadArtifact().Modules.Select(m => (m.Path, m.Kind, m.BuildFile)));
    }

    [Fact]
    public async Task The_compact_version_goes_to_the_facet_input_without_the_per_file_lines()
    {
        WriteMavenRepo();
        using var graph = await NewGraphAsync();
        var (context, _) = NewContext(graph.DatabasePath);

        var result = await RunAsync(context);

        using var doc = JsonDocument.Parse(result.RepositoryInventory);
        var root = doc.RootElement;
        Assert.Equal(14, root.GetProperty("production_loc").GetProperty("total").GetInt32());
        Assert.False(root.GetProperty("production_loc").TryGetProperty("files", out _));
        Assert.Equal(2, root.GetProperty("entry_points_total").GetInt32());
        Assert.Equal("maven", root.GetProperty("modules")[0].GetProperty("kind").GetString());
        Assert.DoesNotContain('\n', result.RepositoryInventory);
        // Los demás campos de preparación no los toca este paso.
        Assert.Equal("{}", result.DomainMap);
        Assert.Equal("{}", result.CoveragePlan);
    }

    [Fact]
    public async Task Without_a_graph_the_inventory_still_comes_out_with_a_warning()
    {
        WriteMavenRepo();
        var (context, reporter) = NewContext(Path.Combine(_temp.Path, "no-existe.db"));

        var result = await RunAsync(context);

        var inventory = ReadArtifact();
        Assert.NotEqual("{}", result.RepositoryInventory);
        Assert.Equal(6, inventory.GraphCoverage.FilesOnDisk);
        Assert.Equal(0, inventory.GraphCoverage.FilesInGraph);
        Assert.Contains(inventory.Warnings, w => w.Contains("grafo", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning);
    }

    [Fact]
    public async Task Graph_paths_that_match_nothing_on_disk_are_flagged()
    {
        WriteMavenRepo();
        using var graph = await CbmTestGraph.CreateAsync(Path.Combine(_temp.Path, "codebase.db"), "demo");
        await graph.NodeAsync("Class", "Invoice", "demo.Invoice", "elsewhere/other/Invoice.java", 1, 4);
        var (context, _) = NewContext(graph.DatabasePath);

        await RunAsync(context);

        var inventory = ReadArtifact();
        Assert.Equal(0, inventory.GraphCoverage.FilesInGraph);
        Assert.Contains(inventory.Warnings, w => w.Contains("ninguna ruta", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_failure_inside_the_step_is_a_warning_and_the_input_is_returned_untouched()
    {
        var (context, reporter) = NewContext(Path.Combine(_temp.Path, "x.db"));
        context.Set(AnalysisContextKeys.PipelineInput, new AnalysisPipelineInput { RepositoryPath = "ruta\0invalida", OutputReportsPath = ReportsPath });
        var input = new ReportFacetInput();

        var result = await RunAsync(context, input);

        Assert.Same(input, result);
        Assert.Equal("{}", result.RepositoryInventory);
        Assert.Null(context.Get<RepositoryInventory>(AnalysisContextKeys.RepositoryInventory));
        Assert.False(File.Exists(Path.Combine(ReportsPath, RepositoryInventory.FileName)));
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Source == RepositoryInventoryStep.StepName);
    }

    [Fact]
    public async Task A_cancelled_run_is_not_swallowed()
    {
        WriteMavenRepo();
        using var graph = await NewGraphAsync();
        var (context, _) = NewContext(graph.DatabasePath);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RepositoryInventoryStep().ExecuteAsync(new ReportFacetInput(), context, 1, null, cts.Token));
    }

    [Fact]
    public async Task With_the_preparation_off_the_step_does_nothing()
    {
        WriteMavenRepo();
        using var graph = await NewGraphAsync();
        var (context, _) = NewContext(graph.DatabasePath, ("CodebaseAnalysis:Preparation:Mode", "Off"));
        var input = new ReportFacetInput();

        var result = await RunAsync(context, input);

        Assert.Same(input, result);
        Assert.Equal("{}", result.RepositoryInventory);
        Assert.False(File.Exists(Path.Combine(ReportsPath, RepositoryInventory.FileName)));
    }

    [Fact]
    public async Task Invalid_preparation_options_are_reported_as_warnings_and_the_step_goes_on()
    {
        WriteMavenRepo();
        using var graph = await NewGraphAsync();
        var (context, reporter) = NewContext(graph.DatabasePath, ("CodebaseAnalysis:Preparation:MaxSubsystemsPerReport", "muchos"));

        await RunAsync(context);

        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Message.Contains("MaxSubsystemsPerReport"));
        Assert.True(File.Exists(Path.Combine(ReportsPath, RepositoryInventory.FileName)));
    }

    [Fact]
    public async Task The_reports_folder_is_not_inventoried_and_a_configured_test_root_is_excluded_from_production()
    {
        WriteMavenRepo();
        Write("suite/unit/Case.java", "class Case {}\n");
        Write("reports/old/Leftover.java", "class Leftover {}\n");
        using var graph = await NewGraphAsync();
        var (context, _) = NewContext(graph.DatabasePath, ("JavaAnalyzer:TestRoots:0", "suite/unit"));

        await RunAsync(context);

        var inventory = ReadArtifact();
        Assert.DoesNotContain(inventory.ProductionLoc.Files, f => f.Path.StartsWith("suite/") || f.Path.StartsWith("reports/"));
        Assert.Equal(2, inventory.SourceSets.Single(s => s.SourceSet == "test").Files);
    }
}
