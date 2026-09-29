using System.Net;
using System.Net.Sockets;
using System.Text;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OpenAILLmService.Tests;

/// <summary>
/// InvokeStreamingAsync retained every answer whose first chunk started with '[' as a possible
/// embedded tool-call array (RF-111), releasing it as one single delta only at finish_reason. A
/// prompt that asks for "[V]"/"[D]"/"[I]" evidence marks makes almost every answer start with
/// '[', so streaming was lost. The decision is now taken on the accumulated prefix: '[' followed
/// by '{' is still retained, '[' followed by anything else is text, and a bare '[' waits for the
/// next chunk.
/// </summary>
public class StreamingEmbeddedToolCallPrefixTests
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Evidence_marked_answer_streams_as_several_deltas()
    {
        await using var server = SseServer.Start(async s =>
        {
            await s.SendContentAsync("[V] El ");
            await s.SendContentAsync("servicio ");
            await s.SendContentAsync("usa SSE.");
            await s.SendFinishAsync();
        });

        var chunks = await CollectAsync(server);

        Assert.Equal(["[V] El ", "servicio ", "usa SSE."], TextDeltas(chunks));
        Assert.Empty(ToolCalls(chunks));
    }

    [Fact]
    public async Task Bare_bracket_is_released_as_soon_as_the_next_chunk_decides_it_is_text()
    {
        var firstTextSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSent    = false;

        await using var server = SseServer.Start(async s =>
        {
            await s.SendContentAsync("[");
            await s.SendContentAsync("V] hola");
            // El finish solo se envía cuando el consumidor ya ha visto texto (o tras el timeout,
            // para que un fallo no cuelgue el test): así se comprueba que no se espera al final.
            await Task.WhenAny(firstTextSeen.Task, Task.Delay(GateTimeout));
            await s.SendContentAsync(" mundo");
            finishSent = true;
            await s.SendFinishAsync();
        });

        var textDeltas = new List<string>();
        var firstTextBeforeFinish = false;
        await foreach (var chunk in Service().InvokeStreamingAsync(Request(server)))
        {
            if (chunk.IsThinking || chunk.Delta.Length == 0) continue;
            if (textDeltas.Count == 0)
            {
                firstTextBeforeFinish = !finishSent;
                firstTextSeen.TrySetResult();
            }
            textDeltas.Add(chunk.Delta);
        }

        Assert.True(firstTextBeforeFinish);
        Assert.Equal(["[V] hola", " mundo"], textDeltas);
    }

    [Fact]
    public async Task Bracket_then_object_in_separate_chunks_is_an_embedded_tool_call()
    {
        await using var server = SseServer.Start(async s =>
        {
            await s.SendContentAsync("[");
            await s.SendContentAsync("""{"name":"read_file","arguments":{"path":"a.txt"}}]""");
            await s.SendFinishAsync();
        });

        var chunks = await CollectAsync(server, withTool: true);

        Assert.Empty(TextDeltas(chunks));
        var call = Assert.Single(ToolCalls(chunks));
        Assert.Equal("read_file", call.FunctionName);
        Assert.Equal("""{"path":"a.txt"}""", call.FunctionArgumentsUpdate);
    }

    [Fact]
    public async Task Json_object_in_content_is_still_an_embedded_tool_call()
    {
        await using var server = SseServer.Start(async s =>
        {
            await s.SendContentAsync("""{"name":"read_file",""");
            await s.SendContentAsync("\"arguments\":{\"path\":\"a.txt\"}}");
            await s.SendFinishAsync();
        });

        var chunks = await CollectAsync(server, withTool: true);

        Assert.Empty(TextDeltas(chunks));
        var call = Assert.Single(ToolCalls(chunks));
        Assert.Equal("read_file", call.FunctionName);
        Assert.Equal("""{"path":"a.txt"}""", call.FunctionArgumentsUpdate);
    }

    [Fact]
    public async Task Streaming_tag_answer_is_not_retained_by_the_provider()
    {
        // Los tags de streaming (<write_file>) los resuelve StreamingTagParser aguas arriba:
        // el proveedor debe dejarlos pasar tal cual y sin retenerlos.
        await using var server = SseServer.Start(async s =>
        {
            await s.SendContentAsync("""<write_file path="x">""");
            await s.SendContentAsync("contenido");
            await s.SendContentAsync("</write_file>");
            await s.SendFinishAsync();
        });

        var chunks = await CollectAsync(server, withTool: true);

        var text = TextDeltas(chunks);
        Assert.True(text.Count > 1, $"Expected streamed deltas, got {text.Count}");
        Assert.Equal("""<write_file path="x">contenido</write_file>""", string.Concat(text));
        Assert.Empty(ToolCalls(chunks));
    }

    [Fact]
    public async Task Stream_ending_with_a_bare_bracket_emits_it_as_text()
    {
        await using var server = SseServer.Start(async s =>
        {
            await s.SendContentAsync("[");
            await s.SendFinishAsync();
        });

        var chunks = await CollectAsync(server);

        Assert.Equal(["["], TextDeltas(chunks));
    }

    [Fact]
    public async Task Stream_closed_without_finish_reason_still_emits_a_bare_bracket()
    {
        await using var server = SseServer.Start(async s =>
        {
            await s.SendContentAsync("[");
            await s.SendDoneAsync();
        });

        var chunks = await CollectAsync(server);

        Assert.Equal(["["], TextDeltas(chunks));
    }

    [Theory]
    [InlineData("[V] texto", "Text")]
    [InlineData("[link](http://x)", "Text")]
    [InlineData("[", "Undecided")]
    [InlineData("  [ \n ", "Undecided")]
    [InlineData("[ \n{\"name\":", "ToolCall")]
    [InlineData("{\"name\":", "ToolCall")]
    [InlineData("<tool_call>", "ToolCall")]
    [InlineData("<write_file path=\"x\">", "Text")]
    [InlineData("<", "Text")]
    [InlineData("Hola", "Text")]
    public void Prefix_classification(string content, string expected)
    {
        Assert.Equal(expected, OpenAILlmService.ClassifyContentPrefix(content).ToString());
    }

    // -------------------------------------------------------------------------

    private static OpenAILlmService Service() => new(NullLogger<OpenAILlmService>.Instance);

    private static LlmRequest Request(SseServer server, bool withTool = false) => new()
    {
        BaseUrl      = server.BaseUrl,
        Model        = "test-model",
        ApiKey       = "k",
        Conversation = new ConversationContext(),
        Tools        = withTool
            ?
            [
                new ToolDefinition
                {
                    Name                 = "read_file",
                    Description          = "Reads a file",
                    ParametersJsonSchema = """{"type":"object","properties":{"path":{"type":"string"}}}"""
                }
            ]
            : null
    };

    private static async Task<List<LlmStreamChunk>> CollectAsync(SseServer server, bool withTool = false)
    {
        var chunks = new List<LlmStreamChunk>();
        await foreach (var chunk in Service().InvokeStreamingAsync(Request(server, withTool)))
            chunks.Add(chunk);
        return chunks;
    }

    private static List<string> TextDeltas(IEnumerable<LlmStreamChunk> chunks) =>
        [.. chunks.Where(c => !c.IsThinking && c.Delta.Length > 0).Select(c => c.Delta)];

    private static List<ToolCallUpdate> ToolCalls(IEnumerable<LlmStreamChunk> chunks) =>
        [.. chunks.SelectMany(c => c.ToolCallUpdates ?? [])];

    /// <summary>
    /// Servidor HTTP/1.1 mínimo en loopback que responde a una única petición con un cuerpo SSE
    /// escrito línea a línea (con flush), para que el cliente lo lea como un stream real.
    /// </summary>
    private sealed class SseServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serving;
        private Stream? _stream;

        public string BaseUrl { get; }

        private SseServer(Func<SseServer, Task> script)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";
            _serving = ServeAsync(script);
        }

        public static SseServer Start(Func<SseServer, Task> script) => new(script);

        private async Task ServeAsync(Func<SseServer, Task> script)
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            _stream = stream;

            await DrainRequestAsync(stream);
            await WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n");
            await script(this);
        }

        // Lee cabeceras y cuerpo (Content-Length) para no cerrar con datos pendientes (RST).
        private static async Task DrainRequestAsync(Stream stream)
        {
            var headerBytes = new List<byte>();
            var one = new byte[1];
            while (!EndsWithBlankLine(headerBytes))
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
        }

        private static bool EndsWithBlankLine(List<byte> b) =>
            b.Count >= 4 && b[^4] == '\r' && b[^3] == '\n' && b[^2] == '\r' && b[^1] == '\n';

        private async Task WriteAsync(string text)
        {
            await _stream!.WriteAsync(Encoding.UTF8.GetBytes(text));
            await _stream.FlushAsync();
        }

        private Task SendChunkAsync(JObject choice) =>
            WriteAsync($"data: {new JObject { ["choices"] = new JArray(choice) }.ToString(Formatting.None)}\n\n");

        public Task SendContentAsync(string content) =>
            SendChunkAsync(new JObject { ["index"] = 0, ["delta"] = new JObject { ["content"] = content } });

        public async Task SendFinishAsync()
        {
            await SendChunkAsync(new JObject { ["index"] = 0, ["delta"] = new JObject(), ["finish_reason"] = "stop" });
            await SendDoneAsync();
        }

        public Task SendDoneAsync() => WriteAsync("data: [DONE]\n\n");

        public async ValueTask DisposeAsync()
        {
            try { await _serving.WaitAsync(GateTimeout); }
            finally { _listener.Stop(); }
        }
    }
}
