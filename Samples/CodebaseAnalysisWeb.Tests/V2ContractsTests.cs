using CodebaseAnalysisWeb.Workspace;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;

namespace CodebaseAnalysisWeb.Tests;

/// <summary>V2-0 (plan v2 §5, §10): contratos compartidos, sin comportamiento nuevo salvo el guard y el cerrojo de conocimiento.</summary>
public sealed class V2ContractsTests : IDisposable
{
    private readonly WebTestFactory _factory = new();
    private readonly TempDirectory _repo = new();

    public void Dispose()
    {
        _factory.Workflow.Release();
        _factory.Dispose();
        _repo.Dispose();
    }

    private static HttpRequestMessage PostSession(string? secFetchSite)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/sessions") { Content = Http.JsonBody(new { }) };
        if (secFetchSite != null)
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", secFetchSite);
        return request;
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    public async Task A_state_changing_request_from_another_site_is_403_cross_site_request(string site)
    {
        var client = _factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);

        var response = await client.SendAsync(PostSession(site));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("cross_site_request", (await response.ReadJsonAsync()).GetProperty("error").GetString());
        Assert.Equal(0, (await (await client.GetAsync("/api/chat/sessions")).ReadJsonAsync()).GetArrayLength());
    }

    [Theory]
    [InlineData("same-origin")]
    [InlineData("none")]
    [InlineData(null)]
    public async Task Same_origin_direct_or_headerless_requests_pass(string? site)
    {
        var client = _factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);

        var response = await client.SendAsync(PostSession(site));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Reads_from_another_site_are_not_blocked_by_the_guard()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/meta");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");

        var response = await _factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Meta_exposes_the_document_types_limits_and_features()
    {
        var meta = await (await _factory.CreateClient().GetAsync("/api/meta")).ReadJsonAsync();

        Assert.Equal("custom", meta.GetProperty("customReportsFolder").GetString());
        var documents = meta.GetProperty("documents");
        Assert.Equal(26_214_400, documents.GetProperty("maxFileBytes").GetInt64());
        Assert.Equal(524_288_000, documents.GetProperty("maxTotalBytes").GetInt64());
        Assert.Equal(300, documents.GetProperty("maxDocuments").GetInt32());
        var types = documents.GetProperty("types").EnumerateArray().ToList();
        Assert.Equal(12, types.Count);
        var pdf = types.Single(t => t.GetProperty("family").GetString() == "pdf");
        Assert.Equal([".pdf"], pdf.GetProperty("extensions").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(26_214_400, pdf.GetProperty("maxBytes").GetInt64());
        Assert.Equal("pdf", pdf.GetProperty("viewer").GetString());
        Assert.Equal("image", types.Single(t => t.GetProperty("family").GetString() == "svg").GetProperty("viewer").GetString());

        foreach (var feature in new[] { "embeddings", "imageDescription" })
        {
            var f = meta.GetProperty("features").GetProperty(feature);
            Assert.False(f.GetProperty("enabled").GetBoolean());
            Assert.Equal(JsonValueKind.Null, f.GetProperty("model").ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(f.GetProperty("disabledReason").GetString()));
        }
    }

    [Fact]
    public async Task Workspace_counts_documents_and_custom_reports_with_the_validated_names_only()
    {
        var documents = Path.Combine(_repo.Path, ".codebase", "documents");
        foreach (var (id, withMetadata) in new[] { ("20260927-111203-a7qk", true), ("sin-metadatos", false), ("MAYUSCULAS", true), (".incoming", true) })
        {
            Directory.CreateDirectory(Path.Combine(documents, id));
            if (withMetadata)
                File.WriteAllText(Path.Combine(documents, id, "document.json"), "{}");
        }
        var custom = Path.Combine(_repo.Path, "reports", "custom");
        Directory.CreateDirectory(custom);
        File.WriteAllText(Path.Combine(custom, "estudio-de-colas.md"), "# Colas");
        File.WriteAllText(Path.Combine(custom, "Notas Mías.md"), "ajeno");
        File.WriteAllText(Path.Combine(custom, "otro.txt"), "ajeno");
        var client = _factory.CreateClient();

        await client.OpenWorkspaceAsync(_repo.Path);
        var ws = await (await client.GetAsync("/api/workspace")).ReadJsonAsync();

        Assert.Equal(documents, ws.GetProperty("documentsPath").GetString());
        Assert.Equal(1, ws.GetProperty("documentCount").GetInt32());
        Assert.Equal(1, ws.GetProperty("customReportCount").GetInt32());
    }

    [Fact]
    public async Task Without_workspace_the_v2_workspace_fields_are_empty()
    {
        var ws = await (await _factory.CreateClient().GetAsync("/api/workspace")).ReadJsonAsync();

        Assert.Equal(JsonValueKind.Null, ws.GetProperty("documentsPath").ValueKind);
        Assert.Equal(0, ws.GetProperty("documentCount").GetInt32());
        Assert.Equal(0, ws.GetProperty("customReportCount").GetInt32());
    }

    [Fact]
    public async Task The_v1_report_listing_keeps_the_catalog_only_and_carries_the_v2_fields()
    {
        var reports = Path.Combine(_repo.Path, "reports");
        Directory.CreateDirectory(Path.Combine(reports, "custom"));
        File.WriteAllText(Path.Combine(reports, "05_errores_y_bugs.md"), "# E\n<!-- analysis-meta: health=Red; critical=6 -->");
        File.WriteAllText(Path.Combine(reports, "custom", "estudio.md"), "# Estudio");
        var client = _factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);

        var list = await (await client.GetAsync("/api/reports")).ReadJsonAsync();

        var report = Assert.Single(list.EnumerateArray());
        Assert.Equal("catalog", report.GetProperty("kind").GetString());
        Assert.Equal("05_errores_y_bugs.md", report.GetProperty("id").GetString());
        Assert.Equal("pipeline", report.GetProperty("origin").GetString());
        Assert.False(report.GetProperty("modifiedByChat").GetBoolean());
        Assert.Equal(0, report.GetProperty("versionCount").GetInt32());
        var knowledge = report.GetProperty("knowledge");
        Assert.Equal("absent", knowledge.GetProperty("graph").GetProperty("state").GetString());
        Assert.Equal(0, knowledge.GetProperty("graph").GetProperty("nodeCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, knowledge.GetProperty("graph").GetProperty("addedAt").ValueKind);
        Assert.Equal("absent", knowledge.GetProperty("embeddings").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, knowledge.GetProperty("embeddings").GetProperty("model").ValueKind);
    }

    [Fact]
    public async Task A_knowledge_operation_blocks_analysis_chat_and_workspace_change_until_released()
    {
        var client = _factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);
        var session = await (await client.PostAsync("/api/chat/sessions", Http.JsonBody(new { }))).ReadJsonAsync();
        var messages = $"/api/chat/sessions/{session.GetProperty("id").GetString()}/messages";
        var workspace = _factory.Services.GetRequiredService<WorkspaceState>();

        var lease = workspace.BeginKnowledgeOperation();
        try
        {
            foreach (var response in new[]
                     {
                         await client.PostAsync("/api/analysis", Http.JsonBody(new { })),
                         await client.PostAsync(messages, Http.JsonBody(new { message = "hola" })),
                         await client.PutAsync("/api/workspace", Http.JsonBody(new { repositoryPath = _repo.Path }))
                     })
            {
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Equal("knowledge_operation_running", (await response.ReadJsonAsync()).GetProperty("error").GetString());
            }
        }
        finally
        {
            lease.Dispose();
            lease.Dispose();
        }

        Assert.False(workspace.AnyKnowledgeOperationRunning);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync("/api/workspace", Http.JsonBody(new { repositoryPath = _repo.Path }))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/analysis", Http.JsonBody(new { }))).StatusCode);
        await client.WaitForRunEndAsync();
    }

    [Fact]
    public void Duplicate_document_errors_carry_the_existing_document_id_and_other_errors_do_not()
    {
        var duplicate = JsonSerializer.SerializeToElement(Api.ApiErrorsFactory.DuplicateDocument("20260927-111203-a7qk"), Http.Json);
        var other = JsonSerializer.SerializeToElement(Api.ApiErrorsFactory.KnowledgeOperationRunning(), Http.Json);

        Assert.Equal("duplicate_document", duplicate.GetProperty("error").GetString());
        Assert.Equal("20260927-111203-a7qk", duplicate.GetProperty("documentId").GetString());
        Assert.False(other.TryGetProperty("documentId", out _));
        Assert.False(other.TryGetProperty("runId", out _));
    }
}
