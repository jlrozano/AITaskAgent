using AITaskAgent.Core.Models;
using CodebaseAnalysis.Core.Chat;
using CodebaseAnalysis.Core.Documents;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Knowledge.Embeddings;
using CodebaseAnalysis.Core.Knowledge.Graph;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>Cliente de embeddings determinista: bolsa de palabras con hash a <see cref="Dimensions"/> dimensiones.</summary>
internal sealed class HashingEmbeddingClient(string model = "fake-embeddings", int dimensions = 64) : IEmbeddingClient
{
    public int Calls { get; private set; }
    public string Model { get; } = model;
    public int Dimensions { get; } = dimensions;

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(Vector).ToList());
    }

    public float[] Vector(string text)
    {
        var v = new float[Dimensions];
        foreach (var word in text.ToLowerInvariant().Split([' ', '\n', '.', ',', '›', ':', '#', '|', '-'], StringSplitOptions.RemoveEmptyEntries))
            v[(int)((uint)word.GetHashCode(StringComparison.Ordinal) % Dimensions)] += 1;
        return v;
    }
}

/// <summary>V2-D (plan v2 §8): grafo de conocimiento, reincorporación, facetas, embeddings y <c>semantic_search</c>.</summary>
[Collection(ConsoleCollection.Name)]
public sealed class KnowledgeTests
{
    private const string DocText = "# Diseño\n\n## Uso\n\nLa clase `ClaseX` se usa en src/ClaseX.cs:12 y `Duplicado` tiene cuatro candidatos.\n\n## Pagos\n\nEl CreateOrderFlow cobra con tarjeta.\n";

    private static async Task<string> CreateGraphAsync(string dir)
    {
        var db = Path.Combine(dir, ".codebase", "codebase.db");
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        await GraphSchema.EnsureAsync(db, CancellationToken.None);
        await using var conn = new SqliteConnection($"Data Source={db};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO nodes (project, label, name, qualified_name, file_path, start_line, end_line, properties) VALUES
              ('Codebase', 'Class', 'ClaseX', 'Demo.ClaseX', 'src/ClaseX.cs', 1, 40, '{"cyclomatic":3}'),
              ('Codebase', 'Method', 'Run', 'Demo.ClaseX.Run', 'src/ClaseX.cs', 10, 20, '{}'),
              ('Codebase', 'Class', 'Duplicado', 'A.Duplicado', 'a/Duplicado.cs', 1, 5, '{}'),
              ('Codebase', 'Class', 'Duplicado', 'B.Duplicado', 'b/Duplicado.cs', 1, 5, '{}'),
              ('Codebase', 'Class', 'Duplicado', 'C.Duplicado', 'c/Duplicado.cs', 1, 5, '{}'),
              ('Codebase', 'Class', 'Duplicado', 'D.Duplicado', 'd/Duplicado.cs', 1, 5, '{}');
            """;
        await cmd.ExecuteNonQueryAsync();
        return db;
    }

    private static async Task<long> ScalarAsync(string db, string sql)
    {
        await using var conn = new SqliteConnection($"Data Source={db};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<(KnowledgePaths Paths, DocumentRecord Doc)> WithDocumentAsync(TempDirectory dir, string text = DocText)
    {
        var paths = new KnowledgePaths(dir.Path, Path.Combine(dir.Path, "reports"));
        var doc = await new DocumentStore(paths).UploadAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), "diseno.md", CancellationToken.None);
        return (paths, doc);
    }

    [Fact]
    public async Task Adding_twice_is_idempotent_mentions_resolve_and_ambiguity_over_three_links_nothing()
    {
        using var dir = new TempDirectory();
        var db = await CreateGraphAsync(dir.Path);
        var (paths, doc) = await WithDocumentAsync(dir);
        var source = SourceRef.Document(doc.Id);

        var first = await KnowledgeGraphIngestor.AddAsync(db, paths, source, CancellationToken.None);
        var second = await KnowledgeGraphIngestor.AddAsync(db, paths, source, CancellationToken.None);

        Assert.Equal(GraphKnowledgeStates.Present, second.State);
        Assert.Equal((first.NodeCount, first.EdgeCount), (second.NodeCount, second.EdgeCount));
        Assert.Equal(4, second.NodeCount); // documento y tres secciones (Diseño, Uso, Pagos)
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(1) FROM nodes WHERE label = 'UserDocument'"));

        var targets = new List<string>();
        await using (var conn = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT t.qualified_name, json_extract(e.properties, '$.mention_type') FROM edges e JOIN nodes t ON t.id = e.target_id WHERE e.type = 'DOC_DESCRIBES' ORDER BY 1";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                targets.Add($"{reader.GetString(0)}:{reader.GetString(1)}");
        }
        Assert.Equal(["Demo.ClaseX:code_span", "Demo.ClaseX.Run:path"], targets);
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(1) FROM nodes WHERE label LIKE 'UserDoc%' AND file_path <> ''"));
        Assert.Contains(new GraphSourceRegistry(paths).Sources(), s => s == source);

        // Cambiar el contenido la deja desactualizada; quitar deja cero nodos userdoc.
        new DocumentStore(paths).SaveDescription(doc.Id, "otro texto", "m");
        Assert.Equal(GraphKnowledgeStates.Stale, (await KnowledgeGraphIngestor.GetStateAsync(db, paths, source, CancellationToken.None)).State);

        var removed = await KnowledgeGraphIngestor.RemoveAsync(db, paths, source, CancellationToken.None);
        Assert.Equal(GraphKnowledgeStates.Absent, removed.State);
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(1) FROM nodes WHERE qualified_name LIKE 'userdoc.%'"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(1) FROM edges WHERE type IN ('DOC_DESCRIBES', 'HAS_SECTION')"));
    }

    [Fact]
    public async Task Without_graph_adding_is_no_graph_and_a_registered_source_without_nodes_is_pending()
    {
        using var dir = new TempDirectory();
        var (paths, doc) = await WithDocumentAsync(dir);
        var db = Path.Combine(dir.Path, ".codebase", "codebase.db");
        var source = SourceRef.Document(doc.Id);

        var ex = await Assert.ThrowsAsync<KnowledgeOperationException>(() => KnowledgeGraphIngestor.AddAsync(db, paths, source, CancellationToken.None));
        Assert.Equal("no_graph", ex.Code);

        new GraphSourceRegistry(paths).Add(source);
        Assert.Equal(GraphKnowledgeStates.Pending, (await KnowledgeGraphIngestor.GetStateAsync(db, paths, source, CancellationToken.None)).State);
    }

    [Fact]
    public void Mention_resolver_handles_wiki_links_qualified_names_identifiers_and_code_blocks()
    {
        var resolver = new MentionResolver(
        [
            new CodeNode(1, "Class", "OrderService", "Shop.OrderService", "src/Shop/OrderService.cs", 1, 50),
            new CodeNode(2, "Method", "Place", "Shop.OrderService.Place", "src/Shop/OrderService.cs", 5, 9),
            new CodeNode(3, "Function", "calc_total_amount", "billing.calc_total_amount", "billing.py", 1, 3),
            new CodeNode(4, "Class", "Helper", "X.Helper", "x/Helper.cs", 1, 2),
            new CodeNode(5, "Class", "Helper", "Y.Helper", "y/Helper.cs", 1, 2)
        ]);

        var mentions = resolver.Resolve("Ver [[OrderService]], `Shop.OrderService.Place()` y calc_total_amount.\n```\nHelper dentro de código\n```\n`Helper` es ambiguo.");

        Assert.Equal([1L, 2, 3, 4, 5], mentions.Select(m => m.TargetId));
        Assert.Equal(MentionTypes.WikiLink, mentions[0].MentionType);
        Assert.Equal(MentionTypes.CodeSpan, mentions[1].MentionType);
        Assert.Equal((MentionTypes.Identifier, 0.8), (mentions[2].MentionType, mentions[2].Confidence));
        Assert.All(mentions.Skip(3), m => Assert.True(m.Ambiguous && Math.Abs(m.Confidence - 0.475) < 1e-9));
    }

    [Fact]
    public async Task Facets_ignore_user_documents_and_index_the_provided_documentation()
    {
        using var dir = new TempDirectory();
        var db = await CreateGraphAsync(dir.Path);
        Directory.CreateDirectory(Path.Combine(dir.Path, "src"));
        File.WriteAllText(Path.Combine(dir.Path, "src", "ClaseX.cs"), "class ClaseX { }");
        var (paths, doc) = await WithDocumentAsync(dir, DocText + "\n## CreateOrderFlow\n\npassword = \"S3cr3t0!\"\n");
        await new DocumentStore(paths).UploadAsync(new MemoryStream(Encoding.UTF8.GetBytes("class Subido { string password = \"abc123XYZ!\"; }")), "Subido.cs", CancellationToken.None);
        await KnowledgeGraphIngestor.AddAsync(db, paths, SourceRef.Document(doc.Id), CancellationToken.None);

        var context = new PipelineContext();
        context.Set(AnalysisContextKeys.PipelineInput, new AnalysisPipelineInput { RepositoryPath = dir.Path, DatabasePath = db, OutputReportsPath = paths.ReportsPath });
        context.Set(AnalysisContextKeys.Reporter, new CapturingReporter());
        var facets = await new SqlFacetExtractorStep().ExecuteAsync(new GraphStatusResult { DatabasePath = db }, context, 1, null, CancellationToken.None);

        Assert.DoesNotContain("UserDoc", facets.DocCoverageMetrics);
        Assert.DoesNotContain("UserDocSection", facets.CreationalCandidates);
        Assert.DoesNotContain(".codebase", facets.HardcodedSecrets);
        Assert.DoesNotContain("Subido", facets.HardcodedSecrets);
        using var index = JsonDocument.Parse(facets.ProvidedDocumentation);
        Assert.Equal(2, index.RootElement.GetArrayLength());
        var entry = index.RootElement.EnumerateArray().Single(e => e.GetProperty("id").GetString() == doc.Id);
        Assert.Contains("Demo.ClaseX", entry.GetProperty("linked_symbols").EnumerateArray().Select(s => s.GetString()));
    }

    [Fact]
    public async Task Embeddings_rank_the_expected_chunk_and_detect_outdated_and_model_mismatch()
    {
        using var dir = new TempDirectory();
        var (paths, doc) = await WithDocumentAsync(dir);
        var custom = new ReportWriter(paths).Create("Colas", "# Colas\n\nLas colas de mensajería usan reintentos exponenciales.", null, ReportVersionOrigin.Ui);
        var client = new HashingEmbeddingClient();
        var options = new EmbeddingOptions { Model = client.Model };
        var service = new KnowledgeEmbeddingService(paths, options, client);
        var docSource = SourceRef.Document(doc.Id);

        Assert.Equal(EmbeddingKnowledgeStates.Absent, service.GetState(docSource).State);
        var indexed = await service.IndexAsync(docSource, CancellationToken.None);
        await service.IndexAsync(custom.Source, CancellationToken.None);
        Assert.Equal(EmbeddingKnowledgeStates.Indexed, indexed.State);
        Assert.True(indexed.ChunkCount >= 2);

        var results = await service.SearchAsync("colas de mensajería con reintentos", 3, null, CancellationToken.None);
        Assert.Equal(custom.Source, results[0].Source);
        Assert.False(results[0].Stale);
        var onlyDocs = await service.SearchAsync("tarjeta cobra pagos", 3, [SourceKind.Document], CancellationToken.None);
        Assert.All(onlyDocs, r => Assert.Equal(SourceKind.Document, r.Source.Kind));
        Assert.Equal("Pagos", onlyDocs[0].Section);

        new ReportWriter(paths).Update(custom.Source, "append_section", "Más", "texto", "n", ReportVersionOrigin.Ui);
        Assert.Equal(EmbeddingKnowledgeStates.Outdated, service.GetState(custom.Source).State);
        Assert.True((await service.SearchAsync("colas de mensajería con reintentos", 1, null, CancellationToken.None))[0].Stale);
        Assert.Equal(1, service.Summary().OutdatedCount);

        var otherModel = new KnowledgeEmbeddingService(paths, options with { Model = "otro-modelo" }, client);
        Assert.Equal(EmbeddingKnowledgeStates.ModelMismatch, otherModel.GetState(docSource).State);

        Assert.Equal(EmbeddingKnowledgeStates.Absent, (await service.RemoveAsync(docSource, CancellationToken.None)).State);
        var disabled = new KnowledgeEmbeddingService(paths, options, null);
        Assert.Equal("embeddings_disabled", (await Assert.ThrowsAsync<KnowledgeOperationException>(() => disabled.IndexAsync(docSource, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task Semantic_search_tool_returns_results_and_says_when_it_is_unavailable()
    {
        using var dir = new TempDirectory();
        var (paths, doc) = await WithDocumentAsync(dir);
        var client = new HashingEmbeddingClient();
        var runtime = new EmbeddingsRuntime(new EmbeddingOptions { Model = client.Model }, client, null);
        await runtime.For(paths).IndexAsync(SourceRef.Document(doc.Id), CancellationToken.None);

        var context = new PipelineContext();
        context.Set(AnalysisContextKeys.KnowledgePaths, paths);
        context.Set(AnalysisContextKeys.ChatKnowledgeServices, new ChatKnowledgeServices { Embeddings = runtime });
        var result = await new SemanticSearchTool().ExecuteAsync("""{"query":"tarjeta cobra","top_k":2}""", context, "ChatAssistant", NullLogger.Instance);
        Assert.Contains(ReadDocumentTool.DataNotice, result);
        Assert.Contains($"\"id\": \"{doc.Id}\"", result);
        Assert.Contains("\"stale\": false", result);

        context.Set(AnalysisContextKeys.ChatKnowledgeServices, new ChatKnowledgeServices { Embeddings = new EmbeddingsRuntime(new EmbeddingOptions(), null, "sin clave") });
        Assert.Contains("no está disponible: sin clave", await new SemanticSearchTool().ExecuteAsync("""{"query":"x"}""", context, "ChatAssistant", NullLogger.Instance));
    }

    [Fact]
    public void Chunker_splits_code_in_windows_tables_by_rows_and_prose_with_context_prefix()
    {
        var code = "```csharp\n" + string.Join('\n', Enumerable.Range(1, 200).Select(i => $"linea {i};")) + "\n```\n";
        var codeChunks = Chunker.Split(Content(code, DocumentFamilies.Code, [new DocumentSection(1, "Pagos.cs", 1, 1, 203)]));
        Assert.Equal(3, codeChunks.Count);
        Assert.Equal((1, 80), (codeChunks[0].StartLine, codeChunks[0].EndLine));
        Assert.Equal(71, codeChunks[1].StartLine);
        Assert.StartsWith("Doc › Pagos.cs\n\n", codeChunks[0].Text);

        var table = "| a | b |\n|---|---|\n" + string.Join('\n', Enumerable.Range(1, 300).Select(i => $"| fila {i} | valor {i} |")) + "\n";
        var tableChunks = Chunker.Split(Content(table, DocumentFamilies.Csv, [new DocumentSection(1, "Tabla", 1, 1, 302)]), maxChars: 500);
        Assert.True(tableChunks.Count > 5);
        Assert.All(tableChunks, c => Assert.Contains("| a | b |\n|---|---|", c.Text));

        var prose = string.Join("\n\n", Enumerable.Range(1, 30).Select(i => $"Párrafo {i} " + new string('x', 150)));
        var proseChunks = Chunker.Split(Content(prose, DocumentFamilies.Markdown, []), maxChars: 600, overlapChars: 100);
        Assert.True(proseChunks.Count > 5);
        Assert.All(proseChunks, c => Assert.True(c.Text.Length <= 600 + 100 + 40, c.Text.Length.ToString()));
        Assert.Contains("Párrafo 30", proseChunks[^1].Text);

        static KnowledgeSourceContent Content(string text, string family, IReadOnlyList<DocumentSection> sections) =>
            new(SourceRef.Document("d1"), "Doc", text, "sha", "text/plain", family, "x", sections);
    }

    [Fact]
    public async Task Embedding_client_builds_azure_and_openai_requests_and_retries_429()
    {
        var handler = new FakeHandler();
        var azure = new OpenAICompatibleEmbeddingClient(new HttpClient(handler),
            new EmbeddingEndpoint("azure-openai", "https://res.openai.azure.com/", "clave-azure", "2024-08-01-preview"),
            new EmbeddingOptions { Model = "text-embedding-3-small", BatchSize = 2 }, (_, _) => Task.CompletedTask);
        handler.Responses.Enqueue(HttpStatusCode.TooManyRequests);

        var vectors = await azure.EmbedAsync(["a", "b", "c"], CancellationToken.None);

        Assert.Equal(3, vectors.Count);
        Assert.Equal(3, handler.Requests.Count); // 429, lote de 2 y lote de 1
        var request = handler.Requests[1];
        Assert.Equal("https://res.openai.azure.com/openai/deployments/text-embedding-3-small/embeddings?api-version=2024-08-01-preview", request.Url);
        Assert.Equal("clave-azure", request.Headers["api-key"]);
        Assert.DoesNotContain("\"model\"", request.Body);

        var openai = new OpenAICompatibleEmbeddingClient(new HttpClient(handler),
            new EmbeddingEndpoint("openai", "https://api.example/v1", "sk-x", null), new EmbeddingOptions { Model = "m", Dimensions = 8 });
        await openai.EmbedAsync(["z"], CancellationToken.None);
        var last = handler.Requests[^1];
        Assert.Equal("https://api.example/v1/embeddings", last.Url);
        Assert.Equal("Bearer sk-x", last.Headers["Authorization"]);
        Assert.Contains("\"model\":\"m\"", last.Body);
        Assert.Contains("\"dimensions\":8", last.Body);

        handler.Responses.Enqueue(HttpStatusCode.BadRequest);
        Assert.Equal("embedding_provider_error", (await Assert.ThrowsAsync<KnowledgeOperationException>(() => openai.EmbedAsync(["z"], CancellationToken.None))).Code);
    }

    [Fact]
    public void Runtime_is_disabled_with_a_reason_without_key_or_when_turned_off()
    {
        var off = EmbeddingsRuntime.FromConfiguration(TestConfig.FromPairs(("CodebaseAnalysis:Embeddings:Enabled", "false")), _ => null);
        Assert.False(off.Enabled);
        Assert.Contains("desactivados", off.DisabledReason);

        var noKey = EmbeddingsRuntime.FromConfiguration(TestConfig.FromPairs(
            ("LlmConfiguration:DefaultProvider", "Az"),
            ("LlmConfiguration:Providers:Az:ServiceType", "azure-openai"),
            ("LlmConfiguration:Providers:Az:BaseUrl", "https://x/"),
            ("LlmConfiguration:Providers:Az:ApiKey", "${Asistente__ApiKey}")), _ => null);
        Assert.False(noKey.Enabled);

        var ok = EmbeddingsRuntime.FromConfiguration(TestConfig.FromPairs(
            ("LlmConfiguration:DefaultProvider", "Az"),
            ("LlmConfiguration:Providers:Az:ServiceType", "azure-openai"),
            ("LlmConfiguration:Providers:Az:BaseUrl", "https://x/")), name => name == "Asistente__ApiKey" ? "k" : null);
        Assert.True(ok.Enabled);
        Assert.Equal("text-embedding-3-small", ok.Client!.Model);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Queue<HttpStatusCode> Responses { get; } = new();
        public List<(string Url, Dictionary<string, string> Headers, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.ToString(), request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)), body));
            if (Responses.TryDequeue(out var status))
                return new HttpResponseMessage(status) { Content = new StringContent("{\"error\":\"x\"}") };

            var count = JsonDocument.Parse(body).RootElement.GetProperty("input").GetArrayLength();
            var data = Enumerable.Range(0, count).Select(i => new { index = i, embedding = new[] { 1f, i, 0.5f } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data }), Encoding.UTF8, "application/json") };
        }
    }
}
