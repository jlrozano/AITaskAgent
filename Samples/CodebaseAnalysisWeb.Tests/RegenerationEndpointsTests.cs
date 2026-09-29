using CodebaseAnalysis.Core.Models;
using System.Net;
using System.Text.Json;

namespace CodebaseAnalysisWeb.Tests;

/// <summary>
/// <c>POST /api/analysis/{runId}/regenerate</c>: la ejecución terminada vuelve a correr con el mismo <c>runId</c>, sigue el
/// mismo log de eventos con <c>seq</c> continuo y cierra con otro <c>run.completed</c> con <c>failedReports</c> recalculado.
/// </summary>
public sealed class RegenerationEndpointsTests : IDisposable
{
    private const string SmellsFile = "02_code_smells.md";
    private const string DiFile = "09_inyeccion_dependencias.md";

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

    private static Task<HttpResponseMessage> RegenerateAsync(HttpClient client, string runId, object body) =>
        client.PostAsync($"/api/analysis/{runId}/regenerate", Http.JsonBody(body));

    private static List<string> FailedReports(JsonElement status) =>
        status.GetProperty("failedReports").EnumerateArray().Select(e => e.GetString()!).ToList();

    private static string TypeOf(SseEvent e) => e.Data.GetProperty("type").GetString()!;

    /// <summary>Ejecución completa con esos auditores fallando; devuelve su runId ya terminada.</summary>
    private async Task<string> FinishedRunAsync(HttpClient client, params ReportType[] failing)
    {
        foreach (var report in failing)
            _factory.Workflow.FailingReports.Add(report);
        var runId = await client.StartRunAsync(new { reports = new[] { "smells", "di" } });
        await client.WaitForRunEndAsync();
        _factory.Workflow.FailingReports.Clear();
        return runId;
    }

    [Fact]
    public async Task An_unknown_run_is_404()
    {
        var client = await ClientWithWorkspaceAsync();

        var response = await RegenerateAsync(client, "nope", new { reports = new[] { "smells" } });

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

        var response = await RegenerateAsync(client, runId, new { reports = report == null ? Array.Empty<string>() : new[] { report } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await response.ReadJsonAsync()).GetProperty("error").GetString());
        Assert.Empty(_factory.Workflow.Regenerations);
        Assert.Equal("succeeded", (await client.GetCurrentRunAsync())!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_run_of_another_repository_is_409_and_regenerates_again_once_its_repository_is_open()
    {
        var client = await ClientWithWorkspaceAsync();
        var runId = await FinishedRunAsync(client);
        using var other = new TempDirectory();
        await client.OpenWorkspaceAsync(other.Path);

        var response = await RegenerateAsync(client, runId, new { reports = new[] { "smells" } });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal("run_from_other_workspace", body.GetProperty("error").GetString());
        Assert.Equal(runId, body.GetProperty("runId").GetString());
        Assert.Contains(_repo.Path, body.GetProperty("message").GetString());
        Assert.Empty(_factory.Workflow.Regenerations);
        Assert.Equal("succeeded", (await client.GetCurrentRunAsync())!.Value.GetProperty("status").GetString());

        // Con su repositorio abierto otra vez (la ruta con otra caja y barra final), la misma ejecución se regenera.
        await client.OpenWorkspaceAsync(_repo.Path.ToUpperInvariant() + Path.DirectorySeparatorChar);
        Assert.Equal(HttpStatusCode.Accepted, (await RegenerateAsync(client, runId, new { reports = new[] { "smells" } })).StatusCode);
        await client.WaitForRunEndAsync();
        Assert.Single(_factory.Workflow.Regenerations);
    }

    [Fact]
    public async Task While_the_run_or_its_regeneration_is_running_it_is_409()
    {
        _factory.Workflow.HoldMidway = true;
        var client = await ClientWithWorkspaceAsync();
        var runId = await client.StartRunAsync();
        await _factory.Workflow.Held.WaitAsync(TimeSpan.FromSeconds(30));

        var during = await RegenerateAsync(client, runId, new { reports = new[] { "smells" } });

        Assert.Equal(HttpStatusCode.Conflict, during.StatusCode);
        var body = await during.ReadJsonAsync();
        Assert.Equal("analysis_running", body.GetProperty("error").GetString());
        Assert.Equal(runId, body.GetProperty("runId").GetString());

        // Durante la regeneración, ni otra regeneración ni un análisis nuevo.
        _factory.Workflow.Release();
        await client.WaitForRunEndAsync();
        _factory.Workflow.Reset();
        Assert.Equal(HttpStatusCode.Accepted, (await RegenerateAsync(client, runId, new { reports = new[] { "smells" } })).StatusCode);
        await _factory.Workflow.Held.WaitAsync(TimeSpan.FromSeconds(30));

        var again = await RegenerateAsync(client, runId, new { reports = new[] { "di" } });
        var start = await client.PostAsync("/api/analysis", Http.JsonBody(new { }));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("analysis_running", (await again.ReadJsonAsync()).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
        Assert.Single(_factory.Workflow.Regenerations);
    }

    [Fact]
    public async Task The_regeneration_continues_the_same_run_and_log_and_ends_with_a_second_run_completed()
    {
        var client = await ClientWithWorkspaceAsync();
        var runId = await FinishedRunAsync(client, ReportType.InyeccionDependenciasExtensiones);
        var first = (await client.GetCurrentRunAsync())!.Value;
        Assert.Equal("completed_with_failures", first.GetProperty("status").GetString());
        Assert.Equal([DiFile], FailedReports(first));
        var firstLastSeq = first.GetProperty("lastSeq").GetInt32();

        var response = await RegenerateAsync(client, runId, new { reports = new[] { "di" }, synthesis = true });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.ReadJsonAsync();
        Assert.Equal(runId, accepted.GetProperty("runId").GetString());
        Assert.Equal("running", accepted.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, accepted.GetProperty("finishedAt").ValueKind);

        var final = await client.WaitForRunEndAsync();
        Assert.Equal(runId, final.GetProperty("runId").GetString());
        Assert.Equal("succeeded", final.GetProperty("status").GetString());
        Assert.Empty(FailedReports(final));
        // La lista original de la ejecución no cambia: la regeneración no es otra ejecución.
        Assert.Equal(["CodeSmells", "InyeccionDependenciasExtensiones"],
            final.GetProperty("requestedReports").EnumerateArray().Select(e => e.GetString()));
        var (reports, synthesis) = Assert.Single(_factory.Workflow.Regenerations);
        Assert.Equal([ReportType.InyeccionDependenciasExtensiones], reports);
        Assert.True(synthesis);
        Assert.True(File.Exists(Path.Combine(_repo.Path, "reports", DiFile)));

        using var stream = await client.GetSseAsync($"/api/analysis/{runId}/events?after=0");
        var events = await stream.ReadAllSseAsync();

        Assert.Equal(Enumerable.Range(1, events.Count), events.Select(e => e.Data.GetProperty("seq").GetInt32()));
        Assert.Equal(final.GetProperty("lastSeq").GetInt32(), events.Count);
        Assert.All(events, e => Assert.Equal(runId, e.Data.GetProperty("runId").GetString()));
        Assert.Equal(2, events.Count(e => TypeOf(e) == "run.completed"));
        Assert.Equal("run.completed", TypeOf(events[firstLastSeq - 1]));
        Assert.Equal(
            [
                "run.regeneration_started", "pipeline.started",
                "step.started", "step.completed", "step.started", "step.completed",
                "step.started", "branch.started", "step.started",
                "report.written", "step.completed", "branch.completed", "step.completed",
                "step.started", "report.written", "step.completed",
                "pipeline.completed", "run.completed"
            ],
            events.Skip(firstLastSeq).Select(TypeOf));

        var regeneration = events[firstLastSeq].Data;
        Assert.Equal(["InyeccionDependenciasExtensiones"],
            regeneration.GetProperty("data").GetProperty("reports").EnumerateArray().Select(e => e.GetString()));
        Assert.True(regeneration.GetProperty("data").GetProperty("synthesis").GetBoolean());
        Assert.Equal("PrepareRegenerationStep", events[firstLastSeq + 2].Data.GetProperty("source").GetString());
        Assert.Equal("facets", events[firstLastSeq + 2].Data.GetProperty("phase").GetString());

        var completed = events[^1].Data;
        Assert.Equal("succeeded", completed.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(0, completed.GetProperty("data").GetProperty("failedReports").GetArrayLength());
        Assert.Equal("Regeneración completada.", completed.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_client_that_resumes_after_the_first_run_completed_receives_the_regeneration()
    {
        _factory.Workflow.HoldMidway = true;
        var client = await ClientWithWorkspaceAsync();
        var runId = await client.StartRunAsync();
        await _factory.Workflow.Held.WaitAsync(TimeSpan.FromSeconds(30));
        _factory.Workflow.Release();
        var lastSeq = (await client.WaitForRunEndAsync()).GetProperty("lastSeq").GetInt32();
        _factory.Workflow.Reset();

        Assert.Equal(HttpStatusCode.Accepted, (await RegenerateAsync(client, runId, new { reports = new[] { "smells" }, synthesis = false })).StatusCode);
        await _factory.Workflow.Held.WaitAsync(TimeSpan.FromSeconds(30));
        using var resumed = await client.GetSseAsync($"/api/analysis/{runId}/events?after={lastSeq}");
        var resumedTask = resumed.ReadAllSseAsync();
        _factory.Workflow.Release();
        var events = await resumedTask;

        Assert.Equal(lastSeq + 1, events[0].Data.GetProperty("seq").GetInt32());
        Assert.Equal("run.regeneration_started", TypeOf(events[0]));
        Assert.Equal("run.completed", TypeOf(events[^1]));
        Assert.Equal(Enumerable.Range(lastSeq + 1, events.Count), events.Select(e => e.Data.GetProperty("seq").GetInt32()));
        Assert.DoesNotContain(events, e => e.Data.GetProperty("source").ValueKind == JsonValueKind.String
            && e.Data.GetProperty("source").GetString() == "ExecutiveSynthesizerStep");
    }

    [Fact]
    public async Task FailedReports_is_the_previous_list_minus_the_regenerated_that_succeeded_plus_the_new_failures()
    {
        var client = await ClientWithWorkspaceAsync();
        var runId = await FinishedRunAsync(client, ReportType.CodeSmells, ReportType.InyeccionDependenciasExtensiones);
        Assert.Equal([SmellsFile, DiFile], FailedReports((await client.GetCurrentRunAsync())!.Value));

        // Solo CodeSmells, que ahora sale bien: DI sigue fallido de antes.
        _factory.Workflow.FailingReports.Add(ReportType.InyeccionDependenciasExtensiones);
        Assert.Equal(HttpStatusCode.Accepted, (await RegenerateAsync(client, runId, new { reports = new[] { "smells" }, synthesis = false })).StatusCode);
        var afterSmells = await client.WaitForRunEndAsync();
        Assert.Equal("completed_with_failures", afterSmells.GetProperty("status").GetString());
        Assert.Equal([DiFile], FailedReports(afterSmells));

        // DI vuelve a fallar: sigue en la lista y la ejecución termina con fallos, no como fallida.
        Assert.Equal(HttpStatusCode.Accepted, (await RegenerateAsync(client, runId, new { reports = new[] { "di" }, synthesis = true })).StatusCode);
        var afterDi = await client.WaitForRunEndAsync();
        Assert.Equal("completed_with_failures", afterDi.GetProperty("status").GetString());
        Assert.Equal([DiFile], FailedReports(afterDi));
        Assert.Equal(JsonValueKind.Null, afterDi.GetProperty("error").ValueKind);

        // Un informe que estaba bien y ahora falla entra en la lista.
        _factory.Workflow.FailingReports.Clear();
        _factory.Workflow.FailingReports.Add(ReportType.CodeSmells);
        Assert.Equal(HttpStatusCode.Accepted, (await RegenerateAsync(client, runId, new { reports = new[] { "smells", "di" }, synthesis = false })).StatusCode);
        Assert.Equal([SmellsFile], FailedReports(await client.WaitForRunEndAsync()));
    }

    /// <summary>Ejecución de CodeSmells y DI cancelada durante los informes: los dos carriles quedan sin informe.</summary>
    private async Task<string> CancelledRunAsync(HttpClient client)
    {
        _factory.Workflow.HoldMidway = true;
        var runId = await client.StartRunAsync(new { reports = new[] { "smells", "di" } });
        await _factory.Workflow.Held.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/analysis/{runId}/cancel", Http.JsonBody(new { }))).StatusCode);
        var cancelled = await client.WaitForRunEndAsync();
        Assert.Equal("cancelled", cancelled.GetProperty("status").GetString());
        Assert.Empty(FailedReports(cancelled));
        _factory.Workflow.Reset();
        return runId;
    }

    [Fact]
    public async Task After_a_cancelled_run_regenerating_one_report_leaves_the_other_in_failedReports()
    {
        var client = await ClientWithWorkspaceAsync();
        var runId = await CancelledRunAsync(client);
        _factory.Workflow.HoldMidway = false;

        // DI no se escribió en la ejecución original ni en esta regeneración: no es un «Terminado».
        Assert.Equal(HttpStatusCode.Accepted, (await RegenerateAsync(client, runId, new { reports = new[] { "smells" }, synthesis = true })).StatusCode);
        var afterSmells = await client.WaitForRunEndAsync();

        Assert.Equal("completed_with_failures", afterSmells.GetProperty("status").GetString());
        Assert.Equal([DiFile], FailedReports(afterSmells));
        using (var stream = await client.GetSseAsync($"/api/analysis/{runId}/events?after=0"))
        {
            var completed = (await stream.ReadAllSseAsync()).Last(e => TypeOf(e) == "run.completed").Data;
            Assert.Equal("warning", completed.GetProperty("level").GetString());
            Assert.Equal("completed_with_failures", completed.GetProperty("data").GetProperty("status").GetString());
            Assert.Equal([DiFile], completed.GetProperty("data").GetProperty("failedReports").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal("Regeneración completada; quedan 1 informe(s) sin generar.", completed.GetProperty("message").GetString());
        }

        // Con DI regenerado, todos los pedidos tienen su informe.
        Assert.Equal(HttpStatusCode.Accepted, (await RegenerateAsync(client, runId, new { reports = new[] { "di" }, synthesis = true })).StatusCode);
        var afterDi = await client.WaitForRunEndAsync();
        Assert.Equal("succeeded", afterDi.GetProperty("status").GetString());
        Assert.Empty(FailedReports(afterDi));
    }

    [Fact]
    public async Task A_cancelled_regeneration_lists_the_reports_never_written_and_a_later_one_works_after_a_cancelled_run()
    {
        // Ejecución cancelada durante los informes (criterio 5).
        var client = await ClientWithWorkspaceAsync();
        var runId = await CancelledRunAsync(client);

        // Una regeneración cancelada a su vez: el mensaje es el de la regeneración y failedReports lista los pedidos que
        // no tienen informe en toda la ejecución.
        Assert.Equal(HttpStatusCode.Accepted, (await RegenerateAsync(client, runId, new { reports = new[] { "di" } })).StatusCode);
        await _factory.Workflow.Held.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/analysis/{runId}/cancel", Http.JsonBody(new { }))).StatusCode);
        var cancelled = await client.WaitForRunEndAsync();
        Assert.Equal("cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal("Regeneración cancelada.", cancelled.GetProperty("error").GetString());
        Assert.Equal([SmellsFile, DiFile], FailedReports(cancelled));

        // La siguiente estrena cancelación: no hereda la ya cancelada.
        _factory.Workflow.HoldMidway = false;
        _factory.Workflow.Reset();
        Assert.Equal(HttpStatusCode.Accepted, (await RegenerateAsync(client, runId, new { reports = new[] { "smells", "di" } })).StatusCode);
        var final = await client.WaitForRunEndAsync();
        Assert.Equal("succeeded", final.GetProperty("status").GetString());
        Assert.Empty(FailedReports(final));
        Assert.Equal(runId, final.GetProperty("runId").GetString());
        Assert.True(File.Exists(Path.Combine(_repo.Path, "reports", DiFile)));
    }
}
