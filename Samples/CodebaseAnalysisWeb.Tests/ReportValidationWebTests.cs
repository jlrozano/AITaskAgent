using CodebaseAnalysis.Core.Reports;
using System.Net;
using System.Text.Json;

namespace CodebaseAnalysisWeb.Tests;

/// <summary>
/// La validación de informes en la web: <c>GET /api/reports/validation</c> y el resumen al terminar una ejecución
/// (<c>run.completed</c>, el log de la ejecución y <c>reports/_validation.json</c>).
/// </summary>
public sealed class ReportValidationWebTests : IDisposable
{
    private readonly WebTestFactory _factory = new();
    private readonly TempDirectory _repo = new();

    public void Dispose()
    {
        _factory.Workflow.Release();
        _factory.Dispose();
        _repo.Dispose();
    }

    private async Task<HttpClient> ClientWithWorkspaceAsync()
    {
        var client = _factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);
        return client;
    }

    [Fact]
    public async Task Without_workspace_the_validation_is_409()
    {
        var response = await _factory.CreateClient().GetAsync("/api/reports/validation");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task The_endpoint_validates_the_reports_on_disk_without_writing()
    {
        Directory.CreateDirectory(Path.Combine(_repo.Path, "src"));
        File.WriteAllText(Path.Combine(_repo.Path, "src", "A.cs"), "a\nb\nc\n");
        Directory.CreateDirectory(Path.Combine(_repo.Path, "reports"));
        File.WriteAllText(Path.Combine(_repo.Path, "reports", "05_errores_y_bugs.md"),
            "# Errores\n\n### SEC-001, P0, x\n\n`src/A.cs:2` y `src/A.cs:9`.\n\n<!-- analysis-meta: health=Red -->\n");
        var client = await ClientWithWorkspaceAsync();

        var response = await client.GetAsync("/api/reports/validation");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.ReadJsonAsync();
        var totals = json.GetProperty("totals");
        Assert.Equal((1, 2, 1), (totals.GetProperty("reports").GetInt32(), totals.GetProperty("citationsTotal").GetInt32(),
            totals.GetProperty("brokenCitations").GetInt32()));
        var report = Assert.Single(json.GetProperty("reports").EnumerateArray());
        Assert.Equal("05_errores_y_bugs.md", report.GetProperty("fileName").GetString());
        Assert.Contains(report.GetProperty("issues").EnumerateArray(),
            i => i.GetProperty("check").GetString() == ReportValidationChecks.Citations && i.GetProperty("line").GetInt32() == 5);
        Assert.False(File.Exists(Path.Combine(_repo.Path, "reports", ReportValidationFile.FileName)));
    }

    [Fact]
    public async Task A_finished_run_carries_the_validation_in_run_completed_the_log_and_the_file()
    {
        var client = await ClientWithWorkspaceAsync();
        var runId = await client.StartRunAsync(new { reports = new[] { "smells", "di" } });
        await client.WaitForRunEndAsync();

        using var response = await client.GetSseAsync($"/api/analysis/{runId}/events?after=0");
        var events = await response.ReadAllSseAsync();
        var validation = events[^1].Data.GetProperty("data").GetProperty("validation");

        // Los tres informes del workflow falso, sin citas y sin procedencia.
        Assert.Equal(3, validation.GetProperty("reports").GetInt32());
        Assert.Equal(0, validation.GetProperty("citationsTotal").GetInt32());
        Assert.Equal(3, validation.GetProperty("reportsWithoutProvenance").GetInt32());
        var log = events[^2].Data;
        Assert.Equal(("log", ReportValidationSummary.SourceName, "warning"),
            (log.GetProperty("type").GetString(), log.GetProperty("source").GetString(), log.GetProperty("level").GetString()));
        Assert.StartsWith("🧪 Validación de 3 informe(s)", log.GetProperty("message").GetString(), StringComparison.Ordinal);

        using var file = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_repo.Path, "reports", ReportValidationFile.FileName)));
        Assert.Equal(3, file.RootElement.GetProperty("totals").GetProperty("reports").GetInt32());
    }
}
