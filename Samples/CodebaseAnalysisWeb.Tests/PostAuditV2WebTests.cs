using CodebaseAnalysis.Core.Documents;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Knowledge.Graph;
using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tests;
using CodebaseAnalysisWeb.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodebaseAnalysisWeb.Tests;

/// <summary>Correcciones posteriores a la auditoría de V2 (plan v2, «Correcciones post-auditoría V2»), en la web.</summary>
public sealed class PostAuditV2WebTests : IDisposable
{
    private const string ErrorsReport = "# Errores y bugs\n\n## Resumen\n\nTexto.\n\n<!-- analysis-meta: health=Red; critical=6 -->\n";

    private readonly TempDirectory _repo = new();

    public void Dispose() => _repo.Dispose();

    private KnowledgePaths Paths => new(_repo.Path, Path.Combine(_repo.Path, "reports"));

    private string DatabasePath => Path.Combine(_repo.Path, ".codebase", "codebase.db");

    private async Task CreateGraphAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        await GraphSchema.EnsureAsync(DatabasePath, CancellationToken.None);
        await using var conn = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO nodes (project, label, name, qualified_name, file_path, start_line, end_line) VALUES ('Codebase', 'Class', 'PaymentValidator', 'Shop.PaymentValidator', 'src/PaymentValidator.cs', 1, 30);";
        await cmd.ExecuteNonQueryAsync();
    }

    private static HttpRequestMessage UploadRequest(string fileName, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/documents") { Content = content };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        request.Headers.Add("X-File-Name", Uri.EscapeDataString(fileName));
        return request;
    }

    // ── Punto 2: borrar un documento fuera del grafo también toma la operación de conocimiento ────────────────

    [Fact]
    public async Task Deleting_a_document_outside_the_graph_counts_as_a_knowledge_operation_but_is_allowed_with_others()
    {
        using var factory = new WebTestFactory();
        var client = factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);
        var upload = await client.SendAsync(UploadRequest("guia.md", new ByteArrayContent(SampleFiles.Markdown())));
        var id = (await upload.ReadJsonAsync()).GetProperty("id").GetString();
        var workspace = factory.Services.GetRequiredService<WorkspaceState>();

        var knowledge = factory.Services.GetRequiredService<CodebaseAnalysisWeb.Knowledge.KnowledgeService>();

        // Fila 1 de §5.5: con otra operación en curso se permite, y su propia operación cuenta mientras dura.
        var (lease, error) = knowledge.Begin(CodebaseAnalysisWeb.Knowledge.KnowledgeOperation.DeleteDocument);
        Assert.Null(error);
        using (lease)
        {
            Assert.True(workspace.AnyKnowledgeOperationRunning);
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/documents/{id}")).StatusCode);
            Assert.Equal("knowledge_operation_running",
                (await (await client.PostAsync("/api/chat/sessions/x/messages", Http.JsonBody(new { message = "hola" }))).ReadJsonAsync()).GetProperty("error").GetString());
        }
        Assert.False(workspace.AnyKnowledgeOperationRunning);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/documents/{id}")).StatusCode);
    }

    // ── Puntos 6 y 7: restaurar no da 500 si la reingesta falla, y la operación queda apuntada mientras dura ──

    [Fact]
    public async Task Restoring_with_a_failing_graph_refresh_is_not_a_500()
    {
        Directory.CreateDirectory(Paths.ReportsPath);
        File.WriteAllText(Path.Combine(Paths.ReportsPath, "05_errores_y_bugs.md"), ErrorsReport);
        await CreateGraphAsync();
        var report = SourceRef.Report("05_errores_y_bugs.md");
        await KnowledgeGraphIngestor.AddAsync(DatabasePath, Paths, report, CancellationToken.None);
        new ReportWriter(Paths).Update(report, "replace", null, "# Errores y bugs\n\nOtro texto.", "cambio", ReportVersionOrigin.Ui);

        using var factory = new WebTestFactory();
        var client = factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);
        SqliteConnection.ClearAllPools();
        File.SetAttributes(DatabasePath, FileAttributes.ReadOnly);
        try
        {
            var restore = await client.PostAsync("/api/reports/05_errores_y_bugs.md/versions/v0001/restore", Http.JsonBody(new { }));
            Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
            Assert.Equal(ErrorsReport, File.ReadAllText(Path.Combine(Paths.ReportsPath, "05_errores_y_bugs.md")));
            Assert.False(factory.Services.GetRequiredService<WorkspaceState>().AnyKnowledgeOperationRunning);
        }
        finally
        {
            File.SetAttributes(DatabasePath, FileAttributes.Normal);
        }
    }

    // ── Punto 8: el artefacto guarda si se restauró o se borró ────────────────────────────────────────────────

    [Fact]
    public async Task Undo_marks_say_whether_the_report_was_restored_or_deleted()
    {
        using var factory = new WebTestFactory
        {
            CustomLlm = new ToolScriptLlmService(
                ("create_report", JsonSerializer.Serialize(new { title = "Estudio de colas", content = "Texto del estudio." })),
                ("update_report", JsonSerializer.Serialize(new
                {
                    file_name = "custom/estudio-de-colas.md", mode = "append_section", heading = "Más", content = "Añadido.", note = "añadida"
                })))
        };
        var client = factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);
        var id = (await (await client.PostAsync("/api/chat/sessions", Http.JsonBody(new { }))).ReadJsonAsync()).GetProperty("id").GetString();
        await (await client.PostSseAsync($"/api/chat/sessions/{id}/messages", new { message = "Guarda y amplía un estudio" })).ReadAllSseAsync();

        async Task<JsonElement> Artifacts() =>
            (await (await client.GetAsync($"/api/chat/sessions/{id}")).ReadJsonAsync()).GetProperty("turns")[0].GetProperty("artifacts");

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/reports/custom/estudio-de-colas.md/versions/v0001/restore", Http.JsonBody(new { }))).StatusCode);
        var restored = await Artifacts();
        Assert.False(restored[0].TryGetProperty("undoneAction", out _));
        Assert.Equal(("v0003", "restored"), (restored[1].GetProperty("undoneByVersionId").GetString(), restored[1].GetProperty("undoneAction").GetString()));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/reports/custom/estudio-de-colas.md")).StatusCode);
        var deleted = await Artifacts();
        Assert.All(deleted.EnumerateArray(), a => Assert.Equal(("v0004", "deleted"),
            (a.GetProperty("undoneByVersionId").GetString(), a.GetProperty("undoneAction").GetString())));
        Assert.False(factory.Services.GetRequiredService<WorkspaceState>().AnyKnowledgeOperationRunning);
    }

    // ── Punto 13: el 413 de Kestrel con un servidor real ──────────────────────────────────────────────────────

    [Fact]
    public async Task Kestrel_cuts_a_chunked_body_over_the_limit_with_the_api_413()
    {
        using var factory = new WebTestFactory();
        factory.UseKestrel(0);
        var client = factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);

        // Sin Content-Length (chunked): solo el límite de la ruta en Kestrel puede cortarlo.
        // HttpClient aborta al ver que el servidor cierra mientras aún envía, así que va por un socket: se envía en
        // segundo plano y se lee la respuesta que Kestrel escribe antes de cerrar.
        var (status, json) = await SendChunkedAsync(client.BaseAddress!, "grande.pdf", DocumentTypeRegistry.MaxFileBytes + 1024 * 1024);
        Assert.Equal(413, status);
        using var body = JsonDocument.Parse(json);
        Assert.Equal("file_too_large", body.RootElement.GetProperty("error").GetString());
        Assert.Equal("El cuerpo de la petición supera el tamaño máximo admitido.", body.RootElement.GetProperty("message").GetString());

        // Con Content-Length mayor que el límite responde el handler, antes de leer nada.
        var declared = await client.SendAsync(UploadRequest("grande.pdf", new ByteArrayContent(new byte[DocumentTypeRegistry.MaxFileBytes + 1])));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);
        Assert.Equal("file_too_large", (await declared.ReadJsonAsync()).GetProperty("error").GetString());
        Assert.Empty(new DocumentStore(Paths).List());
    }


    /// <summary>
    /// POST de <paramref name="bytes"/> ceros en chunked por un socket; devuelve el estado y el JSON de la respuesta. Se lee
    /// a la vez que se envía: Kestrel responde y cierra con datos sin leer, y el RST que sigue descartaría una respuesta
    /// que se leyera después.
    /// </summary>
    private static async Task<(int Status, string Body)> SendChunkedAsync(Uri server, string fileName, long bytes)
    {
        const string Crlf = "\r\n";
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(server.Host, server.Port);
        var stream = tcp.GetStream();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var receive = Task.Run(async () =>
        {
            var received = new StringBuilder();
            var buffer = new byte[16 * 1024];
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var n = await stream.ReadAsync(buffer, stop.Token);
                    if (n == 0)
                        break;
                    received.Append(Encoding.UTF8.GetString(buffer, 0, n));
                    if (TryParse(received.ToString()) is { } done)
                        return done;
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
            }
            return TryParse(received.ToString()) ?? throw new InvalidOperationException($"Respuesta incompleta: {received}");
        });

        var head = $"POST /api/documents HTTP/1.1{Crlf}Host: {server.Authority}{Crlf}Content-Type: application/octet-stream{Crlf}" +
                   $"X-File-Name: {Uri.EscapeDataString(fileName)}{Crlf}Transfer-Encoding: chunked{Crlf}Connection: close{Crlf}{Crlf}";
        var chunk = new byte[64 * 1024];
        var prefix = Encoding.ASCII.GetBytes($"{chunk.Length:x}{Crlf}");
        var end = Encoding.ASCII.GetBytes(Crlf);
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            for (long sent = 0; sent < bytes && !receive.IsCompleted; sent += chunk.Length)
            {
                await stream.WriteAsync(prefix);
                await stream.WriteAsync(chunk);
                await stream.WriteAsync(end);
            }
        }
        catch (IOException)
        {
            // El servidor ya respondió y cerró: es lo esperado.
        }

        return await receive;

        static (int, string)? TryParse(string response)
        {
            var split = response.IndexOf(Crlf + Crlf, StringComparison.Ordinal);
            var open = split < 0 ? -1 : response.IndexOf('{', split);
            var close = response.LastIndexOf('}');
            if (open < 0 || close < open)
                return null;
            var json = response[open..(close + 1)];
            try
            {
                using var _ = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }
            return (int.Parse(response.Split(' ', 3)[1], System.Globalization.CultureInfo.InvariantCulture), json);
        }
    }
}
