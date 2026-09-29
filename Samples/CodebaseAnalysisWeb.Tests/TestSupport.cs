using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using CodebaseAnalysisWeb.Analysis;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

// El registro de tools y el sandbox de FileTools son estáticos de proceso: los tests de la web no corren en
// paralelo entre sí.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CodebaseAnalysisWeb.Tests;

/// <summary>
/// La web con configuración en memoria: un proveedor <c>fake</c> para el rol del chat, un
/// <see cref="ILlmService"/> falso en streaming y un <see cref="IAnalysisWorkflow"/> falso. Sin red.
/// </summary>
public sealed class WebTestFactory : WebApplicationFactory<Program>
{
    public FakeAnalysisWorkflow Workflow { get; } = new();
    public ScriptedStreamingLlmService Llm { get; set; } = new();

    /// <summary>Si se fija, sustituye a <see cref="Llm"/> como servicio del proveedor falso.</summary>
    public ILlmService? CustomLlm { get; init; }

    /// <summary>Configuración extra (sustituye a la de la factoría).</summary>
    public Dictionary<string, string?> ExtraConfiguration { get; init; } = [];

    /// <summary>
    /// Ajustes de la web (último workspace) propios de la factoría: sin esto los tests leerían y escribirían
    /// los de %LOCALAPPDATA%. Dos factorías con la misma ruta simulan un reinicio.
    /// </summary>
    /// <summary>Servicios extra que se registran al final (sustituyen a los de la web).</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    public string SettingsPath { get; init; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "cba-web-tests", "settings", Guid.NewGuid().ToString("N") + ".json");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LlmConfiguration:DefaultProvider"] = "Fake",
            ["LlmConfiguration:Providers:Fake:ServiceType"] = "fake",
            ["LlmConfiguration:Providers:Fake:BaseUrl"] = "http://fake.invalid/",
            ["LlmConfiguration:Providers:Fake:Model"] = "fake-model",
            ["LlmConfiguration:Roles:ChatAssistant:Provider"] = "Fake",
            ["LlmConfiguration:Roles:ChatAssistant:Model"] = "fake-model",
            ["LlmConfiguration:Roles:ChatAssistant:UseStreaming"] = "true",
            // Por si la máquina tiene la variable: los tests no deben ver la clave real.
            ["Asistente:ApiKey"] = "test-key",
            ["CodebaseAnalysis:Web:SettingsPath"] = SettingsPath,
            // Sin red: los embeddings reales usarían el proveedor de Azure de appsettings.analysis.json.
            ["CodebaseAnalysis:Embeddings:Enabled"] = "false"
        }).AddInMemoryCollection(ExtraConfiguration));
        builder.ConfigureTestServices(services =>
        {
            services.AddKeyedSingleton<ILlmService>("fake", (_, _) => CustomLlm ?? Llm);
            services.RemoveAll<IAnalysisWorkflow>();
            services.AddSingleton<IAnalysisWorkflow>(sp =>
            {
                Workflow.LoggerFactory = sp.GetRequiredService<ILoggerFactory>();
                return Workflow;
            });
            ConfigureServices?.Invoke(services);
        });
    }
}

/// <summary>Carpeta temporal propia de cada test; se borra al terminar.</summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cba-web-tests", Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Un log de Serilog aún abierto: queda en %TEMP%.
        }
    }
}

public sealed record SseEvent(string? Id, string Event, JsonElement Data);

public static class Http
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static StringContent JsonBody(object body) =>
        new(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task OpenWorkspaceAsync(this HttpClient client, string path)
    {
        var response = await client.PutAsync("/api/workspace", JsonBody(new { repositoryPath = path }));
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Lee eventos SSE hasta que el servidor cierra el stream. Los
    /// comentarios (<c>: keep-alive</c>) se ignoran.
    /// </summary>
    public static async IAsyncEnumerable<SseEvent> ReadSseAsync(
        this HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? id = null, name = null;
        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null)
                yield break;

            if (line.Length == 0)
            {
                if (name != null || data.Length > 0)
                {
                    yield return new SseEvent(id, name ?? "message", JsonDocument.Parse(data.ToString()).RootElement.Clone());
                }
                id = null;
                name = null;
                data.Clear();
                continue;
            }

            if (line.StartsWith(':'))
                continue;
            if (line.StartsWith("id: ", StringComparison.Ordinal))
                id = line[4..];
            else if (line.StartsWith("event: ", StringComparison.Ordinal))
                name = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
                data.Append(line[6..]);
        }
    }

    public static async Task<List<SseEvent>> ReadAllSseAsync(this HttpResponseMessage response, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        var list = new List<SseEvent>();
        await foreach (var evt in response.ReadSseAsync(cts.Token))
            list.Add(evt);
        return list;
    }

    public static async Task<HttpResponseMessage> GetSseAsync(this HttpClient client, string url, Action<HttpRequestMessage>? configure = null, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        configure?.Invoke(request);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    public static async Task<HttpResponseMessage> PostSseAsync(this HttpClient client, string url, object body, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonBody(body) };
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    public static async Task<string> StartRunAsync(this HttpClient client, object? body = null)
    {
        var response = await client.PostAsync("/api/analysis", JsonBody(body ?? new { }));
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        return (await response.ReadJsonAsync()).GetProperty("runId").GetString()!;
    }

    public static async Task<JsonElement?> GetCurrentRunAsync(this HttpClient client)
    {
        var response = await client.GetAsync("/api/analysis/current");
        return response.StatusCode == System.Net.HttpStatusCode.NoContent ? null : await response.ReadJsonAsync();
    }

    /// <summary>Espera a que la ejecución actual deje de estar en curso.</summary>
    public static async Task<JsonElement> WaitForRunEndAsync(this HttpClient client, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            var current = await client.GetCurrentRunAsync();
            if (current is { } run && run.GetProperty("status").GetString() != "running")
                return run;
            await Task.Delay(50);
        }
        throw new TimeoutException("La ejecución no terminó a tiempo.");
    }
}

/// <summary>
/// Servicio LLM en streaming con dos iteraciones por turno: pide <c>list_reports</c> y después responde.
/// Mismo guion que el de <c>CodebaseAnalysis.Core.Tests</c> y que <c>chatStream</c> en <c>fixtures.ts</c>.
/// </summary>
public sealed class ScriptedStreamingLlmService : ILlmService
{
    public static readonly string[] FinalChunks = ["El peor es ", "**05 Errores y bugs**", " (rojo)."];
    public static string FinalAnswer => string.Concat(FinalChunks);

    public string Name => "scripted-streaming";

    public bool HangOnFinal { get; init; }

    public TaskCompletionSource FirstFinalDeltaSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public async IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = request.Conversation.History.Messages;
        await Task.Yield();

        if (messages.Count == 0 || messages[^1].Role != LlmConstants.MessageRoles.Tool)
        {
            yield return new LlmStreamChunk { Delta = "Voy a mirar " };
            yield return new LlmStreamChunk { Delta = "los informes." };
            yield return new LlmStreamChunk
            {
                Delta = string.Empty,
                ToolCallUpdates = [new ToolCallUpdate { Index = 0, ToolCallId = "call_1", FunctionName = "list_reports", FunctionArgumentsUpdate = "{}" }]
            };
            yield return new LlmStreamChunk { Delta = string.Empty, IsComplete = true, FinishReason = FinishReason.ToolCalls, TokensUsed = 50 };
            yield break;
        }

        for (var i = 0; i < FinalChunks.Length; i++)
        {
            yield return new LlmStreamChunk { Delta = FinalChunks[i] };
            if (i == 0)
            {
                FirstFinalDeltaSent.TrySetResult();
                if (HangOnFinal)
                    await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }

        yield return new LlmStreamChunk { Delta = string.Empty, IsComplete = true, FinishReason = FinishReason.Stop, TokensUsed = 80 };
    }

    public int EstimateTokenCount(string text) => text.Length / 4;

    public int GetMaxContextTokens(string? model = null) => 1_000_000;
}

/// <summary>
/// Servicio en streaming que pide, iteración a iteración, las tools del guion y después responde «Hecho.». Mismo
/// guion que <c>ToolScriptLlmService</c> de los tests de Core.
/// </summary>
public sealed class ToolScriptLlmService(params (string Tool, string Arguments)[] script) : ILlmService
{
    private int _next;

    public string Name => "tool-script";

    public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public async IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        var index = Interlocked.Increment(ref _next) - 1;
        if (index < script.Length)
        {
            var (tool, arguments) = script[index];
            yield return new LlmStreamChunk
            {
                Delta = string.Empty,
                ToolCallUpdates = [new ToolCallUpdate { Index = 0, ToolCallId = $"call_{index + 1}", FunctionName = tool, FunctionArgumentsUpdate = arguments }]
            };
            yield return new LlmStreamChunk { Delta = string.Empty, IsComplete = true, FinishReason = FinishReason.ToolCalls, TokensUsed = 10 };
            yield break;
        }

        yield return new LlmStreamChunk { Delta = "Hecho." };
        yield return new LlmStreamChunk { Delta = string.Empty, IsComplete = true, FinishReason = FinishReason.Stop, TokensUsed = 10 };
    }

    public int EstimateTokenCount(string text) => text.Length / 4;

    public int GetMaxContextTokens(string? model = null) => 1_000_000;
}
