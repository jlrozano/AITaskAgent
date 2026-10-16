using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using CodebaseAnalysis.Core.Knowledge.Embeddings;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tests;
using CodebaseAnalysisWeb.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodebaseAnalysisWeb.Tests;

/// <summary>V2-C y V2-D en la web (plan v2 §5.1, §5.5, §7.5): subida por familia, rechazos, cabeceras, grafo, embeddings y la matriz de concurrencia.</summary>
public sealed class V2DocumentsKnowledgeTests : IDisposable
{
    private readonly TempDirectory _repo = new();

    public void Dispose() => _repo.Dispose();

    private static HttpRequestMessage UploadRequest(string fileName, byte[] bytes, string contentType = "application/octet-stream")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/documents") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        request.Headers.Add("X-File-Name", Uri.EscapeDataString(fileName));
        return request;
    }

    private static async Task<JsonElement> UploadAsync(HttpClient client, string fileName, byte[] bytes)
    {
        var response = await client.SendAsync(UploadRequest(fileName, bytes));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response) => (await response.ReadJsonAsync()).GetProperty("error").GetString()!;

    private async Task CreateGraphAsync()
    {
        var db = Path.Combine(_repo.Path, ".codebase", "codebase.db");
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        await GraphSchema.EnsureAsync(db, CancellationToken.None);
        await using var conn = new SqliteConnection($"Data Source={db};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO nodes (project, label, name, qualified_name, file_path, start_line, end_line) VALUES ('Codebase', 'Class', 'PaymentValidator', 'Shop.PaymentValidator', 'src/PaymentValidator.cs', 1, 30);";
        await cmd.ExecuteNonQueryAsync();
    }

    private static WebTestFactory WithEmbeddings(ILlmService? llm = null) => new()
    {
        CustomLlm = llm,
        ConfigureServices = services =>
        {
            services.RemoveAll<EmbeddingsRuntime>();
            services.AddSingleton(new EmbeddingsRuntime(new EmbeddingOptions { Model = "fake-embeddings", Dimensions = 32 }, new HashingClient(), null));
        }
    };

    [Fact]
    public async Task Every_family_uploads_and_is_readable_with_the_raw_headers_of_its_family()
    {
        using var factory = new WebTestFactory();
        var client = factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);

        var ids = new Dictionary<string, string>();
        var sent = new Dictionary<string, byte[]>();
        foreach (var (name, bytes) in SampleFiles.All())
        {
            sent[name] = bytes;
            var created = await UploadAsync(client, name, bytes);
            ids[name] = created.GetProperty("id").GetString()!;
            Assert.Equal("absent", created.GetProperty("knowledge").GetProperty("graph").GetProperty("state").GetString());
        }

        var list = await (await client.GetAsync("/api/documents")).ReadJsonAsync();
        Assert.Equal(SampleFiles.All().Count, list.GetArrayLength());
        Assert.Equal(SampleFiles.All().Count, (await (await client.GetAsync("/api/workspace")).ReadJsonAsync()).GetProperty("documentCount").GetInt32());

        var text = await (await client.GetAsync($"/api/documents/{ids["especificacion.docx"]}/text")).Content.ReadAsStringAsync();
        Assert.Contains("# Especificación API", text);
        var detail = await (await client.GetAsync($"/api/documents/{ids["contrato.pdf"]}")).ReadJsonAsync();
        Assert.Equal(("pdf", 2, 2), (detail.GetProperty("viewer").GetString(), detail.GetProperty("extraction").GetProperty("pageCount").GetInt32(), detail.GetProperty("sections").GetArrayLength()));
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("description").ValueKind);

        async Task<HttpResponseMessage> Raw(string name) => await client.GetAsync($"/api/documents/{ids[name]}/raw");

        var pdf = await Raw("contrato.pdf");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.Equal("inline", pdf.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", pdf.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("same-origin", pdf.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.Contains("no-store", pdf.Headers.CacheControl!.ToString());
        Assert.False(pdf.Headers.Contains("Content-Security-Policy"));
        Assert.Equal(sent["contrato.pdf"], await pdf.Content.ReadAsByteArrayAsync());

        foreach (var name in new[] { "pagina.html", "diagrama.svg" })
        {
            var sandboxed = await Raw(name);
            Assert.StartsWith("sandbox; default-src 'none'", sandboxed.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("inline", sandboxed.Content.Headers.ContentDisposition!.DispositionType);
        }
        // Sin charset el navegador lee un HTML sin <meta charset> como windows-1252 y los acentos salen rotos (V2-E).
        var html = (await Raw("pagina.html")).Content.Headers.ContentType!;
        Assert.Equal(("text/html", "utf-8"), (html.MediaType, html.CharSet));
        Assert.Equal("image/png", (await Raw("dos-colores.png")).Content.Headers.ContentType!.MediaType);

        var md = await Raw("guia.md");
        Assert.Equal("application/octet-stream", md.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", md.Content.Headers.ContentDisposition!.DispositionType);
    }

    [Fact]
    public async Task Rejections_return_their_codes_and_status()
    {
        using var factory = new WebTestFactory();
        var client = factory.CreateClient();

        Assert.Equal("no_workspace", await ErrorOf(await client.SendAsync(UploadRequest("a.md", SampleFiles.Markdown()))));
        await client.OpenWorkspaceAsync(_repo.Path);

        var wrongType = await client.SendAsync(UploadRequest("a.md", SampleFiles.Markdown(), "multipart/form-data; boundary=x"));
        Assert.Equal((HttpStatusCode.UnsupportedMediaType, "invalid_request"), (wrongType.StatusCode, await ErrorOf(wrongType)));

        var cases = new (string Name, byte[] Bytes, HttpStatusCode Status, string Code)[]
        {
            ("..%2Fx.md", SampleFiles.Markdown(), HttpStatusCode.BadRequest, "invalid_file_name"),
            ("viejo.xls", SampleFiles.Markdown(), HttpStatusCode.BadRequest, "unsupported_type"),
            ("foto.png", SampleFiles.Executable(), HttpStatusCode.BadRequest, "content_mismatch"),
            ("vacio.md", [], HttpStatusCode.BadRequest, "empty_file"),
            ("grande.md", new byte[5 * 1024 * 1024 + 10], HttpStatusCode.RequestEntityTooLarge, "file_too_large")
        };
        foreach (var (name, bytes, status, code) in cases)
        {
            var request = UploadRequest("x", bytes);
            request.Headers.Remove("X-File-Name");
            request.Headers.Add("X-File-Name", name.Contains('%') ? name : Uri.EscapeDataString(name));
            var response = await client.SendAsync(request);
            Assert.Equal((status, code), (response.StatusCode, await ErrorOf(response)));
        }

        var first = await UploadAsync(client, "guia.md", SampleFiles.Markdown());
        var duplicate = await client.SendAsync(UploadRequest("copia.md", SampleFiles.Markdown()));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var body = await duplicate.ReadJsonAsync();
        Assert.Equal(("duplicate_document", first.GetProperty("id").GetString()), (body.GetProperty("error").GetString(), body.GetProperty("documentId").GetString()));

        var crossSite = UploadRequest("otra.md", SampleFiles.Utf8("# otra"));
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(crossSite)).StatusCode);
    }

    [Fact]
    public async Task Patch_describe_and_delete_documents()
    {
        using var factory = new WebTestFactory
        {
            CustomLlm = new DescribingLlm("Mitad roja y mitad azul."),
            ExtraConfiguration = { [$"{CodebaseAnalysis.Core.Hosting.LlmRoleFactory.SectionName}:Providers:Fake:ApiKey"] = "k" }
        };
        var client = factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);
        var meta = await (await client.GetAsync("/api/meta")).ReadJsonAsync();
        Assert.True(meta.GetProperty("features").GetProperty("imageDescription").GetProperty("enabled").GetBoolean());

        var md = (await UploadAsync(client, "guia.md", SampleFiles.Markdown())).GetProperty("id").GetString();
        var png = (await UploadAsync(client, "dos-colores.png", SampleFiles.Png())).GetProperty("id").GetString();

        var patched = await client.PatchAsync($"/api/documents/{md}", Http.JsonBody(new { title = "Guía", useInAnalysis = false }));
        var patchedBody = await patched.ReadJsonAsync();
        Assert.Equal(("Guía", false), (patchedBody.GetProperty("title").GetString(), patchedBody.GetProperty("useInAnalysis").GetBoolean()));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsync($"/api/documents/{md}", Http.JsonBody(new { title = " " }))).StatusCode);

        var notImage = await client.PostAsync($"/api/documents/{md}/describe", Http.JsonBody(new { }));
        Assert.Equal((HttpStatusCode.BadRequest, "not_an_image"), (notImage.StatusCode, await ErrorOf(notImage)));

        var described = await client.PostAsync($"/api/documents/{png}/describe", Http.JsonBody(new { }));
        Assert.Equal(HttpStatusCode.OK, described.StatusCode);
        var detail = await described.ReadJsonAsync();
        Assert.Equal("ok", detail.GetProperty("extraction").GetProperty("status").GetString());
        Assert.Equal("fake-model", detail.GetProperty("description").GetProperty("model").GetString());
        Assert.Contains("Mitad roja", await (await client.GetAsync($"/api/documents/{png}/text")).Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/documents/{md}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/documents/{md}")).StatusCode);
    }

    [Fact]
    public async Task Describe_is_409_without_a_vision_provider()
    {
        using var factory = new WebTestFactory();
        var client = factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);
        var png = (await UploadAsync(client, "dos-colores.png", SampleFiles.Png())).GetProperty("id").GetString();

        var response = await client.PostAsync($"/api/documents/{png}/describe", Http.JsonBody(new { }));
        Assert.Equal((HttpStatusCode.Conflict, "image_description_unavailable"), (response.StatusCode, await ErrorOf(response)));
        var meta = await (await client.GetAsync("/api/meta")).ReadJsonAsync();
        Assert.False(meta.GetProperty("features").GetProperty("imageDescription").GetProperty("enabled").GetBoolean());
        Assert.False(meta.GetProperty("features").GetProperty("embeddings").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Graph_add_and_remove_follow_the_concurrency_matrix()
    {
        using var factory = new WebTestFactory();
        var client = factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);
        var id = (await UploadAsync(client, "guia.md", SampleFiles.Markdown())).GetProperty("id").GetString();
        var body = new { source = new { kind = "document", id } };

        var noGraph = await client.PostAsync("/api/knowledge/graph", Http.JsonBody(body));
        Assert.Equal((HttpStatusCode.Conflict, "no_graph"), (noGraph.StatusCode, await ErrorOf(noGraph)));
        Assert.False((await (await client.GetAsync("/api/workspace")).ReadJsonAsync()).GetProperty("hasGraph").GetBoolean());

        await CreateGraphAsync();
        Assert.True((await (await client.GetAsync("/api/workspace")).ReadJsonAsync()).GetProperty("hasGraph").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/knowledge/graph", Http.JsonBody(new { source = new { kind = "otro", id } }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/knowledge/graph", Http.JsonBody(new { source = new { kind = "document", id = "no-existe" } }))).StatusCode);

        var workspace = factory.Services.GetRequiredService<WorkspaceState>();
        Assert.True(workspace.TryReserveChatTurn("s"));
        Assert.Equal("chat_turn_running", await ErrorOf(await client.PostAsync("/api/knowledge/graph", Http.JsonBody(body))));
        workspace.ReleaseChatTurn("s");
        using (workspace.BeginKnowledgeOperation())
            Assert.Equal("knowledge_operation_running", await ErrorOf(await client.PostAsync("/api/knowledge/graph", Http.JsonBody(body))));

        var added = await client.PostAsync("/api/knowledge/graph", Http.JsonBody(body));
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var state = await added.ReadJsonAsync();
        Assert.Equal(("document", "present"), (state.GetProperty("source").GetProperty("kind").GetString(), state.GetProperty("graph").GetProperty("state").GetString()));
        Assert.True(state.GetProperty("graph").GetProperty("edgeCount").GetInt32() >= 4); // HAS_SECTION ×3 y DOC_DESCRIBES a PaymentValidator
        Assert.Equal(1, (await (await client.GetAsync("/api/knowledge")).ReadJsonAsync()).GetProperty("graph").GetProperty("sourceCount").GetInt32());
        var listed = (await (await client.GetAsync("/api/documents")).ReadJsonAsync())[0];
        Assert.Equal("present", listed.GetProperty("knowledge").GetProperty("graph").GetProperty("state").GetString());

        factory.Workflow.HoldMidway = true;
        await client.StartRunAsync();
        await factory.Workflow.Held.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            Assert.Equal("analysis_running", await ErrorOf(await client.DeleteAsync($"/api/knowledge/graph/document/{id}")));
            Assert.Equal("analysis_running", await ErrorOf(await client.DeleteAsync($"/api/documents/{id}")));
        }
        finally
        {
            factory.Workflow.Release();
            await client.WaitForRunEndAsync();
        }

        var removed = await (await client.DeleteAsync($"/api/knowledge/graph/document/{id}")).ReadJsonAsync();
        Assert.Equal("absent", removed.GetProperty("graph").GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/knowledge/graph/document/no-existe")).StatusCode);
    }

    [Fact]
    public async Task Embeddings_index_search_from_chat_and_remove()
    {
        using var disabledFactory = new WebTestFactory();
        var disabledClient = disabledFactory.CreateClient();
        await disabledClient.OpenWorkspaceAsync(_repo.Path);
        var id = (await UploadAsync(disabledClient, "guia.md", SampleFiles.Markdown())).GetProperty("id").GetString();
        var body = new { source = new { kind = "document", id } };
        Assert.Equal("embeddings_disabled", await ErrorOf(await disabledClient.PostAsync("/api/knowledge/embeddings", Http.JsonBody(body))));
        disabledFactory.Dispose();

        using var factory = WithEmbeddings(new ToolScriptLlmService(("semantic_search", """{"query":"validación de pagos"}""")));
        var client = factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);
        var meta = await (await client.GetAsync("/api/meta")).ReadJsonAsync();
        Assert.Equal((true, "fake-embeddings", 32), (meta.GetProperty("features").GetProperty("embeddings").GetProperty("enabled").GetBoolean(),
            meta.GetProperty("features").GetProperty("embeddings").GetProperty("model").GetString(),
            meta.GetProperty("features").GetProperty("embeddings").GetProperty("dimensions").GetInt32()));

        // Con un análisis en curso se puede indexar un documento, pero no un informe del catálogo.
        Directory.CreateDirectory(Path.Combine(_repo.Path, "reports"));
        File.WriteAllText(Path.Combine(_repo.Path, "reports", "05_errores_y_bugs.md"), "# Errores\n\ntexto\n");
        factory.Workflow.HoldMidway = true;
        await client.StartRunAsync();
        await factory.Workflow.Held.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            var indexed = await client.PostAsync("/api/knowledge/embeddings", Http.JsonBody(body));
            Assert.Equal(HttpStatusCode.OK, indexed.StatusCode);
            Assert.Equal("indexed", (await indexed.ReadJsonAsync()).GetProperty("embeddings").GetProperty("state").GetString());
            Assert.Equal("analysis_running", await ErrorOf(await client.PostAsync("/api/knowledge/embeddings",
                Http.JsonBody(new { source = new { kind = "report", id = "05_errores_y_bugs.md" } }))));
        }
        finally
        {
            factory.Workflow.Release();
            await client.WaitForRunEndAsync();
        }

        var summary = (await (await client.GetAsync("/api/knowledge")).ReadJsonAsync()).GetProperty("embeddings");
        Assert.Equal((1, 0), (summary.GetProperty("sourceCount").GetInt32(), summary.GetProperty("outdatedCount").GetInt32()));
        Assert.True(summary.GetProperty("chunkCount").GetInt32() >= 1);

        var session = (await (await client.PostAsync("/api/chat/sessions", Http.JsonBody(new { }))).ReadJsonAsync()).GetProperty("id").GetString();
        var events = await (await client.PostSseAsync($"/api/chat/sessions/{session}/messages", new { message = "¿Qué dice la documentación de pagos?" })).ReadAllSseAsync();
        var completed = events.Single(e => e.Data.GetProperty("type").GetString() == "tool.completed").Data;
        Assert.Equal("semantic_search", completed.GetProperty("name").GetString());
        Assert.True(completed.GetProperty("success").GetBoolean());

        var removed = await (await client.DeleteAsync($"/api/knowledge/embeddings/document/{id}")).ReadJsonAsync();
        Assert.Equal("absent", removed.GetProperty("embeddings").GetProperty("state").GetString());
    }

    private sealed class HashingClient : IEmbeddingClient
    {
        public string Model => "fake-embeddings";

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(text =>
            {
                var v = new float[32];
                foreach (var word in text.ToLowerInvariant().Split(' ', '\n', '.'))
                    v[(int)((uint)word.GetHashCode(StringComparison.Ordinal) % 32)] += 1;
                return v;
            }).ToList());
    }

    /// <summary>Responde sin streaming con una descripción fija (para «Describir»).</summary>
    private sealed class DescribingLlm(string answer) : ILlmService
    {
        public string Name => "describing";

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(LlmResult.Success([new LlmResponse { Content = answer, FinishReason = FinishReason.Stop }], model: request.Model));

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int EstimateTokenCount(string text) => text.Length / 4;
        public int GetMaxContextTokens(string? model = null) => 1_000_000;
    }
}
