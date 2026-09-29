using CodebaseAnalysisWeb.Analysis;
using CodebaseAnalysisWeb.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace CodebaseAnalysisWeb.Tests;

/// <summary>Correcciones posteriores a la auditoría y a la verificación de extremo a extremo (backend).</summary>
public sealed class PostAuditTests : IDisposable
{
    private readonly WebTestFactory _factory = new();
    private readonly TempDirectory _repo = new();

    public void Dispose()
    {
        _factory.Workflow.Release();
        _factory.Dispose();
        _repo.Dispose();
    }

    private async Task<(HttpClient Client, string SessionId)> SessionAsync(WebTestFactory factory)
    {
        var client = factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);
        var session = await (await client.PostAsync("/api/chat/sessions", Http.JsonBody(new { }))).ReadJsonAsync();
        return (client, session.GetProperty("id").GetString()!);
    }

    /// <summary>Abre un turno que se queda colgado tras el primer delta de la respuesta final.</summary>
    private static async Task<HttpResponseMessage> HangingTurnAsync(WebTestFactory factory, HttpClient client, string sessionId, CancellationToken ct)
    {
        var response = await client.PostSseAsync($"/api/chat/sessions/{sessionId}/messages", new { message = "pregunta" }, ct);
        await factory.Llm.FirstFinalDeltaSent.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        return response;
    }

    private static async Task<JsonElement> WaitForSingleTurnAsync(HttpClient client, string sessionId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var session = await (await client.GetAsync($"/api/chat/sessions/{sessionId}")).ReadJsonAsync();
            if (session.GetProperty("turns").GetArrayLength() == 1)
                return session.GetProperty("turns")[0];
            await Task.Delay(50);
        }
        throw new TimeoutException("El turno no se guardó a tiempo.");
    }

    // P1
    [Fact]
    public async Task Starting_an_analysis_while_a_chat_turn_runs_is_409_chat_turn_running()
    {
        using var factory = new WebTestFactory { Llm = new ScriptedStreamingLlmService { HangOnFinal = true } };
        var (client, id) = await SessionAsync(factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var turn = await HangingTurnAsync(factory, client, id, cts.Token);

        var response = await client.PostAsync("/api/analysis", Http.JsonBody(new { }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("chat_turn_running", (await response.ReadJsonAsync()).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/analysis/current")).StatusCode);

        await cts.CancelAsync();
        turn.Dispose();
        await WaitForSingleTurnAsync(client, id);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/analysis", Http.JsonBody(new { }))).StatusCode);
        await client.WaitForRunEndAsync();
    }

    // P2
    [Fact]
    public async Task Application_stopping_cancels_the_running_analysis_and_closes_its_stream()
    {
        _factory.Workflow.HoldMidway = true;
        var client = _factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);
        var runId = await client.StartRunAsync();
        await _factory.Workflow.Held.WaitAsync(TimeSpan.FromSeconds(30));
        using var stream = await client.GetSseAsync($"/api/analysis/{runId}/events?after=0");
        var streamTask = stream.ReadAllSseAsync();

        var manager = _factory.Services.GetRequiredService<AnalysisRunManager>();

        // El host se detiene de verdad: el SSE tiene que cerrarse con run.completed, no por timeout.
        _factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        // Cancelado ya al volver de StopApplication, en el aviso de ApplicationStopping: no espera a
        // StopAsync, que en Kestrel llega después de esperar a las peticiones abiertas (este mismo SSE).
        Assert.True(manager.Last!.Cancellation.IsCancellationRequested);

        await _factory.Workflow.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await manager.Last!.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("cancelled", manager.Last.ToDto().Status);
        var events = await streamTask;
        var completed = events[^1].Data;
        Assert.Equal("run.completed", completed.GetProperty("type").GetString());
        Assert.Equal("cancelled", completed.GetProperty("data").GetProperty("status").GetString());
    }

    // P3-4
    [Fact]
    public async Task Run_completed_is_stamped_after_the_run_is_finished()
    {
        // Cada GetUtcNow devuelve un instante distinto y apunta si la ejecución seguía en curso: el Ts de
        // run.completed dice en qué estado estaba la ejecución cuando se construyó el evento.
        var clock = new ObservingTimeProvider();
        using var factory = new WebTestFactory { ConfigureServices = s => s.AddSingleton<TimeProvider>(clock) };
        var client = factory.CreateClient();
        clock.Probe = () => factory.Services.GetRequiredService<AnalysisRunManager>().Last?.IsRunning;
        await client.OpenWorkspaceAsync(_repo.Path);
        var runId = await client.StartRunAsync();
        await client.WaitForRunEndAsync();

        using var stream = await client.GetSseAsync($"/api/analysis/{runId}/events?after=0");
        var completed = (await stream.ReadAllSseAsync())[^1].Data;

        Assert.Equal("run.completed", completed.GetProperty("type").GetString());
        Assert.False(clock.RunningAt(completed.GetProperty("ts").GetDateTimeOffset()));
    }

    private sealed class ObservingTimeProvider : TimeProvider
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<DateTimeOffset, bool?> _observed = [];
        private DateTimeOffset _now = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

        public Func<bool?>? Probe { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            // La sonda toma el cerrojo común de la web: fuera del de este reloj, para no cruzarlos.
            var running = Probe?.Invoke();
            lock (_lock)
            {
                _now = _now.AddMilliseconds(1);
                _observed[_now] = running;
                return _now;
            }
        }

        public bool? RunningAt(DateTimeOffset ts)
        {
            lock (_lock)
            {
                return _observed.TryGetValue(ts, out var running) ? running : null;
            }
        }
    }

    // P3-4
    [Fact]
    public async Task A_workflow_that_throws_ends_the_run_as_failed_with_run_completed()
    {
        _factory.Workflow.ThrowMidway = new InvalidOperationException("fallo del workflow");
        var client = _factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);
        var runId = await client.StartRunAsync();

        var final = await client.WaitForRunEndAsync();

        Assert.Equal("failed", final.GetProperty("status").GetString());
        Assert.Equal("fallo del workflow", final.GetProperty("error").GetString());
        using var stream = await client.GetSseAsync($"/api/analysis/{runId}/events?after=0");
        var events = await stream.ReadAllSseAsync();
        Assert.Equal("run.completed", events[^1].Data.GetProperty("type").GetString());
    }

    // P3-7
    [Fact]
    public async Task Deleting_a_session_while_its_turn_runs_is_409()
    {
        using var factory = new WebTestFactory { Llm = new ScriptedStreamingLlmService { HangOnFinal = true } };
        var (client, id) = await SessionAsync(factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var turn = await HangingTurnAsync(factory, client, id, cts.Token);

        var deleted = await client.DeleteAsync($"/api/chat/sessions/{id}");

        Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.Equal("chat_turn_running", (await deleted.ReadJsonAsync()).GetProperty("error").GetString());
        await cts.CancelAsync();
        turn.Dispose();
        await WaitForSingleTurnAsync(client, id);
    }

    // P3-8
    [Fact]
    public async Task A_corrupt_session_file_is_404_with_a_clear_message_and_can_be_deleted()
    {
        var (client, id) = await SessionAsync(_factory);
        File.WriteAllText(Path.Combine(_repo.Path, ".codebase", "chat", id + ".json"), "{ esto no es JSON");

        var get = await client.GetAsync($"/api/chat/sessions/{id}");
        var message = await client.PostAsync($"/api/chat/sessions/{id}/messages", Http.JsonBody(new { message = "hola" }));
        var list = await (await client.GetAsync("/api/chat/sessions")).ReadJsonAsync();

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        var body = await get.ReadJsonAsync();
        Assert.Equal("not_found", body.GetProperty("error").GetString());
        Assert.Contains("dañado", body.GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.NotFound, message.StatusCode);
        Assert.Equal(0, list.GetArrayLength());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/chat/sessions/{id}")).StatusCode);
    }

    // P3-9
    [Fact]
    public async Task A_report_that_cannot_be_read_is_503_with_the_api_error_body()
    {
        var reports = Path.Combine(_repo.Path, "reports");
        Directory.CreateDirectory(reports);
        var path = Path.Combine(reports, "05_errores_y_bugs.md");
        File.WriteAllText(path, "# Errores");
        var client = _factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);

        HttpResponseMessage response;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            response = await client.GetAsync("/api/reports/05_errores_y_bugs.md");
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal("report_unavailable", body.GetProperty("error").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("message").GetString()));
    }

    // P3-10
    [Fact]
    public async Task The_ipv6_loopback_host_is_allowed()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/meta");
        request.Headers.Host = "[::1]:5180";

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // P3-10
    [Theory]
    [InlineData("http://127.0.0.1:5000", true)]
    [InlineData("http://[::1]:5000", true)]
    [InlineData("http://localhost:5000", true)]
    [InlineData("http://0.0.0.0:5000", false)]
    [InlineData("http://*:5000", false)]
    [InlineData("", false)]
    public void Kestrel_endpoints_must_be_loopback(string url, bool accepted)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Urls"] = "http://127.0.0.1:5180",
            ["Kestrel:Endpoints:Http:Url"] = url
        }).Build();

        var error = Record.Exception(() => WebSecurity.EnsureLoopbackUrls(configuration));

        if (accepted)
            Assert.Null(error);
        else
            Assert.Contains("Kestrel", Assert.IsType<InvalidOperationException>(error).Message);
    }

    // P3-10
    [Fact]
    public void Loopback_kestrel_endpoints_make_http_ports_irrelevant()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HTTP_PORTS"] = "8080",
            ["Kestrel:Endpoints:Http:Url"] = "http://127.0.0.1:5180"
        }).Build();

        WebSecurity.EnsureLoopbackUrls(configuration);
    }

    // P3-11
    [Fact]
    public async Task Analysing_another_repository_releases_the_previous_run_log()
    {
        using var other = new TempDirectory();
        var client = _factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);
        await client.StartRunAsync();
        await client.WaitForRunEndAsync();
        await client.OpenWorkspaceAsync(other.Path);
        await client.StartRunAsync();
        await client.WaitForRunEndAsync();

        var firstLog = Assert.Single(Directory.GetFiles(Path.Combine(_repo.Path, "logs"), "analysis-pipeline-*.log"));

        // Con el fichero aún abierto por Serilog, el borrado falla en Windows.
        File.Delete(firstLog);
        Assert.False(File.Exists(firstLog));
        Assert.Single(Directory.GetFiles(Path.Combine(other.Path, "logs"), "analysis-pipeline-*.log"));
    }

    // D5
    [Fact]
    public async Task Opening_a_directory_without_write_permission_is_400_workspace_not_writable()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = new DirectoryInfo(_repo.Path);
        var rule = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateDirectories | FileSystemRights.CreateFiles | FileSystemRights.AppendData | FileSystemRights.WriteData,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Deny);
        var security = directory.GetAccessControl();
        security.AddAccessRule(rule);
        directory.SetAccessControl(security);
        try
        {
            var client = _factory.CreateClient();

            var response = await client.PutAsync("/api/workspace", Http.JsonBody(new { repositoryPath = _repo.Path }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.ReadJsonAsync();
            Assert.Equal("workspace_not_writable", body.GetProperty("error").GetString());
            Assert.Contains(_repo.Path, body.GetProperty("message").GetString());
            var ws = await (await client.GetAsync("/api/workspace")).ReadJsonAsync();
            Assert.Equal(JsonValueKind.Null, ws.GetProperty("repositoryPath").ValueKind);
        }
        finally
        {
            security = directory.GetAccessControl();
            security.RemoveAccessRule(rule);
            directory.SetAccessControl(security);
        }
    }

    // D6
    [Theory]
    [InlineData("POST", "/api/analysis", "{")]
    [InlineData("PUT", "/api/workspace", "{\"repositoryPath\": 5}")]
    [InlineData("POST", "/api/chat/sessions", "no es json")]
    public async Task A_body_that_is_not_valid_json_is_400_with_the_api_error_body(string method, string url, string json)
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.ReadJsonAsync();
        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("message").GetString()));
    }

    // D6
    [Fact]
    public async Task A_non_json_content_type_keeps_415_and_gets_the_api_error_body()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/analysis", new StringContent("{}", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("invalid_request", (await response.ReadJsonAsync()).GetProperty("error").GetString());
    }

    // Último workspace
    [Fact]
    public async Task The_last_workspace_is_reopened_after_a_restart()
    {
        var settings = Path.Combine(_repo.Path, "..", Guid.NewGuid().ToString("N") + "-settings.json");
        using (var first = new WebTestFactory { SettingsPath = settings })
        {
            await first.CreateClient().OpenWorkspaceAsync(_repo.Path);
        }

        using var second = new WebTestFactory { SettingsPath = settings };
        var ws = await (await second.CreateClient().GetAsync("/api/workspace")).ReadJsonAsync();

        Assert.Equal(_repo.Path, ws.GetProperty("repositoryPath").GetString());
        Assert.Equal(_repo.Path, AITaskAgent.FileTools.FileToolsConfiguration.RootDirectory);
        File.Delete(settings);
    }

    // Último workspace
    [Theory]
    [InlineData("{\"lastRepositoryPath\":\"C:\\\\no\\\\existe\\\\en\\\\ningun\\\\sitio-9f2c\"}")]
    [InlineData("{ roto")]
    public async Task A_missing_or_unreadable_last_workspace_starts_without_workspace(string content)
    {
        var settings = Path.Combine(_repo.Path, "settings.json");
        File.WriteAllText(settings, content);
        using var factory = new WebTestFactory { SettingsPath = settings };

        var ws = await (await factory.CreateClient().GetAsync("/api/workspace")).ReadJsonAsync();

        Assert.Equal(JsonValueKind.Null, ws.GetProperty("repositoryPath").ValueKind);
    }
}
