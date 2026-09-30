using System.Net;
using System.Text;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OpenAILLmService.Tests;

/// <summary>
/// A stream that closes without a <c>finish_reason</c> (end of the HTTP body, a <c>[DONE]</c> with no final chunk, or a
/// read error) used to drop what the service was still holding: an embedded tool call in its buffer and the text the
/// <c>&lt;think&gt;</c> reader retains while it matches a tag. The consumer then closed the answer as a normal
/// <c>Stop</c>. Now every choice left open is closed as a <c>finish_reason</c> would close it, with
/// <see cref="FinishReason.Other"/> and a warning; an embedded tool call whose JSON was cut is discarded with a warning,
/// and a cancellation by the caller flushes nothing. The per-choice test covers <c>hasYieldedText</c>, which was global:
/// once one choice had streamed text, the prefix of every other choice skipped classification.
/// </summary>
public class StreamingAbnormalEndTests
{
    private const string ReadFileCall = """{"name":"read_file","arguments":{"path":"a.txt"}}""";

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(20);

    // ------------------------------------------------------------------ texto retenido

    [Fact]
    public async Task A_stream_ending_without_finish_reason_emits_a_retained_partial_tag_as_text()
    {
        var run = await RunAsync(Sse.Content(0, "Hola ") + Sse.Content(0, "<thi"));

        Assert.Equal(["Hola ", "<thi"], run.Text(0));
        AssertClosedAbnormally(run, 0);
    }

    [Fact]
    public async Task A_stream_ending_inside_an_open_think_keeps_the_retained_text_as_reasoning()
    {
        var run = await RunAsync(Sse.Content(0, "<think>razono") + Sse.Content(0, "</thi"));

        Assert.Equal("razono</thi", string.Concat(run.Reasoning(0)));
        Assert.Empty(run.Text(0));
        AssertClosedAbnormally(run, 0);
    }

    [Fact]
    public async Task A_done_without_a_final_chunk_flushes_like_a_finish_reason()
    {
        var run = await RunAsync(Sse.Content(0, "Hola <thi") + Sse.Done);

        Assert.Equal("Hola <thi", string.Concat(run.Text(0)));
        AssertClosedAbnormally(run, 0);
    }

    [Fact]
    public async Task A_read_error_mid_stream_flushes_what_was_retained()
    {
        var run = await RunAsync(Sse.Content(0, "Hola ") + Sse.Content(0, "<thi"), failAtEnd: true);

        Assert.Equal(["Hola ", "<thi"], run.Text(0));
        AssertClosedAbnormally(run, 0);
    }

    // ------------------------------------------------------------------ tool calls embebidas

    [Fact]
    public async Task A_complete_embedded_tool_call_is_emitted_when_the_stream_ends_without_finish_reason()
    {
        var run = await RunAsync(
            Sse.Content(0, """{"name":"read_file",""") + Sse.Content(0, "\"arguments\":{\"path\":\"a.txt\"}}"),
            withTool: true);

        Assert.Empty(run.Text(0));
        var call = Assert.Single(run.ToolCalls(0));
        Assert.Equal("read_file", call.FunctionName);
        Assert.Equal("""{"path":"a.txt"}""", call.FunctionArgumentsUpdate);
        AssertClosedAbnormally(run, 0);
    }

    [Fact]
    public async Task A_complete_embedded_tool_call_is_emitted_after_a_done_without_final_chunk()
    {
        var run = await RunAsync(Sse.Content(0, ReadFileCall) + Sse.Done, withTool: true);

        Assert.Equal("read_file", Assert.Single(run.ToolCalls(0)).FunctionName);
        AssertClosedAbnormally(run, 0);
    }

    [Fact]
    public async Task An_embedded_tool_call_with_incomplete_json_is_discarded_with_a_warning()
    {
        var run = await RunAsync(Sse.Content(0, """{"name":"read_file","arguments":{"path":"a.t"""), withTool: true);

        Assert.Empty(run.Text(0));
        Assert.Empty(run.ToolCalls(0));
        AssertClosedAbnormally(run, 0);
        Assert.Contains(run.Logger.Warnings, m => m.Contains("incomplete") && m.Contains("discarded"));
    }

    [Fact]
    public async Task A_native_tool_call_cut_mid_arguments_is_forwarded_as_it_arrived_with_a_warning()
    {
        // Las nativas las ensambla el consumidor según llegan: el proveedor no las retiene ni las
        // puede retirar, pero avisa de que el stream las dejó a medias.
        var run = await RunAsync(Sse.ToolCall(0, "c1", "read_file", """{"path":"a.t"""), withTool: true);

        var call = Assert.Single(run.ToolCalls(0));
        Assert.Equal("read_file", call.FunctionName);
        AssertClosedAbnormally(run, 0);
        Assert.Contains(run.Logger.Warnings, m => m.Contains("were incomplete") && m.Contains("read_file"));
    }

    [Theory]
    [InlineData("""{"name":"read_file","arguments":{"path":"a.txt"}}""", false)]
    [InlineData("""[{"name":"read_file"}]""", false)]
    [InlineData("""{"name":"read_file","arguments":{"path":"a.t""", true)]
    [InlineData("""{"name":"read_file","arguments":{"path":"a.txt"}""", true)]
    [InlineData("""{"path":"a \"}\" b"}""", false)]
    [InlineData("""{"path":"a \"}\" b""", true)]
    [InlineData("<tool_call>{\"name\":\"x\"}</tool_call>", false)]
    [InlineData("<tool_call>{\"name\":\"x\"}", true)]
    public void Incomplete_tool_call_detection(string content, bool expected)
    {
        Assert.Equal(expected, OpenAILlmService.IsIncompleteToolCall(content));
    }

    // ------------------------------------------------------------------ cancelación

    [Fact]
    public async Task A_cancellation_by_the_caller_mid_stream_flushes_nothing()
    {
        // Un solo chunk: el reasoning sale, el JSON queda en el buffer de tool calls embebidas y el stream termina sin
        // finish_reason. El llamante cancela al ver el reasoning, así que el buffer no se puede vaciar.
        var body = Sse.Content(0, "<think>pienso</think>" + ReadFileCall);
        var logger = new CapturingLogger();
        using var service = new FakeTransportService(new SseHandler(body, failAtEnd: false), logger);
        using var caller = new CancellationTokenSource();
        var chunks = new List<LlmStreamChunk>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var chunk in service.InvokeStreamingAsync(Request(withTool: true), caller.Token))
            {
                chunks.Add(chunk);
                caller.Cancel();
            }
        }).WaitAsync(Guard);

        var only = Assert.Single(chunks);
        Assert.True(only.IsThinking);
        Assert.Equal("pienso", only.Delta);
    }

    // ------------------------------------------------------------------ estado por choice

    [Fact]
    public async Task Text_streamed_by_one_choice_does_not_skip_the_prefix_classification_of_another()
    {
        var body =
            Sse.Content(0, "Hola") +
            Sse.Content(1, """{"name":"read_file",""") +
            Sse.Content(0, " mundo") +
            Sse.Content(1, "\"arguments\":{\"path\":\"a.txt\"}}") +
            Sse.Finish(0, "stop") +
            Sse.Finish(1, "stop") +
            Sse.Done;

        var run = await RunAsync(body, withTool: true);

        Assert.Equal(["Hola", " mundo"], run.Text(0));
        Assert.Empty(run.ToolCalls(0));
        Assert.Empty(run.Text(1));
        Assert.Equal("read_file", Assert.Single(run.ToolCalls(1)).FunctionName);
        Assert.Empty(run.Logger.Warnings);
    }

    // -------------------------------------------------------------------------

    private static void AssertClosedAbnormally(Run run, int choice)
    {
        var complete = Assert.Single(run.Chunks, c => c.ChoiceIndex == choice && c.IsComplete);
        Assert.Equal(FinishReason.Other, complete.FinishReason);
        Assert.Equal(OpenAILlmService.StreamClosedWithoutFinishReason, complete.RawFinishReason);
        Assert.Same(complete, run.Chunks.Last(c => c.ChoiceIndex == choice));
        Assert.Contains(run.Logger.Warnings, m => m.Contains("without a finish_reason"));
    }

    private static LlmRequest Request(bool withTool = false) => new()
    {
        BaseUrl      = "https://fake.invalid/v1",
        Model        = "fake-model",
        ApiKey       = "k",
        Conversation = new ConversationContext(),
        UseStreaming = true,
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

    private static async Task<Run> RunAsync(string body, bool withTool = false, bool failAtEnd = false)
    {
        var logger = new CapturingLogger();
        using var service = new FakeTransportService(new SseHandler(body, failAtEnd), logger);
        var chunks = new List<LlmStreamChunk>();

        async Task CollectAsync()
        {
            await foreach (var chunk in service.InvokeStreamingAsync(Request(withTool)))
                chunks.Add(chunk);
        }

        await CollectAsync().WaitAsync(Guard);
        return new Run(chunks, logger);
    }

    private sealed record Run(List<LlmStreamChunk> Chunks, CapturingLogger Logger)
    {
        public List<string> Text(int choice) =>
            [.. Chunks.Where(c => c.ChoiceIndex == choice && !c.IsThinking && c.Delta.Length > 0).Select(c => c.Delta)];

        public List<string> Reasoning(int choice) =>
            [.. Chunks.Where(c => c.ChoiceIndex == choice && c.IsThinking && c.Delta.Length > 0).Select(c => c.Delta)];

        public List<ToolCallUpdate> ToolCalls(int choice) =>
            [.. Chunks.Where(c => c.ChoiceIndex == choice).SelectMany(c => c.ToolCallUpdates ?? [])];
    }

    /// <summary>SSE lines as a provider sends them.</summary>
    private static class Sse
    {
        public const string Done = "data: [DONE]\n\n";

        public static string Content(int choice, string content) =>
            Line(new JObject { ["index"] = choice, ["delta"] = new JObject { ["content"] = content } });

        public static string ToolCall(int choice, string id, string name, string arguments) =>
            Line(new JObject
            {
                ["index"] = choice,
                ["delta"] = new JObject
                {
                    ["tool_calls"] = new JArray(new JObject
                    {
                        ["index"]    = 0,
                        ["id"]       = id,
                        ["type"]     = "function",
                        ["function"] = new JObject { ["name"] = name, ["arguments"] = arguments }
                    })
                }
            });

        public static string Finish(int choice, string reason) =>
            Line(new JObject { ["index"] = choice, ["delta"] = new JObject(), ["finish_reason"] = reason });

        private static string Line(JObject choice) =>
            $"data: {new JObject { ["choices"] = new JArray(choice) }.ToString(Formatting.None)}\n\n";
    }

    private sealed class FakeTransportService(HttpMessageHandler handler, ILogger logger) : OpenAILlmService(logger)
    {
        protected internal override HttpMessageHandler CreateHttpMessageHandler() => handler;
    }

    /// <summary>Answers with <paramref name="body"/> as the SSE stream; with <paramref name="failAtEnd"/> the read after it fails.</summary>
    private sealed class SseHandler(string body, bool failAtEnd) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BodyStream(Encoding.UTF8.GetBytes(body), failAtEnd))
            });
    }

    /// <summary>The body bytes, then either the end of the stream or an <see cref="IOException"/>, like a dropped connection.</summary>
    private sealed class BodyStream(byte[] bytes, bool failAtEnd) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= bytes.Length)
                return failAtEnd ? throw new IOException("The connection was reset.") : 0;

            var read = Math.Min(count, bytes.Length - _position);
            Array.Copy(bytes, _position, buffer, offset, read);
            _position += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var array = new byte[buffer.Length];
            var read = Read(array, 0, array.Length);
            array.AsSpan(0, read).CopyTo(buffer.Span);
            return ValueTask.FromResult(read);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly Lock _lock = new();
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings
        {
            get
            {
                lock (_lock)
                {
                    return [.. _warnings];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning) return;
            lock (_lock)
            {
                _warnings.Add(formatter(state, exception));
            }
        }
    }
}
