using AITaskAgent.Configuration;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Tools.Implementation;
using AITaskAgent.Observability;
using CodebaseAnalysis.Core.Chat;
using CodebaseAnalysis.Core.Documents;
using CodebaseAnalysis.Core.Documents.Extraction;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Knowledge.Embeddings;
using CodebaseAnalysis.Core.Knowledge.Graph;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tools;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>Correcciones posteriores a la auditoría de V2 (plan v2, «Correcciones post-auditoría V2»), en Core.</summary>
[Collection(ConsoleCollection.Name)]
public sealed class PostAuditV2CoreTests
{
    private const string CannedReport = "# Informe\n\n## Resumen\n\nContenido del servicio falso.\n\n<!-- analysis-meta: health=Green -->\n";

    private static async Task CreateGraphAsync(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        await GraphSchema.EnsureAsync(dbPath, CancellationToken.None);
        await using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO nodes (project, label, name, qualified_name, file_path, start_line, end_line, properties)
            VALUES ('Codebase', 'Class', 'Sample', 'Demo.Sample', 'src/Sample.cs', 1, 10, '{"cyclomatic":3}');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static Task<DocumentRecord> Upload(DocumentStore store, string name, byte[] bytes) =>
        store.UploadAsync(new MemoryStream(bytes), Uri.EscapeDataString(name), CancellationToken.None);

    // ── Punto 1: el plazo de extracción corta aunque el extractor no mire el token ─────────────────────────────

    [Fact]
    public async Task A_slow_extractor_that_ignores_the_token_leaves_the_document_failed_and_releases_the_gate()
    {
        using var dir = new TempDirectory();
        var paths = new KnowledgePaths(dir.Path, Path.Combine(dir.Path, "reports"));
        using var release = new ManualResetEventSlim();
        var slow = new DocumentStore(paths)
        {
            ExtractorOverride = (_, _) =>
            {
                release.Wait(TimeSpan.FromSeconds(30)); // no mira el token, como PdfPig dentro de una página enorme
                return ExtractionResult.Failed("no debería llegar");
            },
            ExtractionTimeout = TimeSpan.FromMilliseconds(200)
        };

        var watch = Stopwatch.StartNew();
        var record = await Upload(slow, "lento.md", SampleFiles.Utf8("# Lento\n\nTexto."));
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"La subida tardó {watch.Elapsed}.");
        Assert.Equal(ExtractionStatuses.Failed, record.Extraction.Status);
        Assert.Contains(record.Extraction.Warnings!, w => w.Contains("plazo"));
        Assert.Equal(SampleFiles.Utf8("# Lento\n\nTexto."), File.ReadAllBytes(slow.OriginalPath(record.Id)));

        // El WriteGate de la subida quedó libre: la siguiente sube sin esperar al extractor colgado.
        var next = await Upload(new DocumentStore(paths), "rapido.md", SampleFiles.Utf8("# Rápido"));
        Assert.Equal(ExtractionStatuses.Ok, next.Extraction.Status);
        release.Set();
    }

    // ── Punto 2: grafo y embeddings vuelven a comprobar dentro de WriteGate que la fuente existe ──────────────

    [Fact]
    public async Task Indexing_a_source_deleted_during_the_network_call_writes_no_embeddings()
    {
        using var dir = new TempDirectory();
        var paths = new KnowledgePaths(dir.Path, Path.Combine(dir.Path, "reports"));
        var store = new DocumentStore(paths);
        var doc = await Upload(store, "guia.md", SampleFiles.Markdown());
        var source = SourceRef.Document(doc.Id);
        var service = new KnowledgeEmbeddingService(paths, new EmbeddingOptions { Model = "fake", Dimensions = 4 },
            new DeletingClient(() => store.Delete(doc.Id)));

        var error = await Assert.ThrowsAsync<KnowledgeOperationException>(() => service.IndexAsync(source, CancellationToken.None));

        Assert.Equal(KnowledgeErrorCodes.NotFound, error.Code);
        Assert.Null(service.Store.GetSource(source.Key));
    }

    [Fact]
    public async Task Adding_to_the_graph_reads_the_source_inside_the_write_gate()
    {
        using var dir = new TempDirectory();
        var paths = new KnowledgePaths(dir.Path, Path.Combine(dir.Path, "reports"));
        var db = Path.Combine(dir.Path, ".codebase", "codebase.db");
        await CreateGraphAsync(db);
        var store = new DocumentStore(paths);
        var doc = await Upload(store, "guia.md", SampleFiles.Markdown());
        var source = SourceRef.Document(doc.Id);

        Task add;
        await KnowledgeGraphIngestor.WriteGate.WaitAsync();
        try
        {
            add = KnowledgeGraphIngestor.AddAsync(db, paths, source, CancellationToken.None);
            await Task.Delay(100);
            store.Delete(doc.Id); // la baja llega mientras el alta espera el cerrojo
        }
        finally
        {
            KnowledgeGraphIngestor.WriteGate.Release();
        }

        var error = await Assert.ThrowsAsync<KnowledgeOperationException>(() => add);
        Assert.Equal(KnowledgeErrorCodes.NotFound, error.Code);
        Assert.Equal(GraphKnowledgeStates.Absent, (await KnowledgeGraphIngestor.GetStateAsync(db, paths, source, CancellationToken.None)).State);
    }

    // ── Punto 3: un slug con historial huérfano no se reutiliza ──────────────────────────────────────────────

    [Fact]
    public void A_custom_name_with_orphan_history_is_not_reused()
    {
        using var dir = new TempDirectory();
        var paths = new KnowledgePaths(dir.Path, Path.Combine(dir.Path, "reports"));
        var created = new ReportWriter(paths).Create("Estudio de colas", "# Estudio de colas\n\nTexto.", null, ReportVersionOrigin.Ui);
        new ReportWriter(paths).DeleteCustom(created.Source, ReportVersionOrigin.Ui);
        Assert.True(Directory.Exists(paths.ReportHistoryFolder(SourceRef.Custom("estudio-de-colas.md"))));

        Assert.Equal("estudio-de-colas-2.md", ReportLibrary.NewCustomFileName(paths, "Estudio de colas"));
        var again = new ReportWriter(paths).Create("Estudio de colas", "# Estudio de colas\n\nOtro.", null, ReportVersionOrigin.Ui);
        Assert.Equal("estudio-de-colas-2.md", again.FileName);
        Assert.Single(new ReportHistoryStore(paths).List(again.Source));
    }

    // ── Punto 5: XLSX por SAX con el tope de filas ────────────────────────────────────────────────────────────

    [Fact]
    public void A_large_sheet_is_read_row_by_row_up_to_the_row_cap()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "grande.xlsx");
        const int rows = 20_000;
        using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook();
            var shared = workbook.AddNewPart<SharedStringTablePart>();
            shared.SharedStringTable = new SharedStringTable(
                new SharedStringItem(new Text("Nombre")), new SharedStringItem(new Text("Importe")), new SharedStringItem(new Text("compartida")));
            var sheetPart = workbook.AddNewPart<WorksheetPart>();
            using (var writer = OpenXmlWriter.Create(sheetPart))
            {
                writer.WriteStartElement(new Worksheet());
                writer.WriteStartElement(new SheetData());
                writer.WriteElement(new Row(
                    new Cell { CellReference = "A1", DataType = CellValues.SharedString, CellValue = new CellValue("0") },
                    new Cell { CellReference = "B1", DataType = CellValues.SharedString, CellValue = new CellValue("1") }));
                for (var r = 2; r <= rows; r++)
                {
                    writer.WriteElement(new Row(
                        new Cell { CellReference = $"A{r}", DataType = CellValues.InlineString, InlineString = new InlineString(new Text($"valor-{r:D5}")) },
                        new Cell { CellReference = $"B{r}", DataType = CellValues.SharedString, CellValue = new CellValue("2") }));
                }
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            workbook.Workbook.AppendChild(new Sheets(new Sheet { Id = workbook.GetIdOfPart(sheetPart), SheetId = 1, Name = "Datos" }));
        }

        var result = OfficeExtractors.Xlsx(path, "grande", CancellationToken.None);

        Assert.Equal(ExtractionStatuses.Ok, result.Status);
        Assert.Contains("| Nombre | Importe |", result.Content);
        Assert.Contains("| valor-00002 | compartida |", result.Content);
        Assert.Contains($"valor-{OfficeExtractors.MaxSheetRows + 1:D5}", result.Content);
        Assert.DoesNotContain($"valor-{OfficeExtractors.MaxSheetRows + 2:D5}", result.Content);
        Assert.Contains(result.Warnings, w => w.Contains("más de 1000 filas"));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => OfficeExtractors.Xlsx(path, "grande", cancelled.Token));
    }

    // ── Punto 6: report.changed y artefacto aunque falle la reingesta ─────────────────────────────────────────

    [Fact]
    public async Task A_report_write_whose_graph_refresh_fails_still_emits_report_changed_and_warns()
    {
        using var dir = new TempDirectory();
        var db = Path.Combine(dir.Path, ".codebase", "codebase.db");
        var ws = new ChatWorkspace(dir.Path, db, Path.Combine(dir.Path, "reports"));
        Directory.CreateDirectory(ws.ReportsPath);
        File.WriteAllText(Path.Combine(ws.ReportsPath, "05_errores_y_bugs.md"), "# Errores\n\nViejo.\n\n<!-- analysis-meta: health=Red -->\n");
        await CreateGraphAsync(db);
        var report = SourceRef.Report("05_errores_y_bugs.md");
        await KnowledgeGraphIngestor.AddAsync(db, ws.Knowledge, report, CancellationToken.None);

        // Grafo de solo lectura: la reingesta que sigue a la escritura falla («attempt to write a readonly database»).
        SqliteConnection.ClearAllPools();
        File.SetAttributes(db, FileAttributes.ReadOnly);
        try
        {
            AvailableTools.Instance.Register(new UpdateReportTool());
            var store = new ChatSessionStore(Path.Combine(dir.Path, ".codebase", "chat"));
            var session = store.Create(ws.RepositoryPath);
            await using var services = ChatTestHost.BuildServices();
            var llm = new ToolScriptLlmService(("update_report", JsonSerializer.Serialize(new
            {
                file_name = "05_errores_y_bugs.md", mode = "replace", content = "# Errores\n\nNuevo.", note = "Reescrito"
            })));
            var agent = new ChatAgent(services, ChatTestHost.StreamingRole(llm), AnalysisPipelineFactory.CreateDefaultTemplateProvider(), new ChatOptions());

            var events = new List<ChatStreamEvent>();
            var turn = await agent.RunTurnAsync(store, session, "Reescribe el 05", ws, events.Add, CancellationToken.None);

            Assert.Equal(ChatTurnStatus.Completed, turn.Status);
            Assert.True(Assert.Single(turn.ToolCalls).Success);
            var changed = Assert.Single(events, e => e.Type == "report.changed");
            Assert.Equal(false, changed.Fields["graphRefreshed"]);
            var artifact = Assert.Single(turn.Artifacts!);
            Assert.Equal(("05_errores_y_bugs.md", "updated"), (artifact.Id, artifact.Action));
            Assert.Contains("Aviso", llm.ToolResults[0]);
            Assert.Contains("Nuevo.", File.ReadAllText(Path.Combine(ws.ReportsPath, "05_errores_y_bugs.md")));
        }
        finally
        {
            File.SetAttributes(db, FileAttributes.Normal);
        }
    }

    // ── Punto 9: describe_image sin pregunta cuenta como escritura ────────────────────────────────────────────

    [Fact]
    public async Task Describe_image_without_question_consumes_the_write_budget_and_with_question_does_not()
    {
        using var dir = new TempDirectory();
        var paths = new KnowledgePaths(dir.Path, Path.Combine(dir.Path, "reports"));
        var image = await Upload(new DocumentStore(paths), "dos-colores.png", SampleFiles.Png());
        var llm = new FixedLlmService("Mitad roja y mitad azul.");
        var describer = new ImageDescriber(new AITaskAgent.LLM.Configuration.LlmRole
        {
            Provider = new AITaskAgent.LLM.Configuration.LlmProvider { Service = llm, BaseUrl = "http://fake.invalid/", ApiKey = "k" },
            Model = "vision-model"
        }, AnalysisPipelineFactory.CreateDefaultTemplateProvider());

        var scope = new ChatTurnScope(paths, "s", "t-0001", maxWrites: 1);
        var context = new PipelineContext();
        context.SetMetadata(AnalysisContextKeys.ChatTurn, scope);
        context.SetMetadata(AnalysisContextKeys.ChatKnowledgeServices, new ChatKnowledgeServices { ImageDescriber = describer });
        var tool = new DescribeImageTool(paths);
        Task<string> Run(object args) => tool.ExecuteAsync(JsonSerializer.Serialize(args), context, "chat", NullLogger.Instance);

        Assert.Contains("Descripción guardada", await Run(new { document_id = image.Id }));
        Assert.Contains("Respuesta de vision-model", await Run(new { document_id = image.Id, question = "¿Qué colores hay?" }));
        var exhausted = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new { document_id = image.Id }));
        Assert.Contains("Límite de 1 escrituras", exhausted.Message);
        Assert.Equal(2, llm.Calls);

        var outside = new PipelineContext();
        outside.SetMetadata(AnalysisContextKeys.ChatKnowledgeServices, new ChatKnowledgeServices { ImageDescriber = describer });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tool.ExecuteAsync(JsonSerializer.Serialize(new { document_id = image.Id }), outside, "chat", NullLogger.Instance));
    }

    // ── Punto 10: los informes del catálogo vuelven al grafo aunque el run no llegue a la síntesis ────────────

    [Fact]
    public async Task Catalog_reports_retired_by_detection_come_back_when_the_run_is_cancelled_before_synthesis()
    {
        using var repo = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(repo.Path, "src"));
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "src", "Sample.cs"), "namespace Demo; public class Sample { }");
        using var cts = new CancellationTokenSource();
        var (configuration, serviceProvider) = await OfflineHostAsync(new CancellingLlmService(cts), ("reports", "errores"));
        await using var _ = serviceProvider;

        var roles = LlmRoleFactory.BuildRoles(configuration, serviceProvider, _ => null, _ => { });
        var input = AnalysisRunOptions.BuildInput(configuration, repo.Path, _ => { });
        await CreateGraphAsync(input.DatabasePath);
        var paths = new KnowledgePaths(input.RepositoryPath, input.OutputReportsPath);
        File.WriteAllText(Path.Combine(paths.ReportsPath, "05_errores_y_bugs.md"), "# Viejo\n\n<!-- analysis-meta: health=Red -->\n");
        var report = SourceRef.Report("05_errores_y_bugs.md");
        await KnowledgeGraphIngestor.AddAsync(input.DatabasePath, paths, report, CancellationToken.None);

        AnalysisToolRegistration.Register(input);
        var reporter = new CapturingReporter();
        try
        {
            var result = await AnalysisRunner.RunAsync(input, roles, serviceProvider, new EventChannel(), reporter, cts.Token);
            Assert.True(result.HasError);
        }
        catch (OperationCanceledException)
        {
        }

        Assert.True(cts.IsCancellationRequested);
        Assert.DoesNotContain(reporter.Entries, e => e.Message.StartsWith("🎉"));
        Assert.Contains(reporter.Entries, e => e.Message == "🧩 Reincorporados al grafo 1 informes del análisis sin llegar a la síntesis.");
        Assert.Equal(GraphKnowledgeStates.Present, (await KnowledgeGraphIngestor.GetStateAsync(input.DatabasePath, paths, report, CancellationToken.None)).State);
    }

    // ── Punto 11: SHA por (ruta, tamaño, fecha) y estados en lote ─────────────────────────────────────────────

    [Fact]
    public async Task File_sha_cache_follows_writes_and_batch_states_match_single_ones()
    {
        using var dir = new TempDirectory();
        var file = Path.Combine(dir.Path, "a.md");
        File.WriteAllText(file, "uno");
        var first = FileShaCache.Get(file);
        Assert.Equal(ReportHistoryStore.Sha256("uno"), first);
        Assert.Equal(first, FileShaCache.Get(file));

        File.WriteAllText(file, "dos"); // mismo tamaño, otra fecha
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddSeconds(5));
        Assert.Equal(ReportHistoryStore.Sha256("dos"), FileShaCache.Get(file));
        File.Delete(file);
        Assert.Null(FileShaCache.Get(file));

        var paths = new KnowledgePaths(dir.Path, Path.Combine(dir.Path, "reports"));
        var db = Path.Combine(dir.Path, ".codebase", "codebase.db");
        await CreateGraphAsync(db);
        var store = new DocumentStore(paths);
        var docs = new List<SourceRef>();
        foreach (var name in new[] { "a.md", "b.md", "c.md" })
            docs.Add(SourceRef.Document((await Upload(store, name, SampleFiles.Utf8($"# {name}\n\nLa clase `Sample`."))).Id));
        await KnowledgeGraphIngestor.AddAsync(db, paths, docs[0], CancellationToken.None);
        new GraphSourceRegistry(paths).Add(docs[1]);

        var many = await KnowledgeStateReader.GetManyAsync(paths, db, null, docs, CancellationToken.None);
        for (var i = 0; i < docs.Count; i++)
            Assert.Equal(await KnowledgeStateReader.GetAsync(paths, db, null, docs[i], CancellationToken.None), many[i]);
        Assert.Equal([GraphKnowledgeStates.Present, GraphKnowledgeStates.Pending, GraphKnowledgeStates.Absent], many.Select(s => s.Graph.State));
    }

    // ── Punto 12: PATCH y SaveDescription bajo el mismo cerrojo ───────────────────────────────────────────────

    [Fact]
    public async Task Concurrent_patch_and_description_do_not_overwrite_each_other()
    {
        using var dir = new TempDirectory();
        var paths = new KnowledgePaths(dir.Path, Path.Combine(dir.Path, "reports"));
        var store = new DocumentStore(paths);
        var image = await Upload(store, "dos-colores.png", SampleFiles.Png());

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() =>
        {
            if (i % 2 == 0)
                store.Patch(image.Id, null, false);
            else
                store.SaveDescription(image.Id, $"Descripción {i}", "gpt-5.4");
        })));

        var record = store.TryGet(image.Id)!;
        Assert.False(record.UseInAnalysis);
        Assert.NotNull(record.Description);
        Assert.Equal(ExtractionStatuses.Ok, record.Extraction.Status);
    }

    // ── Punto 14: tope de iteraciones de los auditores y última llamada sin tools ────────────────────────────

    [Fact]
    public void Auditor_tool_iterations_come_from_configuration_by_role()
    {
        var warnings = new List<string>();
        var configuration = TestConfig.FromPairs(
            ("CodebaseAnalysis:Auditors:MaxToolIterations", "7"),
            ("CodebaseAnalysis:Auditors:MaxToolIterationsByRole:FlowsAuditor", "16"),
            ("CodebaseAnalysis:Auditors:MaxToolIterationsByRole:DiAuditor", "0"));
        using var repo = new TempDirectory();
        var input = AnalysisRunOptions.BuildInput(configuration, repo.Path, warnings.Add);

        Assert.Equal(16, input.GetAuditorMaxToolIterations("FlowsAuditor"));
        Assert.Equal(16, input.GetAuditorMaxToolIterations("flowsauditor"));
        Assert.Equal(7, input.GetAuditorMaxToolIterations("DiAuditor"));
        Assert.Contains(warnings, w => w.Contains("DiAuditor"));

        var defaults = AnalysisRunOptions.BuildInput(TestConfig.FromPairs(), repo.Path, _ => { });
        Assert.Equal(AnalysisPipelineInput.DefaultAuditorMaxToolIterations, defaults.GetAuditorMaxToolIterations("FlowsAuditor"));
    }

    [Fact]
    public async Task An_auditor_that_exhausts_its_tool_iterations_writes_the_report_with_a_last_call_without_tools()
    {
        using var repo = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(repo.Path, "src"));
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "src", "Sample.cs"), "namespace Demo; public class Sample { }");
        var llm = new ToolLoopingLlmService(CannedReport);
        var (configuration, serviceProvider) = await OfflineHostAsync(llm,
            ("reports", "flujos"),
            ("CodebaseAnalysis:Auditors:MaxToolIterations", "2"),
            ("CodebaseAnalysis:Auditors:MaxToolIterationsByRole:FlowsAuditor", "3"));
        await using var _ = serviceProvider;

        var roles = LlmRoleFactory.BuildRoles(configuration, serviceProvider, _ => null, _ => { });
        var input = AnalysisRunOptions.BuildInput(configuration, repo.Path, _ => { });
        await CreateGraphAsync(input.DatabasePath);
        AnalysisToolRegistration.Register(input);
        var reporter = new CapturingReporter();

        var result = await AnalysisRunner.RunAsync(input, roles, serviceProvider, new EventChannel(), reporter, CancellationToken.None);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(3, llm.ToolRequests); // el tope del rol, no el general
        var final = Assert.Single(llm.FinalRequests, r => r.LastUserMessage == AnalysisPipelineFactory.AuditorToolLimitPrompt);
        Assert.True(final.ToolResultCount >= 3);
        Assert.DoesNotContain(reporter.Entries, e => e.Message.Contains("Maximum tool iterations"));
        Assert.Equal(CannedReport, ProvenanceKeys.Strip(File.ReadAllText(Path.Combine(input.OutputReportsPath, "04_flujos_end_to_end.md"))));
    }

    [Fact]
    public async Task An_auditor_that_keeps_asking_for_tools_in_its_final_attempt_fails_once_instead_of_looping()
    {
        using var repo = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(repo.Path, "src"));
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "src", "Sample.cs"), "namespace Demo; public class Sample { }");
        var llm = new AlwaysToolsLlmService();
        var (configuration, serviceProvider) = await OfflineHostAsync(llm,
            ("reports", "flujos"),
            ("CodebaseAnalysis:Auditors:MaxToolIterationsByRole:FlowsAuditor", "3"));
        await using var _ = serviceProvider;

        var roles = LlmRoleFactory.BuildRoles(configuration, serviceProvider, _ => null, _ => { });
        var input = AnalysisRunOptions.BuildInput(configuration, repo.Path, _ => { });
        await CreateGraphAsync(input.DatabasePath);
        AnalysisToolRegistration.Register(input);

        await AnalysisRunner.RunAsync(input, roles, serviceProvider, new EventChannel(), new CapturingReporter(), CancellationToken.None);

        // Una ronda con tools y una sin ellas, de 3 llamadas cada una: el intento final no abre otro.
        Assert.Equal(6, llm.Calls);
        Assert.Equal(3, llm.CallsWithoutTools);
        Assert.False(File.Exists(Path.Combine(input.OutputReportsPath, "04_flujos_end_to_end.md")));
    }

    private static async Task<(IConfiguration, ServiceProvider)> OfflineHostAsync(ILlmService llm, params (string, string?)[] extra)
    {
        IConfiguration configuration = TestConfig.FromPairs(
            [("LlmConfiguration:DefaultProvider", "Fake"),
             ("LlmConfiguration:Providers:Fake:ServiceType", "fake"),
             ("LlmConfiguration:Providers:Fake:BaseUrl", "http://fake.invalid/"),
             ("LlmConfiguration:Providers:Fake:Model", "fake-model"),
             .. extra]);
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddKeyedSingleton("fake", llm);
        services.AddAITaskAgent();
        var provider = services.BuildServiceProvider();
        provider.UseAITaskAgent();
        await Task.CompletedTask;
        return (configuration, provider);
    }

    private sealed class DeletingClient(Action onEmbed) : IEmbeddingClient
    {
        public string Model => "fake";

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
        {
            onEmbed();
            return Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(_ => new float[] { 1, 0, 0, 0 }).ToList());
        }
    }

    private abstract class BatchOnlyLlmService : ILlmService
    {
        public abstract string Name { get; }
        public abstract Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default);
        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public int EstimateTokenCount(string text) => text.Length / 4;
        public int GetMaxContextTokens(string? model = null) => 1_000_000;
    }

    private sealed class FixedLlmService(string answer) : BatchOnlyLlmService
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public override string Name => "fixed";

        public override Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(LlmResult.Success([new LlmResponse { Content = answer, FinishReason = FinishReason.Stop }], model: request.Model));
        }
    }

    /// <summary>La primera llamada cancela el run, como el botón «Cancelar» mientras audita.</summary>
    private sealed class CancellingLlmService(CancellationTokenSource cts) : BatchOnlyLlmService
    {
        public override string Name => "cancelling";

        public override Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            cts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(LlmResult.Success([new LlmResponse { Content = CannedReport, FinishReason = FinishReason.Stop }], model: request.Model));
        }
    }

    /// <summary>Con tools, pide siempre otra búsqueda distinta; sin tools, responde el informe.</summary>
    /// <summary>Pide tools siempre, se le ofrezcan o no: el caso de un modelo que desobedece el intento final.</summary>
    private sealed class AlwaysToolsLlmService : BatchOnlyLlmService
    {
        private int _calls;
        private int _callsWithoutTools;

        public int Calls => Volatile.Read(ref _calls);
        public int CallsWithoutTools => Volatile.Read(ref _callsWithoutTools);

        public override string Name => "always-tools";

        public override Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            var n = Interlocked.Increment(ref _calls);
            if (request.Tools is not { Count: > 0 })
            {
                Interlocked.Increment(ref _callsWithoutTools);
            }
            return Task.FromResult(LlmResult.Success([new LlmResponse
            {
                Content = string.Empty,
                FinishReason = FinishReason.ToolCalls,
                ToolCalls = [new ToolCall { Id = $"call_{n}", Name = "graph_search", Arguments = JsonSerializer.Serialize(new { query = $"Sample{n}" }) }]
            }], model: request.Model));
        }
    }

    private sealed class ToolLoopingLlmService(string report) : BatchOnlyLlmService
    {
        public sealed record FinalRequest(string? LastUserMessage, int ToolResultCount);

        private readonly Lock _lock = new();
        private readonly List<FinalRequest> _final = [];
        private int _toolRequests;

        public int ToolRequests => Volatile.Read(ref _toolRequests);

        public IReadOnlyList<FinalRequest> FinalRequests
        {
            get
            {
                lock (_lock)
                {
                    return [.. _final];
                }
            }
        }

        public override string Name => "tool-looping";

        public override Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Tools?.Any(t => t.Name == "graph_search") == true)
            {
                var n = Interlocked.Increment(ref _toolRequests);
                return Task.FromResult(LlmResult.Success([new LlmResponse
                {
                    Content = string.Empty,
                    FinishReason = FinishReason.ToolCalls,
                    ToolCalls = [new ToolCall { Id = $"call_{n}", Name = "graph_search", Arguments = JsonSerializer.Serialize(new { query = $"Sample{n}" }) }]
                }], model: request.Model));
            }

            var messages = request.Conversation.History.Messages;
            lock (_lock)
            {
                _final.Add(new FinalRequest(
                    messages.LastOrDefault(m => m.Role == LlmConstants.MessageRoles.User)?.Content,
                    messages.Count(m => m.Role == LlmConstants.MessageRoles.Tool)));
            }
            return Task.FromResult(LlmResult.Success([new LlmResponse { Content = report, FinishReason = FinishReason.Stop }], model: request.Model));
        }
    }
}
