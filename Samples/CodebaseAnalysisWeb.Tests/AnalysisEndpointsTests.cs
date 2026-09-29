using System.Net;
using System.Text;

namespace CodebaseAnalysisWeb.Tests;

public sealed class AnalysisEndpointsTests : IDisposable
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
    public async Task Without_workspace_starting_is_409_and_current_is_204()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/analysis", Http.JsonBody(new { }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("no_workspace", (await response.ReadJsonAsync()).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/analysis/current")).StatusCode);
    }

    [Fact]
    public async Task Start_returns_202_with_the_status_and_the_requested_reports()
    {
        var client = await ClientWithWorkspaceAsync();

        var response = await client.PostAsync("/api/analysis", Http.JsonBody(new { forceReindex = true, reports = new[] { "smells", "di", "code_smells" } }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var status = await response.ReadJsonAsync();
        Assert.Equal(12, status.GetProperty("runId").GetString()!.Length);
        Assert.Equal(_repo.Path, status.GetProperty("repositoryPath").GetString());
        Assert.True(status.GetProperty("forceReindex").GetBoolean());
        Assert.Equal(["CodeSmells", "InyeccionDependenciasExtensiones"],
            status.GetProperty("requestedReports").EnumerateArray().Select(e => e.GetString()));

        var final = await client.WaitForRunEndAsync();
        Assert.Equal("succeeded", final.GetProperty("status").GetString());
        Assert.Equal(FakeAnalysisWorkflow.ExpectedTypes.Length, final.GetProperty("lastSeq").GetInt32());
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, final.GetProperty("finishedAt").ValueKind);
    }

    [Fact]
    public async Task Without_reports_the_configured_list_is_used()
    {
        var client = await ClientWithWorkspaceAsync();

        var status = await (await client.PostAsync("/api/analysis", Http.JsonBody(new { }))).ReadJsonAsync();

        Assert.Equal(9, status.GetProperty("requestedReports").GetArrayLength());
        await client.WaitForRunEndAsync();
    }

    [Theory]
    [InlineData("no-existe")]
    [InlineData("99")]
    public async Task An_unknown_report_is_400(string name)
    {
        var client = await ClientWithWorkspaceAsync();

        var response = await client.PostAsync("/api/analysis", Http.JsonBody(new { reports = new[] { "smells", name } }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unknown_report", (await response.ReadJsonAsync()).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/analysis/current")).StatusCode);
    }

    [Fact]
    public async Task A_non_json_body_is_415()
    {
        var client = await ClientWithWorkspaceAsync();

        var response = await client.PostAsync("/api/analysis", new StringContent("{}", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task A_second_start_while_running_is_409_with_the_run_id()
    {
        _factory.Workflow.HoldMidway = true;
        var client = await ClientWithWorkspaceAsync();
        var runId = await client.StartRunAsync();
        await _factory.Workflow.Held.WaitAsync(TimeSpan.FromSeconds(30));

        var response = await client.PostAsync("/api/analysis", Http.JsonBody(new { }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal("analysis_running", body.GetProperty("error").GetString());
        Assert.Equal(runId, body.GetProperty("runId").GetString());
        Assert.Equal("running", (await client.GetCurrentRunAsync())!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Events_from_zero_replay_in_order_and_end_with_run_completed()
    {
        var client = await ClientWithWorkspaceAsync();
        var runId = await client.StartRunAsync(new { reports = new[] { "smells", "di" } });
        await client.WaitForRunEndAsync();

        using var response = await client.GetSseAsync($"/api/analysis/{runId}/events?after=0");
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl!.ToString());
        Assert.Equal("no", response.Headers.GetValues("X-Accel-Buffering").Single());
        var events = await response.ReadAllSseAsync();

        Assert.All(events, e => Assert.Equal("analysis", e.Event));
        Assert.Equal(FakeAnalysisWorkflow.ExpectedTypes, events.Select(e => e.Data.GetProperty("type").GetString()));
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => i.ToString()), events.Select(e => e.Id));
        Assert.Equal(Enumerable.Range(1, events.Count), events.Select(e => e.Data.GetProperty("seq").GetInt32()));
        Assert.All(events, e => Assert.Equal(runId, e.Data.GetProperty("runId").GetString()));

        var last = events[^1].Data;
        Assert.Equal("succeeded", last.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(0, last.GetProperty("data").GetProperty("exitCode").GetInt32());

        // Campos comunes de §4.3 en un evento de tool.
        var tool = events.First(e => e.Data.GetProperty("type").GetString() == "tool.completed").Data;
        Assert.Equal("Auditor_CodeSmells", tool.GetProperty("source").GetString());
        Assert.Equal("CodeSmells", tool.GetProperty("report").GetString());
        Assert.Equal("reports", tool.GetProperty("phase").GetString());
        Assert.Equal("info", tool.GetProperty("level").GetString());
        Assert.Equal("view_file (120 ms)", tool.GetProperty("message").GetString());
        Assert.Equal("view_file", tool.GetProperty("data").GetProperty("tool").GetString());

        var retry = events.First(e => e.Data.GetProperty("type").GetString() == "step.started"
            && e.Data.GetProperty("data").GetProperty("attempt").GetInt32() == 2).Data;
        Assert.Equal("warning", retry.GetProperty("level").GetString());
    }

    /// <summary>
    /// El consumo LLM de la ejecución (auditoría §4): <c>run.completed</c> lo trae en <c>data.usage</c>, en total y por paso,
    /// y queda también en <c>reports/_run_meta.json</c>. Sin precios configurados, el coste es null y no 0.
    /// </summary>
    [Fact]
    public async Task Run_completed_and_the_run_meta_file_carry_the_llm_usage()
    {
        var client = await ClientWithWorkspaceAsync();
        var runId = await client.StartRunAsync(new { reports = new[] { "smells", "di" } });
        await client.WaitForRunEndAsync();

        using var response = await client.GetSseAsync($"/api/analysis/{runId}/events?after=0");
        var events = await response.ReadAllSseAsync();
        var usage = events[^1].Data.GetProperty("data").GetProperty("usage");

        Assert.Equal(1, usage.GetProperty("calls").GetInt32());
        Assert.Equal(800, usage.GetProperty("promptTokens").GetInt64());
        Assert.Equal(100, usage.GetProperty("completionTokens").GetInt64());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, usage.GetProperty("costUsd").ValueKind);
        Assert.Equal(1, usage.GetProperty("unpricedCalls").GetInt32());
        var smells = Assert.Single(usage.GetProperty("bySource").EnumerateArray());
        Assert.Equal(("Auditor_CodeSmells", "CodeSmells"), (smells.GetProperty("source").GetString(), smells.GetProperty("report").GetString()));

        var llm = events.First(e => e.Data.GetProperty("type").GetString() == "llm.response").Data.GetProperty("data");
        Assert.Equal(800, llm.GetProperty("promptTokens").GetInt32());

        using var meta = System.Text.Json.JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(_repo.Path, "reports", CodebaseAnalysis.Core.Reports.RunMetaFile.FileName)));
        Assert.Equal(runId, meta.RootElement.GetProperty("runId").GetString());
        Assert.Equal("analysis", meta.RootElement.GetProperty("kind").GetString());
        Assert.Equal("succeeded", meta.RootElement.GetProperty("status").GetString());
        Assert.Equal(800, meta.RootElement.GetProperty("usage").GetProperty("promptTokens").GetInt64());
    }

    [Fact]
    public async Task After_and_Last_Event_ID_return_only_later_events()
    {
        var client = await ClientWithWorkspaceAsync();
        var runId = await client.StartRunAsync();
        await client.WaitForRunEndAsync();

        using var byQuery = await client.GetSseAsync($"/api/analysis/{runId}/events?after=5");
        using var byHeader = await client.GetSseAsync($"/api/analysis/{runId}/events", r => r.Headers.Add("Last-Event-ID", "7"));
        using var both = await client.GetSseAsync($"/api/analysis/{runId}/events?after=30", r => r.Headers.Add("Last-Event-ID", "2"));

        var total = FakeAnalysisWorkflow.ExpectedTypes.Length;
        Assert.Equal(Enumerable.Range(6, total - 5), (await byQuery.ReadAllSseAsync()).Select(e => int.Parse(e.Id!)));
        Assert.Equal(Enumerable.Range(8, total - 7), (await byHeader.ReadAllSseAsync()).Select(e => int.Parse(e.Id!)));
        Assert.Equal(Enumerable.Range(31, total - 30), (await both.ReadAllSseAsync()).Select(e => int.Parse(e.Id!)));
    }

    [Fact]
    public async Task A_client_that_connects_midway_sees_no_gaps_or_duplicates()
    {
        _factory.Workflow.HoldMidway = true;
        var client = await ClientWithWorkspaceAsync();
        var runId = await client.StartRunAsync();
        await _factory.Workflow.Held.WaitAsync(TimeSpan.FromSeconds(30));

        // Uno conectado desde el principio de la pausa y otro que reanuda a mitad con after.
        using var early = await client.GetSseAsync($"/api/analysis/{runId}/events?after=0");
        using var resumed = await client.GetSseAsync($"/api/analysis/{runId}/events?after=10");
        var earlyTask = early.ReadAllSseAsync();
        var resumedTask = resumed.ReadAllSseAsync();

        _factory.Workflow.Release();
        var earlyEvents = await earlyTask;
        var resumedEvents = await resumedTask;

        var total = FakeAnalysisWorkflow.ExpectedTypes.Length;
        Assert.Equal(Enumerable.Range(1, total), earlyEvents.Select(e => int.Parse(e.Id!)));
        Assert.Equal(Enumerable.Range(11, total - 10), resumedEvents.Select(e => int.Parse(e.Id!)));
        Assert.Equal("run.completed", earlyEvents[^1].Data.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Cancel_ends_the_run_as_cancelled()
    {
        _factory.Workflow.HoldMidway = true;
        var client = await ClientWithWorkspaceAsync();
        var runId = await client.StartRunAsync();
        await _factory.Workflow.Held.WaitAsync(TimeSpan.FromSeconds(30));
        using var stream = await client.GetSseAsync($"/api/analysis/{runId}/events?after=0");
        var streamTask = stream.ReadAllSseAsync();

        var cancel = await client.PostAsync($"/api/analysis/{runId}/cancel", Http.JsonBody(new { }));

        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        var final = await client.WaitForRunEndAsync();
        Assert.Equal("cancelled", final.GetProperty("status").GetString());
        var events = await streamTask;
        var completed = events[^1].Data;
        Assert.Equal("run.completed", completed.GetProperty("type").GetString());
        Assert.Equal("cancelled", completed.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(1, completed.GetProperty("data").GetProperty("exitCode").GetInt32());

        var again = await client.PostAsync($"/api/analysis/{runId}/cancel", Http.JsonBody(new { }));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("not_running", (await again.ReadJsonAsync()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Unknown_runs_are_404()
    {
        var client = await ClientWithWorkspaceAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/analysis/nope/events")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/analysis/nope/cancel", Http.JsonBody(new { }))).StatusCode);
    }

    [Fact]
    public async Task The_run_log_file_receives_lines_logged_inside_parallel_branches()
    {
        var client = await ClientWithWorkspaceAsync();
        await client.StartRunAsync();
        await client.WaitForRunEndAsync();

        var logFile = Assert.Single(Directory.GetFiles(Path.Combine(_repo.Path, "logs"), "analysis-pipeline-*.log"));
        string content;
        using (var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(fs))
        {
            content = await reader.ReadToEndAsync();
        }

        Assert.Contains("Rama paralela 1 del workflow falso", content);
        Assert.Contains("Rama paralela 2 del workflow falso", content);
    }
}
