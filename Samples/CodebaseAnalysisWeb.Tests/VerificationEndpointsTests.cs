using CodebaseAnalysis.Core.Models;
using System.Net;
using System.Text.Json;

namespace CodebaseAnalysisWeb.Tests;

/// <summary>
/// <c>POST /api/analysis/{runId}/verify</c>: la ejecución terminada vuelve a correr con el mismo <c>runId</c> y el mismo log
/// (como al regenerar), emite <c>run.verification_started</c> y un <c>report.verified</c> por informe, y cierra con otro
/// <c>run.completed</c> sin tocar <c>failedReports</c>.
/// </summary>
public sealed class VerificationEndpointsTests : IDisposable
{
    private const string SmellsFile = "02_code_smells.md";

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

    private static Task<HttpResponseMessage> VerifyAsync(HttpClient client, string runId, object body) =>
        client.PostAsync($"/api/analysis/{runId}/verify", Http.JsonBody(body));

    private static string TypeOf(SseEvent e) => e.Data.GetProperty("type").GetString()!;

    private async Task<string> FinishedRunAsync(HttpClient client)
    {
        var runId = await client.StartRunAsync(new { reports = new[] { "smells", "di" } });
        await client.WaitForRunEndAsync();
        return runId;
    }

    [Fact]
    public async Task An_unknown_run_is_404()
    {
        var client = await ClientWithWorkspaceAsync();

        var response = await VerifyAsync(client, "nope", new { reports = new[] { "smells" } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", (await response.ReadJsonAsync()).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("no-existe", "unknown_report")]
    [InlineData("99", "unknown_report")]
    [InlineData(null, "invalid_request")]
    public async Task An_unknown_report_or_an_empty_list_is_400(string? report, string code)
    {
        var client = await ClientWithWorkspaceAsync();
        var runId = await FinishedRunAsync(client);

        var response = await VerifyAsync(client, runId, new { reports = report == null ? Array.Empty<string>() : new[] { report } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await response.ReadJsonAsync()).GetProperty("error").GetString());
        Assert.Empty(_factory.Workflow.Verifications);
        Assert.Equal("succeeded", (await client.GetCurrentRunAsync())!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_run_of_another_repository_is_409()
    {
        var client = await ClientWithWorkspaceAsync();
        var runId = await FinishedRunAsync(client);
        using var other = new TempDirectory();
        await client.OpenWorkspaceAsync(other.Path);

        var response = await VerifyAsync(client, runId, new { reports = new[] { "smells" } });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("run_from_other_workspace", (await response.ReadJsonAsync()).GetProperty("error").GetString());
        Assert.Empty(_factory.Workflow.Verifications);
    }

    [Fact]
    public async Task While_the_run_a_regeneration_or_a_verification_is_running_it_is_409()
    {
        _factory.Workflow.HoldMidway = true;
        var client = await ClientWithWorkspaceAsync();
        var runId = await client.StartRunAsync();
        await _factory.Workflow.Held.WaitAsync(TimeSpan.FromSeconds(30));

        var during = await VerifyAsync(client, runId, new { reports = new[] { "smells" } });

        Assert.Equal(HttpStatusCode.Conflict, during.StatusCode);
        var body = await during.ReadJsonAsync();
        Assert.Equal("analysis_running", body.GetProperty("error").GetString());
        Assert.Equal(runId, body.GetProperty("runId").GetString());

        // Durante una verificación, ni otra, ni regenerar, ni un análisis nuevo.
        _factory.Workflow.Release();
        await client.WaitForRunEndAsync();
        _factory.Workflow.Reset();
        Assert.Equal(HttpStatusCode.Accepted, (await VerifyAsync(client, runId, new { reports = new[] { "smells" } })).StatusCode);
        await _factory.Workflow.Held.WaitAsync(TimeSpan.FromSeconds(30));

        var again = await VerifyAsync(client, runId, new { reports = new[] { "di" } });
        var regenerate = await client.PostAsync($"/api/analysis/{runId}/regenerate", Http.JsonBody(new { reports = new[] { "di" } }));
        var start = await client.PostAsync("/api/analysis", Http.JsonBody(new { }));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, regenerate.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
        Assert.Single(_factory.Workflow.Verifications);
        Assert.Empty(_factory.Workflow.Regenerations);
    }

    [Fact]
    public async Task The_verification_continues_the_same_run_and_log_and_ends_with_a_second_run_completed()
    {
        var client = await ClientWithWorkspaceAsync();
        var runId = await FinishedRunAsync(client);
        var first = (await client.GetCurrentRunAsync())!.Value;
        var firstLastSeq = first.GetProperty("lastSeq").GetInt32();

        var response = await VerifyAsync(client, runId, new { reports = new[] { "smells" } });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.ReadJsonAsync();
        Assert.Equal(runId, accepted.GetProperty("runId").GetString());
        Assert.Equal("running", accepted.GetProperty("status").GetString());

        var final = await client.WaitForRunEndAsync();
        Assert.Equal(runId, final.GetProperty("runId").GetString());
        Assert.Equal("succeeded", final.GetProperty("status").GetString());
        var (reports, synthesis) = Assert.Single(_factory.Workflow.Verifications);
        Assert.Equal([ReportType.CodeSmells], reports);
        Assert.False(synthesis);

        using var stream = await client.GetSseAsync($"/api/analysis/{runId}/events?after=0");
        var events = await stream.ReadAllSseAsync();

        Assert.Equal(Enumerable.Range(1, events.Count), events.Select(e => e.Data.GetProperty("seq").GetInt32()));
        Assert.Equal(2, events.Count(e => TypeOf(e) == "run.completed"));
        Assert.Equal(
            [
                "run.verification_started", "pipeline.started",
                "step.started", "step.completed",
                "step.started", "branch.started", "step.started",
                "report.verified", "step.completed", "branch.completed", "step.completed",
                "pipeline.completed", "log", "run.completed"
            ],
            events.Skip(firstLastSeq).Select(TypeOf));

        var started = events[firstLastSeq].Data;
        Assert.Equal(["CodeSmells"], started.GetProperty("data").GetProperty("reports").EnumerateArray().Select(e => e.GetString()));
        Assert.False(started.GetProperty("data").GetProperty("synthesis").GetBoolean());

        var verified = events.Single(e => TypeOf(e) == "report.verified").Data;
        Assert.Equal("CodeSmells", verified.GetProperty("report").GetString());
        Assert.Equal("corrected", verified.GetProperty("data").GetProperty("outcome").GetString());
        Assert.Equal(2, verified.GetProperty("data").GetProperty("changes").GetInt32());
        Assert.Equal("v0001", verified.GetProperty("data").GetProperty("previousVersionId").GetString());
        Assert.Equal("v0002", verified.GetProperty("data").GetProperty("versionId").GetString());

        var completed = events[^1].Data;
        Assert.Equal("success", completed.GetProperty("level").GetString());
        Assert.Equal("Verificación completada.", completed.GetProperty("message").GetString());
        Assert.Equal(0, completed.GetProperty("data").GetProperty("unverifiedReports").GetInt32());
    }

    [Fact]
    public async Task A_report_that_could_not_be_verified_does_not_fail_the_run_and_is_counted()
    {
        var client = await ClientWithWorkspaceAsync();
        var runId = await FinishedRunAsync(client);
        _factory.Workflow.FailingVerifications.Add(ReportType.CodeSmells);

        Assert.Equal(HttpStatusCode.Accepted, (await VerifyAsync(client, runId, new { reports = new[] { "smells", "di" } })).StatusCode);
        var final = await client.WaitForRunEndAsync();

        Assert.Equal("succeeded", final.GetProperty("status").GetString());
        using var stream = await client.GetSseAsync($"/api/analysis/{runId}/events?after=0");
        var completed = (await stream.ReadAllSseAsync()).Last(e => TypeOf(e) == "run.completed").Data;
        Assert.Equal("warning", completed.GetProperty("level").GetString());
        Assert.Equal(1, completed.GetProperty("data").GetProperty("unverifiedReports").GetInt32());
        Assert.Equal("Verificación completada; 1 informe(s) no se pudieron verificar y siguen como estaban.", completed.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Verifying_leaves_failedReports_as_it_was_and_the_synthesis_is_optional()
    {
        var client = await ClientWithWorkspaceAsync();
        _factory.Workflow.FailingReports.Add(ReportType.InyeccionDependenciasExtensiones);
        var runId = await client.StartRunAsync(new { reports = new[] { "smells", "di" } });
        var before = await client.WaitForRunEndAsync();
        _factory.Workflow.FailingReports.Clear();
        Assert.Equal("completed_with_failures", before.GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.Accepted, (await VerifyAsync(client, runId, new { reports = new[] { "smells" }, synthesis = true })).StatusCode);
        var after = await client.WaitForRunEndAsync();

        Assert.Equal("completed_with_failures", after.GetProperty("status").GetString());
        Assert.Equal(before.GetProperty("failedReports").ToString(), after.GetProperty("failedReports").ToString());
        Assert.True(Assert.Single(_factory.Workflow.Verifications).Synthesis);
        Assert.True(File.Exists(Path.Combine(_repo.Path, "reports", "00_resumen_ejecutivo.md")));
    }

    [Fact]
    public async Task A_cancelled_verification_ends_cancelled_and_a_later_one_works()
    {
        var client = await ClientWithWorkspaceAsync();
        var runId = await FinishedRunAsync(client);
        _factory.Workflow.HoldMidway = true;
        _factory.Workflow.Reset();

        Assert.Equal(HttpStatusCode.Accepted, (await VerifyAsync(client, runId, new { reports = new[] { "smells" } })).StatusCode);
        await _factory.Workflow.Held.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/analysis/{runId}/cancel", Http.JsonBody(new { }))).StatusCode);
        var cancelled = await client.WaitForRunEndAsync();

        Assert.Equal("cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal("Verificación cancelada.", cancelled.GetProperty("error").GetString());

        _factory.Workflow.HoldMidway = false;
        _factory.Workflow.Reset();
        Assert.Equal(HttpStatusCode.Accepted, (await VerifyAsync(client, runId, new { reports = new[] { "smells" } })).StatusCode);
        Assert.Equal("succeeded", (await client.WaitForRunEndAsync()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_run_meta_file_says_verification()
    {
        var client = await ClientWithWorkspaceAsync();
        var runId = await FinishedRunAsync(client);

        await VerifyAsync(client, runId, new { reports = new[] { "smells" } });
        await client.WaitForRunEndAsync();

        var meta = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_repo.Path, "reports", "_run_meta.json"))).RootElement;
        Assert.Equal("verification", meta.GetProperty("kind").GetString());
        Assert.Equal(runId, meta.GetProperty("runId").GetString());
        Assert.True(File.Exists(Path.Combine(_repo.Path, "reports", SmellsFile)));
    }
}
