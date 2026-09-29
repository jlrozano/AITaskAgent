using Microsoft.AspNetCore.Http.Features;
using System.Text;
using System.Text.Json;

namespace CodebaseAnalysisWeb.Sse;

/// <summary>Intervalo del comentario de keep-alive (<c>CodebaseAnalysis:Web:SseKeepAliveSeconds</c>, 15 s por defecto).</summary>
public sealed record SseOptions(TimeSpan KeepAliveInterval)
{
    public static SseOptions FromConfiguration(IConfiguration configuration) =>
        new(TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("CodebaseAnalysis:Web:SseKeepAliveSeconds", 15.0))));
}

/// <summary>
/// Server-Sent Events escritos a mano (plan D6): cabeceras, sin buffering, <c>id</c>/<c>event</c>/<c>data</c>,
/// flush por evento y un comentario <c>: keep-alive</c> periódico. Las escrituras se serializan, así que el
/// keep-alive no se intercala dentro de un evento.
/// </summary>
public sealed class SseWriter : IAsyncDisposable
{
    private readonly HttpResponse _response;
    private readonly JsonSerializerOptions _json;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _keepAliveCts;
    private readonly Task _keepAlive;
    private readonly CancellationToken _requestAborted;

    private SseWriter(HttpContext context, JsonSerializerOptions json, TimeSpan keepAliveInterval)
    {
        _response = context.Response;
        _json = json;
        _requestAborted = context.RequestAborted;
        _keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        _keepAlive = KeepAliveLoopAsync(keepAliveInterval, _keepAliveCts.Token);
    }

    /// <summary>Envía las cabeceras y empieza el keep-alive.</summary>
    public static async Task<SseWriter> StartAsync(HttpContext context, JsonSerializerOptions json, SseOptions options)
    {
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await response.StartAsync(context.RequestAborted);
        await response.Body.FlushAsync(context.RequestAborted);
        return new SseWriter(context, json, options.KeepAliveInterval);
    }

    /// <summary>Escribe un evento. <paramref name="id"/> null = sin línea <c>id:</c> (el chat no tiene replay).</summary>
    public Task WriteEventAsync(string eventName, object payload, long? id = null)
    {
        var sb = new StringBuilder();
        if (id.HasValue)
            sb.Append("id: ").Append(id.Value).Append('\n');
        sb.Append("event: ").Append(eventName).Append('\n');

        // JSON en una línea: el serializador no emite saltos de línea sin WriteIndented.
        sb.Append("data: ").Append(JsonSerializer.Serialize(payload, _json)).Append("\n\n");
        return WriteRawAsync(sb.ToString());
    }

    public Task WriteCommentAsync(string comment) => WriteRawAsync($": {comment}\n\n");

    private async Task WriteRawAsync(string text)
    {
        await _writeLock.WaitAsync(_requestAborted);
        try
        {
            await _response.WriteAsync(text, Encoding.UTF8, _requestAborted);
            await _response.Body.FlushAsync(_requestAborted);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task KeepAliveLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await WriteCommentAsync("keep-alive");
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Cliente desconectado o stream terminado.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _keepAliveCts.CancelAsync();
        await _keepAlive;
        _keepAliveCts.Dispose();
        _writeLock.Dispose();
    }
}
