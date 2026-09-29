using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysisWeb.Workspace;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;

namespace CodebaseAnalysisWeb.Tests;

/// <summary>V2-B (plan v2 §5.1, §6): informes propios, versiones, restaurar, borrar y <c>report.changed</c> en el SSE.</summary>
public sealed class V2ReportsTests : IDisposable
{
    private const string ErrorsReport = "# Errores y bugs\n\n## Resumen\n\nTexto.\n\n<!-- analysis-meta: health=Red; critical=6 -->\n";

    private readonly TempDirectory _repo = new();

    public void Dispose() => _repo.Dispose();

    private KnowledgePaths Paths => new(_repo.Path, Path.Combine(_repo.Path, "reports"));

    private void WriteCatalog()
    {
        Directory.CreateDirectory(Paths.ReportsPath);
        File.WriteAllText(Path.Combine(Paths.ReportsPath, "05_errores_y_bugs.md"), ErrorsReport);
    }

    [Fact]
    public async Task A_chat_turn_creates_a_custom_report_with_report_changed_before_turn_completed_and_v1_listing_stays_catalog_only()
    {
        WriteCatalog();
        using var factory = new WebTestFactory
        {
            CustomLlm = new ToolScriptLlmService(("create_report", JsonSerializer.Serialize(new { title = "Estudio de colas", content = "Texto del estudio." })))
        };
        var client = factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);
        var session = await (await client.PostAsync("/api/chat/sessions", Http.JsonBody(new { }))).ReadJsonAsync();
        var id = session.GetProperty("id").GetString();

        var events = await (await client.PostSseAsync($"/api/chat/sessions/{id}/messages", new { message = "Guarda un estudio sobre las colas" })).ReadAllSseAsync();

        var types = events.Select(e => e.Data.GetProperty("type").GetString()).ToList();
        var changed = types.IndexOf("report.changed");
        Assert.True(changed > 0 && types[changed - 1] == "tool.completed" && changed < types.IndexOf("turn.completed"), string.Join(",", types));
        var data = events[changed].Data;
        Assert.Equal("custom/estudio-de-colas.md", data.GetProperty("id").GetString());
        Assert.Equal("estudio-de-colas.md", data.GetProperty("fileName").GetString());
        Assert.Equal("created", data.GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("previousVersionId").ValueKind);
        Assert.False(data.GetProperty("graphRefreshed").GetBoolean());

        var turn = (await (await client.GetAsync($"/api/chat/sessions/{id}")).ReadJsonAsync()).GetProperty("turns")[0];
        var artifact = turn.GetProperty("artifacts")[0];
        Assert.Equal("report", artifact.GetProperty("type").GetString());
        Assert.Equal("v0001", artifact.GetProperty("versionId").GetString());

        var catalog = await (await client.GetAsync("/api/reports")).ReadJsonAsync();
        Assert.Equal(["05_errores_y_bugs.md"], catalog.EnumerateArray().Select(r => r.GetProperty("id").GetString()));
        Assert.Equal("catalog", catalog[0].GetProperty("kind").GetString());

        var custom = (await (await client.GetAsync("/api/reports/custom")).ReadJsonAsync())[0];
        Assert.Equal(("custom/estudio-de-colas.md", "custom", "chat", 1), (custom.GetProperty("id").GetString(), custom.GetProperty("kind").GetString(),
            custom.GetProperty("origin").GetString(), custom.GetProperty("versionCount").GetInt32()));
        Assert.True(custom.GetProperty("modifiedByChat").GetBoolean());
        Assert.Equal(JsonValueKind.Null, custom.GetProperty("reportType").ValueKind);
        Assert.Equal("Estudio de colas", custom.GetProperty("title").GetString());
        Assert.Equal("absent", custom.GetProperty("knowledge").GetProperty("graph").GetProperty("state").GetString());

        var text = await (await client.GetAsync("/api/reports/custom/estudio-de-colas.md")).Content.ReadAsStringAsync();
        Assert.StartsWith("# Estudio de colas", text);
        Assert.Equal(1, (await (await client.GetAsync("/api/workspace")).ReadJsonAsync()).GetProperty("customReportCount").GetInt32());
    }

    [Fact]
    public async Task Versions_are_listed_newest_first_read_and_restored()
    {
        WriteCatalog();
        var writer = new ReportWriter(Paths);
        var source = SourceRef.Report("05_errores_y_bugs.md");
        var update = writer.Update(source, "append_section", "Colas", "Nueva sección.", "añadida", ReportVersionOrigin.Chat("s1", "t-0001"));
        using var factory = new WebTestFactory();
        var client = factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);

        var listed = (await (await client.GetAsync("/api/reports")).ReadJsonAsync())[0];
        Assert.True(listed.GetProperty("modifiedByChat").GetBoolean());
        Assert.Equal(2, listed.GetProperty("versionCount").GetInt32());

        var versions = await (await client.GetAsync("/api/reports/05_errores_y_bugs.md/versions")).ReadJsonAsync();
        Assert.Equal(["v0002", "v0001"], versions.EnumerateArray().Select(v => v.GetProperty("versionId").GetString()));
        Assert.Equal(("updated", "chat", "s1"), (versions[0].GetProperty("action").GetString(),
            versions[0].GetProperty("origin").GetProperty("type").GetString(), versions[0].GetProperty("origin").GetProperty("sessionId").GetString()));
        Assert.Equal([true, false], versions.EnumerateArray().Select(v => v.GetProperty("isCurrent").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, versions[1].GetProperty("origin").GetProperty("sessionId").ValueKind);

        Assert.Equal(ErrorsReport, await (await client.GetAsync("/api/reports/05_errores_y_bugs.md/versions/v0001")).Content.ReadAsStringAsync());
        var missing = await client.GetAsync("/api/reports/05_errores_y_bugs.md/versions/v0042");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("version_not_found", (await missing.ReadJsonAsync()).GetProperty("error").GetString());

        var restored = await client.PostAsync($"/api/reports/05_errores_y_bugs.md/versions/{update.PreviousVersionId}/restore", Http.JsonBody(new { }));
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var summary = await restored.ReadJsonAsync();
        Assert.False(summary.GetProperty("modifiedByChat").GetBoolean());
        Assert.Equal(3, summary.GetProperty("versionCount").GetInt32());
        Assert.Equal("Red", summary.GetProperty("health").GetString());
        Assert.Equal(ErrorsReport, File.ReadAllText(Path.Combine(Paths.ReportsPath, "05_errores_y_bugs.md")));

        // La restauración repite el SHA de v0001: solo la más reciente es la actual.
        var after = await (await client.GetAsync("/api/reports/05_errores_y_bugs.md/versions")).ReadJsonAsync();
        Assert.Equal([true, false, false], after.EnumerateArray().Select(v => v.GetProperty("isCurrent").GetBoolean()));
        Assert.Equal("ui", after[0].GetProperty("origin").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Deleting_a_custom_report_keeps_the_deleted_version_and_bad_names_are_404()
    {
        var created = new ReportWriter(Paths).Create("Borrable", "texto", null, ReportVersionOrigin.Chat("s", "t"));
        using var factory = new WebTestFactory();
        var client = factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/reports/custom/{created.FileName}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/reports/custom/{created.FileName}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/reports/custom/{created.FileName}")).StatusCode);
        var versions = await (await client.GetAsync($"/api/reports/custom/{created.FileName}/versions")).ReadJsonAsync();
        Assert.Equal(["deleted", "created"], versions.EnumerateArray().Select(v => v.GetProperty("action").GetString()));

        foreach (var bad in new[] { "/api/reports/custom/..%2Fx.md", "/api/reports/custom/NOTAS.md", "/api/reports/custom/x.txt", "/api/reports/notas.md/versions" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(bad)).StatusCode);
    }

    [Fact]
    public async Task Restore_and_delete_are_409_while_an_analysis_or_a_chat_turn_runs()
    {
        WriteCatalog();
        var writer = new ReportWriter(Paths);
        var update = writer.Update(SourceRef.Report("05_errores_y_bugs.md"), "append_section", "X", "x", "x", ReportVersionOrigin.Chat("s", "t"));
        var created = writer.Create("Propio", "texto", null, ReportVersionOrigin.Chat("s", "t"));
        using var factory = new WebTestFactory();
        var client = factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);
        var restoreUrl = $"/api/reports/05_errores_y_bugs.md/versions/{update.PreviousVersionId}/restore";

        var workspace = factory.Services.GetRequiredService<WorkspaceState>();
        Assert.True(workspace.TryReserveChatTurn("sesion"));
        var duringTurn = await client.PostAsync(restoreUrl, Http.JsonBody(new { }));
        Assert.Equal(HttpStatusCode.Conflict, duringTurn.StatusCode);
        Assert.Equal("chat_turn_running", (await duringTurn.ReadJsonAsync()).GetProperty("error").GetString());
        workspace.ReleaseChatTurn("sesion");

        factory.Workflow.HoldMidway = true;
        await client.StartRunAsync();
        await factory.Workflow.Held.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            var restore = await client.PostAsync(restoreUrl, Http.JsonBody(new { }));
            var delete = await client.DeleteAsync($"/api/reports/custom/{created.FileName}");
            Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
            Assert.Equal("analysis_running", (await restore.ReadJsonAsync()).GetProperty("error").GetString());
            Assert.Equal("analysis_running", (await delete.ReadJsonAsync()).GetProperty("error").GetString());
        }
        finally
        {
            factory.Workflow.Release();
            await client.WaitForRunEndAsync();
        }
    }

    [Fact]
    public async Task Undoing_a_chat_change_marks_the_turn_artifact_as_undone()
    {
        WriteCatalog();
        using var factory = new WebTestFactory
        {
            CustomLlm = new ToolScriptLlmService(("update_report", JsonSerializer.Serialize(new
            {
                file_name = "05_errores_y_bugs.md", mode = "append_section", heading = "Colas", content = "Texto nuevo.", note = "añadida"
            })))
        };
        var client = factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);
        var id = (await (await client.PostAsync("/api/chat/sessions", Http.JsonBody(new { }))).ReadJsonAsync()).GetProperty("id").GetString();
        var events = await (await client.PostSseAsync($"/api/chat/sessions/{id}/messages", new { message = "Añade al 05 una sección Colas" })).ReadAllSseAsync();
        var changed = events.Single(e => e.Data.GetProperty("type").GetString() == "report.changed").Data;
        Assert.Equal(("updated", "v0001", "v0002"), (changed.GetProperty("action").GetString(),
            changed.GetProperty("previousVersionId").GetString(), changed.GetProperty("versionId").GetString()));
        Assert.Contains("<!-- analysis-meta: health=Red; critical=6 -->", File.ReadAllText(Path.Combine(Paths.ReportsPath, "05_errores_y_bugs.md")));

        var before = (await (await client.GetAsync($"/api/chat/sessions/{id}")).ReadJsonAsync()).GetProperty("turns")[0].GetProperty("artifacts")[0];
        Assert.False(before.TryGetProperty("undoneByVersionId", out _));

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/reports/05_errores_y_bugs.md/versions/v0001/restore", Http.JsonBody(new { }))).StatusCode);
        var after = (await (await client.GetAsync($"/api/chat/sessions/{id}")).ReadJsonAsync()).GetProperty("turns")[0].GetProperty("artifacts")[0];
        Assert.Equal("v0003", after.GetProperty("undoneByVersionId").GetString());
        Assert.Equal(ErrorsReport, File.ReadAllText(Path.Combine(Paths.ReportsPath, "05_errores_y_bugs.md")));
    }
}
