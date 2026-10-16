using System.Net;
using System.Net.Sockets;
using System.Text;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using Microsoft.Extensions.Logging;

namespace OpenAILLmService.Tests;

/// <summary>
/// At Trace the provider logs what the model received and returned (request body, content, native and
/// &lt;think&gt; reasoning, tool calls, finish_reason, stream timings, the whole usage block with
/// reasoning_tokens); below Trace it formats and logs none of it.
/// </summary>
public class TraceLoggingTests
{
    private const string StreamBody =
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"pienso en la ruta\"}}],\"usage\":null}\n\n" +
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Leo el fichero.\"}}],\"usage\":null}\n\n" +
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\"}}]}}],\"usage\":null}\n\n" +
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"a.cs\\\"}\"}}]}}],\"usage\":null}\n\n" +
        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}],\"usage\":null}\n\n" +
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":40,\"total_tokens\":50," +
        "\"prompt_tokens_details\":{\"cached_tokens\":7},\"completion_tokens_details\":{\"reasoning_tokens\":33}}}\n\n" +
        "data: [DONE]\n\n";

    [Fact]
    public async Task Stream_at_trace_logs_request_content_reasoning_tool_calls_and_usage()
    {
        var log = new CaptureLogger(LogLevel.Trace);
        await using var server = RawServer.Start("text/event-stream", StreamBody);

        var chunks = new List<LlmStreamChunk>();
        await foreach (var chunk in new OpenAILlmService(log).InvokeStreamingAsync(Request(server)))
            chunks.Add(chunk);

        var text = log.Text(LogLevel.Trace);
        Assert.Contains("(streaming) with body", text);
        Assert.Contains("\"stream_options\"", text);
        Assert.Contains("[Content]: Leo el fichero.", text);
        Assert.Contains("[Reasoning native]: pienso en la ruta", text);
        Assert.Contains("read_file({\"path\":\"a.cs\"})", text);
        Assert.Contains("finish_reason=ToolCalls", text);
        Assert.Contains("\"reasoning_tokens\":33", text);
        Assert.Contains("reasoning_tokens=33", text);
        Assert.Contains("cached_tokens=7", text);
        Assert.Contains("first_delta_ms=", text);
        Assert.Contains("deltas=4", text);
        Assert.DoesNotContain("Bearer", text);

        // The usage the step needs travels on the chunks, including the reasoning tokens.
        Assert.Contains(chunks, c => c.ReasoningTokens == 33 && c.CompletionTokens == 40);
        Assert.Contains(chunks, c => c.IsComplete && c.CachedPromptTokens == 7 && c.PromptTokens == 10);
    }

    [Fact]
    public async Task Stream_without_prompt_tokens_details_reports_no_cached_tokens()
    {
        var body =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hola\"}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]," +
            "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"total_tokens\":12}}\n\n" +
            "data: [DONE]\n\n";
        await using var server = RawServer.Start("text/event-stream", body);

        var chunks = new List<LlmStreamChunk>();
        await foreach (var chunk in new OpenAILlmService(new CaptureLogger(LogLevel.Information)).InvokeStreamingAsync(Request(server)))
            chunks.Add(chunk);

        var final = Assert.Single(chunks, c => c.IsComplete);
        Assert.Equal(10, final.PromptTokens);
        Assert.Null(final.CachedPromptTokens);
    }

    [Fact]
    public async Task Non_stream_reads_cached_tokens_from_prompt_tokens_details()
    {
        const string json =
            "{\"model\":\"m\",\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]," +
            "\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":5,\"total_tokens\":105,\"prompt_tokens_details\":{\"cached_tokens\":64}}}";
        await using var server = RawServer.Start("application/json", json);

        var result = await new OpenAILlmService(new CaptureLogger(LogLevel.Information)).InvokeAsync(Request(server));

        Assert.True(result.IsSuccess);
        Assert.Equal((100, 64), (result.PromptTokens, result.CachedPromptTokens));
    }

    [Fact]
    public async Task Stream_at_trace_separates_think_tag_reasoning_from_native()
    {
        var body =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"<think>razono</think>Hola\"}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var log = new CaptureLogger(LogLevel.Trace);
        await using var server = RawServer.Start("text/event-stream", body);

        await foreach (var _ in new OpenAILlmService(log).InvokeStreamingAsync(Request(server))) { }

        var text = log.Text(LogLevel.Trace);
        Assert.Contains("[Reasoning <think>]: razono", text);
        Assert.Contains("[Content]: Hola", text);
    }

    [Fact]
    public async Task Stream_below_trace_logs_and_formats_nothing_at_trace_or_debug()
    {
        var log = new CaptureLogger(LogLevel.Information);
        await using var server = RawServer.Start("text/event-stream", StreamBody);

        await foreach (var _ in new OpenAILlmService(log).InvokeStreamingAsync(Request(server))) { }

        Assert.DoesNotContain(log.Entries, e => e.Level is LogLevel.Trace or LogLevel.Debug);
        Assert.Equal(0, log.FormattedBelowThreshold);
    }

    [Fact]
    public async Task Non_stream_at_trace_logs_the_response_and_usage()
    {
        const string json =
            "{\"model\":\"m\",\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\"," +
            "\"content\":\"Respuesta\",\"reasoning_content\":\"razon nativa\"}}]," +
            "\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":9,\"total_tokens\":14," +
            "\"completion_tokens_details\":{\"reasoning_tokens\":4}}}";
        var log = new CaptureLogger(LogLevel.Trace);
        await using var server = RawServer.Start("application/json", json);

        var result = await new OpenAILlmService(log).InvokeAsync(Request(server));

        Assert.True(result.IsSuccess);
        Assert.Equal(4, result.ReasoningTokens);
        // Sin prompt_tokens_details el proveedor no informa caché: null, no 0.
        Assert.Null(result.CachedPromptTokens);
        var text = log.Text(LogLevel.Trace);
        Assert.Contains("[Content]: Respuesta", text);
        Assert.Contains("[Reasoning native]: razon nativa", text);
        Assert.Contains("finish_reason=stop", text);
        Assert.Contains("\"reasoning_tokens\":4", text);
        Assert.Contains("reasoning_tokens=4", text);
    }

    // -------------------------------------------------------------------------

    private static LlmRequest Request(RawServer server) => new()
    {
        BaseUrl      = server.BaseUrl,
        Model        = "test-model",
        ApiKey       = "secret-key-123",
        Conversation = new ConversationContext(),
    };

    private sealed class CaptureLogger(LogLevel minimum) : ILogger<OpenAILlmService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        /// <summary>Log calls made below the minimum: each one means a message was formatted for nothing.</summary>
        public int FormattedBelowThreshold { get; private set; }

        public string Text(LogLevel level) =>
            string.Join("\n", Entries.Where(e => e.Level == level).Select(e => e.Message));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                FormattedBelowThreshold++;
                return;
            }
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>One-shot loopback HTTP server that answers the first request with a fixed body.</summary>
    private sealed class RawServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serving;

        public string BaseUrl { get; }

        private RawServer(string contentType, string body)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";
            _serving = ServeAsync(contentType, body);
        }

        public static RawServer Start(string contentType, string body) => new(contentType, body);

        private async Task ServeAsync(string contentType, string body)
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();

            var headerBytes = new List<byte>();
            var one = new byte[1];
            while (!(headerBytes.Count >= 4 && headerBytes[^4] == '\r' && headerBytes[^3] == '\n'
                     && headerBytes[^2] == '\r' && headerBytes[^1] == '\n'))
            {
                if (await stream.ReadAsync(one) == 0) return;
                headerBytes.Add(one[0]);
            }
            var headers = Encoding.ASCII.GetString([.. headerBytes]);
            var lengthLine = headers.Split("\r\n")
                .FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var remaining = lengthLine is null ? 0 : int.Parse(lengthLine["Content-Length:".Length..].Trim());
            var buffer = new byte[4096];
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)));
                if (read == 0) return;
                remaining -= read;
            }

            var payload = Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(head));
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try { await _serving.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { _listener.Stop(); }
        }
    }
}
