using AITaskAgent.FileTools;
using System.Net;

namespace CodebaseAnalysisWeb.Tests;

public sealed class WorkspaceAndHostTests : IDisposable
{
    private readonly WebTestFactory _factory = new();
    private readonly TempDirectory _repo = new();

    public void Dispose()
    {
        _factory.Workflow.Release();
        _factory.Dispose();
        _repo.Dispose();
    }

    [Fact]
    public async Task Without_workspace_the_state_is_empty()
    {
        var client = _factory.CreateClient();

        var ws = await (await client.GetAsync("/api/workspace")).ReadJsonAsync();

        Assert.Equal(System.Text.Json.JsonValueKind.Null, ws.GetProperty("repositoryPath").ValueKind);
        Assert.False(ws.GetProperty("hasGraph").GetBoolean());
        Assert.Equal(0, ws.GetProperty("reportCount").GetInt32());
        Assert.False(ws.GetProperty("analysisRunning").GetBoolean());
    }

    [Theory]
    [InlineData("relativo\\repo")]
    [InlineData("C:\\no\\existe\\en\\ningun\\sitio-9f2c")]
    [InlineData("")]
    public async Task Invalid_paths_are_400(string path)
    {
        var client = _factory.CreateClient();

        var response = await client.PutAsync("/api/workspace", Http.JsonBody(new { repositoryPath = path }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_path", (await response.ReadJsonAsync()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Opening_a_directory_creates_the_chat_folder_and_resolves_the_paths()
    {
        var client = _factory.CreateClient();

        var response = await client.PutAsync("/api/workspace", Http.JsonBody(new { repositoryPath = _repo.Path }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ws = await response.ReadJsonAsync();
        Assert.Equal(_repo.Path, ws.GetProperty("repositoryPath").GetString());
        Assert.Equal(Path.Combine(_repo.Path, ".codebase", "codebase.db"), ws.GetProperty("databasePath").GetString());
        Assert.Equal(Path.Combine(_repo.Path, "reports"), ws.GetProperty("reportsPath").GetString());
        Assert.Equal(Path.Combine(_repo.Path, ".codebase", "chat"), ws.GetProperty("chatPath").GetString());
        Assert.Equal(Path.Combine(_repo.Path, "logs"), ws.GetProperty("logsPath").GetString());
        Assert.True(Directory.Exists(Path.Combine(_repo.Path, ".codebase", "chat")));
        Assert.Equal(_repo.Path, FileToolsConfiguration.RootDirectory);
    }

    [Fact]
    public async Task Switching_repositories_does_not_widen_the_sandbox()
    {
        using var other = new TempDirectory();
        var client = _factory.CreateClient();

        await client.OpenWorkspaceAsync(_repo.Path);
        await client.OpenWorkspaceAsync(other.Path);

        Assert.True(FileToolsConfiguration.IsAllowed(Path.Combine(other.Path, "a.cs")));
        Assert.False(FileToolsConfiguration.IsAllowed(Path.Combine(_repo.Path, "a.cs")));
    }

    [Fact]
    public async Task Changing_the_workspace_while_an_analysis_runs_is_409()
    {
        using var other = new TempDirectory();
        _factory.Workflow.HoldMidway = true;
        var client = _factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);
        var runId = await client.StartRunAsync();
        await _factory.Workflow.Held.WaitAsync(TimeSpan.FromSeconds(30));

        var response = await client.PutAsync("/api/workspace", Http.JsonBody(new { repositoryPath = other.Path }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal("analysis_running", body.GetProperty("error").GetString());
        Assert.Equal(runId, body.GetProperty("runId").GetString());
        Assert.True((await (await client.GetAsync("/api/workspace")).ReadJsonAsync()).GetProperty("analysisRunning").GetBoolean());

        _factory.Workflow.Release();
        await client.WaitForRunEndAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync("/api/workspace", Http.JsonBody(new { repositoryPath = other.Path }))).StatusCode);
    }

    [Fact]
    public async Task A_foreign_host_header_is_rejected()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/meta");
        request.Headers.Host = "evil.example";

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Meta_lists_the_nine_reports()
    {
        var client = _factory.CreateClient();

        var meta = await (await client.GetAsync("/api/meta")).ReadJsonAsync();

        Assert.Equal(9, meta.GetProperty("reports").GetArrayLength());
        Assert.Equal("00_resumen_ejecutivo.md", meta.GetProperty("executiveReportFileName").GetString());
        var first = meta.GetProperty("reports")[0];
        Assert.Equal("AnalisisFuncional", first.GetProperty("type").GetString());
        Assert.Equal("funcional", first.GetProperty("alias").GetString());
        Assert.Equal(9, meta.GetProperty("defaultReports").GetArrayLength());
    }

    [Fact]
    public async Task An_unknown_api_route_is_a_json_404()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/no-existe");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", (await response.ReadJsonAsync()).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("http://127.0.0.1:5180", true)]
    [InlineData("http://localhost:5180", true)]
    [InlineData("http://[::1]:5180", true)]
    [InlineData("http://0.0.0.0:5180", false)]
    [InlineData("http://*:5180", false)]
    [InlineData("http://+:5180", false)]
    [InlineData("http://192.168.1.10:5180", false)]
    public void Only_loopback_urls_are_accepted(string url, bool expected)
    {
        Assert.Equal(expected, Hosting.WebSecurity.IsLoopbackUrl(url));
    }
}
