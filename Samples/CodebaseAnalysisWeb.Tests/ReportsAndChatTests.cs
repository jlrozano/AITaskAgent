using System.Net;
using System.Text.Json;

namespace CodebaseAnalysisWeb.Tests;

public sealed class ReportsEndpointsTests : IDisposable
{
    private readonly WebTestFactory _factory = new();
    private readonly TempDirectory _repo = new();

    public void Dispose()
    {
        _factory.Dispose();
        _repo.Dispose();
    }

    [Fact]
    public async Task Without_workspace_the_listing_is_409()
    {
        var response = await _factory.CreateClient().GetAsync("/api/reports");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("no_workspace", (await response.ReadJsonAsync()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_listing_reads_health_from_analysis_meta_and_ignores_foreign_files()
    {
        var reports = Path.Combine(_repo.Path, "reports");
        Directory.CreateDirectory(reports);
        File.WriteAllText(Path.Combine(reports, "05_errores_y_bugs.md"), "# E\n<!-- analysis-meta: health=Red; critical=6 -->");
        File.WriteAllText(Path.Combine(reports, "00_resumen_ejecutivo.md"), "# R\n<!-- analysis-meta: health=Amber -->");
        File.WriteAllText(Path.Combine(reports, "NOTAS.md"), "ajeno");
        var client = _factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);

        var list = await (await client.GetAsync("/api/reports")).ReadJsonAsync();

        Assert.Equal(2, list.GetArrayLength());
        var executive = list[0];
        Assert.Equal("00_resumen_ejecutivo.md", executive.GetProperty("fileName").GetString());
        Assert.Equal(JsonValueKind.Null, executive.GetProperty("reportType").ValueKind);
        Assert.Equal("Amber", executive.GetProperty("health").GetString());
        Assert.Equal(JsonValueKind.Null, executive.GetProperty("criticalFindings").ValueKind);
        var errors = list[1];
        Assert.Equal("ErroresYBugs", errors.GetProperty("reportType").GetString());
        Assert.Equal("Errores y bugs", errors.GetProperty("title").GetString());
        Assert.Equal("Red", errors.GetProperty("health").GetString());
        Assert.Equal(6, errors.GetProperty("criticalFindings").GetInt32());
        Assert.True(errors.GetProperty("sizeBytes").GetInt64() > 0);
        Assert.True(errors.TryGetProperty("lastModified", out _));
        Assert.Equal(2, (await (await client.GetAsync("/api/workspace")).ReadJsonAsync()).GetProperty("reportCount").GetInt32());
    }

    [Fact]
    public async Task Content_is_served_as_markdown_and_names_outside_the_catalog_are_404()
    {
        var reports = Path.Combine(_repo.Path, "reports");
        Directory.CreateDirectory(reports);
        File.WriteAllText(Path.Combine(reports, "05_errores_y_bugs.md"), "# Errores ñ");
        File.WriteAllText(Path.Combine(reports, "NOTAS.md"), "ajeno");
        File.WriteAllText(Path.Combine(_repo.Path, "secreto.md"), "secreto");
        var client = _factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);

        var ok = await client.GetAsync("/api/reports/05_errores_y_bugs.md");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("text/markdown", ok.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", ok.Content.Headers.ContentType.CharSet);
        Assert.Equal("# Errores ñ", await ok.Content.ReadAsStringAsync());

        foreach (var name in new[] { "NOTAS.md", "..%2Fsecreto.md", "..%2F..%2Fsecreto.md", "00_resumen_ejecutivo.md", "%2E%2E%5Csecreto.md" })
        {
            var response = await client.GetAsync($"/api/reports/{name}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}

public sealed class ChatEndpointsTests : IDisposable
{
    private readonly WebTestFactory _factory = new();
    private readonly TempDirectory _repo = new();

    public void Dispose()
    {
        _factory.Workflow.Release();
        _factory.Dispose();
        _repo.Dispose();
    }

    private async Task<(HttpClient Client, string SessionId)> SessionAsync(WebTestFactory? factory = null)
    {
        var reports = Path.Combine(_repo.Path, "reports");
        Directory.CreateDirectory(reports);
        File.WriteAllText(Path.Combine(reports, "05_errores_y_bugs.md"), "# E\n<!-- analysis-meta: health=Red; critical=6 -->");

        var client = (factory ?? _factory).CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);
        var created = await client.PostAsync("/api/chat/sessions", Http.JsonBody(new { }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var session = await created.ReadJsonAsync();
        Assert.Equal(1, session.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(0, session.GetProperty("turns").GetArrayLength());
        var id = session.GetProperty("id").GetString()!;
        Assert.Equal($"/api/chat/sessions/{id}", created.Headers.Location!.OriginalString);
        return (client, id);
    }

    [Fact]
    public async Task A_turn_streams_the_full_sequence_and_is_persisted()
    {
        var (client, id) = await SessionAsync();

        using var response = await client.PostSseAsync($"/api/chat/sessions/{id}/messages", new { message = "¿Qué informes hay y cuál es el peor?" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        var events = await response.ReadAllSseAsync();

        Assert.All(events, e => Assert.Equal("chat", e.Event));
        Assert.All(events, e => Assert.Null(e.Id));
        Assert.Equal(
            ["turn.started", "delta", "delta", "tool.calls", "tool.started", "tool.completed", "delta", "delta", "delta", "turn.completed"],
            events.Select(e => e.Data.GetProperty("type").GetString()));
        Assert.Equal("t-0001", events[0].Data.GetProperty("turnId").GetString());
        Assert.Equal("Voy a mirar ", events[1].Data.GetProperty("text").GetString());
        Assert.Equal("list_reports", events[3].Data.GetProperty("calls")[0].GetProperty("name").GetString());
        Assert.Equal("list_reports", events[4].Data.GetProperty("name").GetString());
        Assert.True(events[5].Data.GetProperty("success").GetBoolean());

        var turn = events[^1].Data.GetProperty("turn");
        Assert.Equal("completed", turn.GetProperty("status").GetString());
        Assert.Equal(ScriptedStreamingLlmService.FinalAnswer, turn.GetProperty("answer").GetString());
        Assert.Equal("list_reports", turn.GetProperty("toolCalls")[0].GetProperty("name").GetString());

        // En disco, en <repo>/.codebase/chat/<id>.json, y por la API.
        Assert.True(File.Exists(Path.Combine(_repo.Path, ".codebase", "chat", id + ".json")));
        var session = await (await client.GetAsync($"/api/chat/sessions/{id}")).ReadJsonAsync();
        Assert.Equal("¿Qué informes hay y cuál es el peor?", session.GetProperty("title").GetString());
        Assert.Equal(1, session.GetProperty("turns").GetArrayLength());

        var list = await (await client.GetAsync("/api/chat/sessions")).ReadJsonAsync();
        var summary = Assert.Single(list.EnumerateArray());
        Assert.Equal(id, summary.GetProperty("id").GetString());
        Assert.Equal(1, summary.GetProperty("turnCount").GetInt32());
        Assert.True(summary.TryGetProperty("updatedAt", out _));
        Assert.True(summary.TryGetProperty("createdAt", out _));
    }

    private WebTestFactory TwoModelsFactory() => new()
    {
        ExtraConfiguration = new Dictionary<string, string?>
        {
            ["AITaskAgent:LlmProviders:Roles:Chat.b:Provider"] = "Fake",
            ["AITaskAgent:LlmProviders:Roles:Chat.b:Model"] = "fake-model-b",
            ["AITaskAgent:LlmProviders:Roles:Chat.b:Inference:UseStreaming"] = "true",
            ["CodebaseAnalysis:Chat:Models:0:Id"] = "a",
            ["CodebaseAnalysis:Chat:Models:0:Label"] = "Modelo A",
            ["CodebaseAnalysis:Chat:Models:0:Role"] = "ChatAssistant",
            ["CodebaseAnalysis:Chat:Models:0:Streaming"] = "true",
            ["CodebaseAnalysis:Chat:Models:1:Id"] = "b",
            ["CodebaseAnalysis:Chat:Models:1:Label"] = "Modelo B",
            ["CodebaseAnalysis:Chat:Models:1:Role"] = "Chat.b",
            ["CodebaseAnalysis:Chat:Models:1:Streaming"] = "false",
            ["CodebaseAnalysis:Chat:DefaultModel"] = "a"
        }
    };

    [Fact]
    public async Task The_models_endpoint_lists_the_configured_models_with_the_default()
    {
        using var factory = TwoModelsFactory();
        var client = factory.CreateClient();

        var body = await (await client.GetAsync("/api/chat/models")).ReadJsonAsync();

        Assert.Equal("a", body.GetProperty("defaultId").GetString());
        var models = body.GetProperty("models").EnumerateArray().ToList();
        Assert.Equal(["a", "b"], models.Select(m => m.GetProperty("id").GetString()));
        Assert.Equal("Modelo B", models[1].GetProperty("label").GetString());
        Assert.Equal("fake-model-b", models[1].GetProperty("model").GetString());
        Assert.True(models[0].GetProperty("streaming").GetBoolean());
        Assert.False(models[1].GetProperty("streaming").GetBoolean());
        Assert.True(models[1].TryGetProperty("provider", out _));
    }

    [Fact]
    public async Task Without_a_models_list_the_models_endpoint_offers_the_chat_role_only()
    {
        var body = await (await _factory.CreateClient().GetAsync("/api/chat/models")).ReadJsonAsync();

        Assert.Equal("fake-model", body.GetProperty("defaultId").GetString());
        Assert.Equal(1, body.GetProperty("models").GetArrayLength());
    }

    [Fact]
    public async Task A_new_session_gets_the_default_model_and_it_can_be_changed_between_turns()
    {
        using var factory = TwoModelsFactory();
        var (client, id) = await SessionAsync(factory);
        var session = await (await client.GetAsync($"/api/chat/sessions/{id}")).ReadJsonAsync();
        Assert.Equal("a", session.GetProperty("modelId").GetString());

        using (var first = await client.PostSseAsync($"/api/chat/sessions/{id}/messages", new { message = "uno" }))
        {
            var turn = (await first.ReadAllSseAsync())[^1].Data.GetProperty("turn");
            Assert.Equal("a", turn.GetProperty("modelId").GetString());
            Assert.Equal("fake-model", turn.GetProperty("model").GetString());
        }

        var put = await client.PutAsync($"/api/chat/sessions/{id}/model", Http.JsonBody(new { modelId = "b" }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("b", (await put.ReadJsonAsync()).GetProperty("modelId").GetString());

        using (var second = await client.PostSseAsync($"/api/chat/sessions/{id}/messages", new { message = "dos" }))
        {
            var turn = (await second.ReadAllSseAsync())[^1].Data.GetProperty("turn");
            Assert.Equal("b", turn.GetProperty("modelId").GetString());
            Assert.Equal("fake-model-b", turn.GetProperty("model").GetString());
        }

        // El historial se conserva y el modelo elegido queda en disco.
        var persisted = await (await client.GetAsync($"/api/chat/sessions/{id}")).ReadJsonAsync();
        Assert.Equal("b", persisted.GetProperty("modelId").GetString());
        Assert.Equal(["a", "b"], persisted.GetProperty("turns").EnumerateArray().Select(t => t.GetProperty("modelId").GetString()));
    }

    [Fact]
    public async Task Choosing_an_unknown_model_is_400_and_an_unknown_session_404()
    {
        using var factory = TwoModelsFactory();
        var (client, id) = await SessionAsync(factory);

        var unknown = await client.PutAsync($"/api/chat/sessions/{id}/model", Http.JsonBody(new { modelId = "nope" }));
        var missing = await client.PutAsync("/api/chat/sessions/20260101-000000-zzzz/model", Http.JsonBody(new { modelId = "b" }));
        var created = await client.PostAsync("/api/chat/sessions", Http.JsonBody(new { modelId = "nope" }));

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("invalid_request", (await unknown.ReadJsonAsync()).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Equal("a", (await (await client.GetAsync($"/api/chat/sessions/{id}")).ReadJsonAsync()).GetProperty("modelId").GetString());
    }

    [Fact]
    public async Task A_session_with_a_retired_model_falls_back_to_the_default_instead_of_failing()
    {
        using var factory = TwoModelsFactory();
        var (client, id) = await SessionAsync(factory);
        var file = Path.Combine(_repo.Path, ".codebase", "chat", id + ".json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"modelId\": \"a\"", "\"modelId\": \"retirado\""));

        using var response = await client.PostSseAsync($"/api/chat/sessions/{id}/messages", new { message = "hola" });
        var turn = (await response.ReadAllSseAsync())[^1].Data.GetProperty("turn");

        Assert.Equal("completed", turn.GetProperty("status").GetString());
        Assert.Equal("a", turn.GetProperty("modelId").GetString());
    }

    [Fact]
    public async Task The_model_cannot_change_while_the_session_has_a_turn_running()
    {
        using var factory = new WebTestFactory { Llm = new ScriptedStreamingLlmService { HangOnFinal = true } };
        var (client, id) = await SessionAsync(factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.PostSseAsync($"/api/chat/sessions/{id}/messages", new { message = "pregunta" }, cts.Token);
        await factory.Llm.FirstFinalDeltaSent.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var put = await client.PutAsync($"/api/chat/sessions/{id}/model", Http.JsonBody(new { modelId = "fake-model" }));

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Equal("chat_turn_running", (await put.ReadJsonAsync()).GetProperty("error").GetString());
        await cts.CancelAsync();
    }

    [Fact]
    public async Task Invalid_messages_are_400_and_unknown_sessions_404()
    {
        var (client, id) = await SessionAsync();

        var empty = await client.PostAsync($"/api/chat/sessions/{id}/messages", Http.JsonBody(new { message = "   " }));
        var tooLong = await client.PostAsync($"/api/chat/sessions/{id}/messages", Http.JsonBody(new { message = new string('x', 8001) }));
        var unknown = await client.PostAsync("/api/chat/sessions/20260101-000000-zzzz/messages", Http.JsonBody(new { message = "hola" }));

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("empty_message", (await empty.ReadJsonAsync()).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("message_too_long", (await tooLong.ReadJsonAsync()).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/chat/sessions/..%2F..%2Fx")).StatusCode);
    }

    [Fact]
    public async Task Chat_is_409_while_an_analysis_runs()
    {
        _factory.Workflow.HoldMidway = true;
        var (client, id) = await SessionAsync();
        var runId = await client.StartRunAsync();
        await _factory.Workflow.Held.WaitAsync(TimeSpan.FromSeconds(30));

        var response = await client.PostAsync($"/api/chat/sessions/{id}/messages", Http.JsonBody(new { message = "hola" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal("analysis_running", body.GetProperty("error").GetString());
        Assert.Equal(runId, body.GetProperty("runId").GetString());
    }

    [Fact]
    public async Task A_client_that_aborts_leaves_the_turn_persisted_as_cancelled()
    {
        using var factory = new WebTestFactory { Llm = new ScriptedStreamingLlmService { HangOnFinal = true } };
        var (client, id) = await SessionAsync(factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var response = await client.PostSseAsync($"/api/chat/sessions/{id}/messages", new { message = "pregunta" }, cts.Token);
        var seen = new List<string>();
        await foreach (var evt in response.ReadSseAsync(cts.Token))
        {
            seen.Add(evt.Data.GetProperty("type").GetString()!);
            if (seen.Count(t => t == "delta") == 3)
                break;
        }
        await factory.Llm.FirstFinalDeltaSent.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cts.CancelAsync();
        response.Dispose();

        // El servidor ve RequestAborted, cancela el turno y lo guarda.
        JsonElement turn = default;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var session = await (await client.GetAsync($"/api/chat/sessions/{id}")).ReadJsonAsync();
            if (session.GetProperty("turns").GetArrayLength() == 1)
            {
                turn = session.GetProperty("turns")[0];
                break;
            }
            await Task.Delay(50);
        }

        Assert.Equal(JsonValueKind.Object, turn.ValueKind);
        Assert.Equal("cancelled", turn.GetProperty("status").GetString());

        // La sesión queda libre: se puede borrar.
        var deleted = await client.DeleteAsync($"/api/chat/sessions/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/chat/sessions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/chat/sessions/{id}")).StatusCode);
    }

    [Fact]
    public async Task Sessions_without_workspace()
    {
        var client = _factory.CreateClient();

        Assert.Equal(0, (await (await client.GetAsync("/api/chat/sessions")).ReadJsonAsync()).GetArrayLength());
        var create = await client.PostAsync("/api/chat/sessions", Http.JsonBody(new { title = "x" }));
        Assert.Equal(HttpStatusCode.Conflict, create.StatusCode);
        Assert.Equal("no_workspace", (await create.ReadJsonAsync()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Create_with_title_keeps_it()
    {
        var client = _factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);

        var session = await (await client.PostAsync("/api/chat/sessions", Http.JsonBody(new { title = "Mi sesión" }))).ReadJsonAsync();

        Assert.Equal("Mi sesión", session.GetProperty("title").GetString());
        Assert.Equal(_repo.Path, session.GetProperty("repositoryPath").GetString());
    }
}
