using AITaskAgent.Configuration;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Results;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// La marca de grafo enriquecido (<see cref="GraphStatusResult.IsEnriched"/>) es la del enriquecimiento de código de
/// <see cref="DotNetEnricherStep"/>: ni la ingesta de Office ni el analizador Java la ponen (auditoría 2026-09-29, H-10), y
/// tampoco el fallback del LLM, cuya salida se marca y se filtra en código (H-3).
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class EnrichedGraphMarkTests
{
    private static async Task CreateGraphAsync(string dbPath, params (string Label, string Qn, string Properties)[] nodes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        await GraphSchema.EnsureAsync(dbPath, CancellationToken.None);
        await using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await conn.OpenAsync();
        foreach (var (label, qn, properties) in nodes)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO nodes (project, label, name, qualified_name, file_path, start_line, end_line, properties)
                VALUES ('Codebase', @label, @qn, @qn, 'src/Sample.cs', 1, 10, @properties);
                """;
            cmd.Parameters.AddWithValue("@label", label);
            cmd.Parameters.AddWithValue("@qn", qn);
            cmd.Parameters.AddWithValue("@properties", properties);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [InlineData("Class", """{"cyclomatic":3}""", true)]
    [InlineData("CsProject", "{}", true)]
    [InlineData("Class", """{"afferent_coupling":2,"origin":"ai_enricher"}""", false)]
    [InlineData("Method", """{"cyclomatic":4,"origin":"access_vba_extractor"}""", false)]
    [InlineData("AccessTable", """{"in_cycle":true,"origin":"office_tools"}""", false)]
    [InlineData("Method", """{"cyclomatic":2,"enriched_by":["JavaAnalyzer"]}""", false)]
    [InlineData("Method", """{"cyclomatic":2,"origin":"enricher","enriched_by":["JavaAnalyzer"]}""", false)]
    [InlineData("Class", "{}", false)]
    public async Task Only_the_code_enrichment_marks_the_graph_as_enriched(string label, string properties, bool enriched)
    {
        using var temp = new TempDirectory();
        var db = Path.Combine(temp.Path, "codebase.db");
        await CreateGraphAsync(db, (label, "Demo.Node", properties));

        Assert.Equal(enriched, await DetectAndIndexGraphStep.CheckIfEnrichedAsync(db, CancellationToken.None));
    }

    [Fact]
    public async Task A_docx_in_the_repository_does_not_mark_the_graph_as_enriched()
    {
        using var repo = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(repo.Path, "docs"));
        await File.WriteAllBytesAsync(Path.Combine(repo.Path, "docs", "manual.docx"), SampleFiles.Docx());
        var configuration = TestConfig.FromPairs();
        var input = AnalysisRunOptions.BuildInput(configuration, repo.Path, _ => { });
        // Un grafo estructural sin enriquecer: no se indexa y la ingesta de Office corre.
        await CreateGraphAsync(input.DatabasePath, ("Class", "Demo.Sample", "{}"));
        var services = new ServiceCollection().AddSingleton(configuration).AddLogging().BuildServiceProvider();
        var reporter = new CapturingReporter();
        var context = AnalysisRunner.CreateContext(input, services, null, reporter);

        var status = await new DetectAndIndexGraphStep().ExecuteAsync(input, context, 1, null, CancellationToken.None);

        Assert.Contains(reporter.Entries, e => e.Message.Contains("Ingestando artefactos de Office", StringComparison.Ordinal));
        Assert.False(status.IsEnriched);
    }

    /// <summary>Respuesta del modelo con métricas, marcas de origen propias y una arista que ya existe en cbm.</summary>
    private const string CannedEnrichment = """
        {
          "nodes": [
            { "label": "Route", "name": "/api/orders", "qualified_name": "__route__POST__/api/orders", "file": "OrdersController.cs", "line": 12,
              "properties": { "origin": "otro", "enriched_by": "Modelo", "enriched_at": "ayer", "http_verb": "POST", "route_path": "/api/orders",
                              "auth_required": true, "cyclomatic": 5, "fan_in": 3 } },
            { "label": "Class", "name": "OrdersController", "qualified_name": "Demo.OrdersController", "file": "OrdersController.cs", "line": 1,
              "properties": { "docstring": "Controlador de pedidos", "loc_total": 400, "afferent_coupling": 7 } }
          ],
          "edges": [
            { "source_qualified_name": "Demo.OrdersController", "target_qualified_name": "__route__POST__/api/orders", "type": "HANDLES",
              "properties": { "origin": "ai_enricher", "presentational": true, "confidence": 0.9 } },
            { "source_qualified_name": "Demo.OrdersController", "target_qualified_name": "Demo.Base", "type": "INHERITS",
              "properties": { "presentational": false } }
          ],
          "node_properties_updates": [
            { "qualified_name": "Demo.OrdersController", "properties": { "cyclomatic": 8, "in_cycle": true, "is_async": true } },
            { "qualified_name": "Demo.Base", "properties": { "fan_out": 4 } }
          ]
        }
        """;

    private static (SemanticEnrichmentStep Step, CannedLlmService Llm) FallbackStep(string answer)
    {
        var llm = new CannedLlmService(answer);
        var role = new LlmRole
        {
            Provider = new LlmProvider { Service = llm, BaseUrl = "http://fake.invalid/" },
            Model = "fake-model",
            Inference = new LlmInferenceSettings { UseStreaming = false }
        };
        var llmStep = new CustomStatelessTemplateLlmStep<EnrichmentBatchInput, LlmStringStepResult>(
            AnalysisPipelineFactory.SemanticEnricherStepName, role, AnalysisPipelineFactory.CreateDefaultTemplateProvider(),
            "enricher_prompt", "enricher_system");
        return (new SemanticEnrichmentStep(llmStep), llm);
    }

    private static async Task<string> PropertiesAsync(string db, string qn)
    {
        await using var conn = new SqliteConnection($"Data Source={db};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT properties FROM nodes WHERE qualified_name = @qn";
        cmd.Parameters.AddWithValue("@qn", qn);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<List<(string Type, string Properties)>> EdgesAsync(string db)
    {
        await using var conn = new SqliteConnection($"Data Source={db};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT type, properties FROM edges ORDER BY type";
        var edges = new List<(string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            edges.Add((reader.GetString(0), reader.GetString(1)));
        return edges;
    }

    private static async Task<long> CountNodesAsync(string db)
    {
        await using var conn = new SqliteConnection($"Data Source={db};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM nodes";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// H-3: el código impone <c>origin</c> y <c>enriched_by</c> (lista) y deja pasar solo propiedades semánticas; las métricas
    /// que propone el modelo se descartan con un aviso, una arista que ya existe no se toca y el grafo no queda marcado
    /// como enriquecido. Una segunda pasada sustituye a la primera en lugar de acumularse.
    /// </summary>
    [Fact]
    public async Task The_llm_fallback_marks_its_output_in_code_and_cannot_write_metrics()
    {
        using var repo = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "OrdersController.cs"), "public class OrdersController : Base { }");
        var input = new AnalysisPipelineInput { RepositoryPath = repo.Path, DatabasePath = Path.Combine(repo.Path, "codebase.db") };
        await CreateGraphAsync(input.DatabasePath, ("Class", "Demo.OrdersController", """{"fan_in":2}"""), ("Class", "Demo.Base", "{}"));
        await using (var conn = new SqliteConnection($"Data Source={input.DatabasePath};Pooling=False"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO edges (project, source_id, target_id, type, properties)
                SELECT 'Codebase', s.id, t.id, 'INHERITS', '{"strategy":"base_classes"}'
                FROM nodes s, nodes t WHERE s.qualified_name = 'Demo.OrdersController' AND t.qualified_name = 'Demo.Base';
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var (step, llm) = FallbackStep(CannedEnrichment);
        var services = new ServiceCollection().AddSingleton(TestConfig.FromPairs()).AddLogging().BuildServiceProvider();
        var reporter = new CapturingReporter();
        var context = AnalysisRunner.CreateContext(input, services, null, reporter);

        var result = await step.ExecuteAsync(new GraphStatusResult { DatabasePath = input.DatabasePath }, context, 1, null, CancellationToken.None);

        Assert.Equal(1, llm.Calls);
        Assert.False(result.IsEnriched);
        Assert.False(await DetectAndIndexGraphStep.CheckIfEnrichedAsync(input.DatabasePath, CancellationToken.None));

        using (var route = JsonDocument.Parse(await PropertiesAsync(input.DatabasePath, "__route__POST__/api/orders")))
        {
            var r = route.RootElement;
            Assert.Equal(SemanticEnrichmentStep.OriginAiEnricher, r.GetProperty("origin").GetString());
            Assert.Equal("[\"SemanticEnricherAgent\"]", r.GetProperty("enriched_by").GetRawText());
            Assert.NotEqual("ayer", r.GetProperty("enriched_at").GetString());
            Assert.Equal(("POST", "/api/orders", true),
                (r.GetProperty("http_verb").GetString(), r.GetProperty("route_path").GetString(), r.GetProperty("auth_required").GetBoolean()));
            Assert.False(r.TryGetProperty("cyclomatic", out _));
            Assert.False(r.TryGetProperty("fan_in", out _));
        }

        // El nodo de cbm se completa: conserva lo suyo, gana lo semántico y la marca, y ninguna métrica del modelo.
        using (var controller = JsonDocument.Parse(await PropertiesAsync(input.DatabasePath, "Demo.OrdersController")))
        {
            var c = controller.RootElement;
            Assert.Equal(2, c.GetProperty("fan_in").GetInt32());
            Assert.Equal("Controlador de pedidos", c.GetProperty("docstring").GetString());
            Assert.True(c.GetProperty("is_async").GetBoolean());
            Assert.Equal("[\"SemanticEnricherAgent\"]", c.GetProperty("enriched_by").GetRawText());
            Assert.False(c.TryGetProperty("origin", out _));
            foreach (var metric in new[] { "loc_total", "afferent_coupling", "cyclomatic", "in_cycle" })
                Assert.False(c.TryGetProperty(metric, out _), metric);
        }
        // Una actualización que solo trae métricas no toca el nodo.
        Assert.Equal("{}", await PropertiesAsync(input.DatabasePath, "Demo.Base"));

        var edges = await EdgesAsync(input.DatabasePath);
        Assert.Equal(
            [("HANDLES", "{\"enriched_by\":[\"SemanticEnricherAgent\"],\"presentational\":true}"), ("INHERITS", "{\"strategy\":\"base_classes\"}")],
            edges);

        var warning = Assert.Single(reporter.Entries, e => e.Message.Contains("se han descartado", StringComparison.Ordinal));
        Assert.EndsWith("afferent_coupling, confidence, cyclomatic, enriched_at, enriched_by, fan_in, fan_out, in_cycle, loc_total, origin.", warning.Message);

        // Segunda pasada: la salida anterior se retira antes de escribir y no se acumula. Este grafo no tiene tabla
        // projects, así que no hay huella de indexación y el fallback se repite (ver el test siguiente).
        await step.ExecuteAsync(new GraphStatusResult { DatabasePath = input.DatabasePath }, context, 1, null, CancellationToken.None);
        Assert.Equal(2, llm.Calls);
        Assert.Equal(edges, await EdgesAsync(input.DatabasePath));
        Assert.Equal(3, await CountNodesAsync(input.DatabasePath));
    }

    /// <summary>
    /// Como P3: el fallback registra su pasada en <c>enrichment_runs</c> y no vuelve a llamar al modelo sobre la misma
    /// indexación de cbm y los mismos ficheros, porque su salida sigue en el grafo. Una reindexación de cbm (otra huella),
    /// otros ficheros o <c>ForceReindex</c> lo repiten.
    /// </summary>
    [Fact]
    public async Task The_llm_fallback_is_not_repeated_on_the_same_indexing()
    {
        using var repo = new TempDirectory();
        var controller = Path.Combine(repo.Path, "OrdersController.cs");
        await File.WriteAllTextAsync(controller, "public class OrdersController : Base { }");
        var input = new AnalysisPipelineInput { RepositoryPath = repo.Path, DatabasePath = Path.Combine(repo.Path, "codebase.db") };
        await CreateGraphAsync(input.DatabasePath, ("Class", "Demo.OrdersController", "{}"), ("Class", "Demo.Base", "{}"));
        async Task SqlAsync(string sql)
        {
            await using var conn = new SqliteConnection($"Data Source={input.DatabasePath};Pooling=False");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
        await SqlAsync("""
            CREATE TABLE projects (name TEXT PRIMARY KEY, indexed_at TEXT NOT NULL, root_path TEXT NOT NULL);
            INSERT INTO projects VALUES ('Codebase', '2026-09-29T10:00:00Z', 'C:/repo');
            """);
        var (step, llm) = FallbackStep(CannedEnrichment);
        var services = new ServiceCollection().AddSingleton(TestConfig.FromPairs()).AddLogging().BuildServiceProvider();
        var reporter = new CapturingReporter();
        var context = AnalysisRunner.CreateContext(input, services, null, reporter);
        Task RunAsync() => step.ExecuteAsync(new GraphStatusResult { DatabasePath = input.DatabasePath }, context, 1, null, CancellationToken.None);

        await RunAsync();
        Assert.Equal(1, llm.Calls);
        await using (var conn = new SqliteConnection($"Data Source={input.DatabasePath};Pooling=False"))
        {
            await conn.OpenAsync();
            var run = Assert.Single(await CodebaseAnalysis.Core.Enrichment.EnrichmentRuns.ListAsync(conn, CancellationToken.None, SemanticEnrichmentStep.EnricherName));
            Assert.Equal((CodebaseAnalysis.Core.Enrichment.EnrichmentRuns.StatusOk, SemanticEnrichmentStep.EnricherVersion, "Codebase|2026-09-29T10:00:00Z"),
                (run.Status, run.EnricherVersion, run.IndexFingerprint));
            // El nodo de cbm se completa dos veces (nodes y node_properties_updates) y, sin la INHERITS de cbm, entran las dos aristas.
            Assert.Equal((1, 1, 2, 2), (run.Outcome!.FilesAnalyzed, run.Outcome.NodesCreated, run.Outcome.NodesUpdated, run.Outcome.EdgesCreated));
        }

        // Misma indexación y mismos ficheros: no hay llamada y la salida de la pasada anterior sigue en el grafo.
        await RunAsync();
        Assert.Equal(1, llm.Calls);
        Assert.Contains(reporter.Entries, e => e.Message.StartsWith("ℹ️ Enriquecimiento semántico (LLM): el grafo ya tiene la pasada", StringComparison.Ordinal));
        Assert.Contains(SemanticEnrichmentStep.OriginAiEnricher, await PropertiesAsync(input.DatabasePath, "__route__POST__/api/orders"));

        // cbm vuelve a indexar: otra huella.
        await SqlAsync("UPDATE projects SET indexed_at = '2026-09-30T08:00:00Z'");
        await RunAsync();
        Assert.Equal(2, llm.Calls);

        // Otros ficheros que enviar.
        await File.WriteAllTextAsync(controller, "public class OrdersController : Base { void Post() { } }");
        await RunAsync();
        Assert.Equal(3, llm.Calls);

        // ForceReindex lo repite aunque nada haya cambiado.
        input.ForceReindex = true;
        await RunAsync();
        Assert.Equal(4, llm.Calls);
        Assert.Equal(3, await CountNodesAsync(input.DatabasePath));
    }

    [Fact]
    public async Task Without_key_files_the_semantic_enricher_does_not_call_the_model()
    {
        using var repo = new TempDirectory();
        await File.WriteAllBytesAsync(Path.Combine(repo.Path, "Datos.docx"), SampleFiles.Docx());
        var input = new AnalysisPipelineInput { RepositoryPath = repo.Path, DatabasePath = Path.Combine(repo.Path, "codebase.db") };
        var llm = new CannedLlmService("{}");
        var role = new LlmRole
        {
            Provider = new LlmProvider { Service = llm, BaseUrl = "http://fake.invalid/" },
            Model = "fake-model",
            Inference = new LlmInferenceSettings { UseStreaming = false }
        };
        var llmStep = new CustomStatelessTemplateLlmStep<EnrichmentBatchInput, LlmStringStepResult>(
            AnalysisPipelineFactory.SemanticEnricherStepName, role, AnalysisPipelineFactory.CreateDefaultTemplateProvider(),
            "enricher_prompt", "enricher_system");
        var services = new ServiceCollection().AddSingleton(TestConfig.FromPairs()).AddLogging().BuildServiceProvider();
        var reporter = new CapturingReporter();
        var context = AnalysisRunner.CreateContext(input, services, null, reporter);
        var graph = new GraphStatusResult { DatabasePath = input.DatabasePath, IsEnriched = false };

        var result = await new SemanticEnrichmentStep(llmStep).ExecuteAsync(graph, context, 1, null, CancellationToken.None);

        Assert.Equal(0, llm.Calls);
        Assert.False(result.IsEnriched);
        Assert.Contains(reporter.Entries, e => e.Message.StartsWith("ℹ️ No hay ficheros de rutas, controladores ni DI", StringComparison.Ordinal));
    }
}
