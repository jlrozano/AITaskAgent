using System.Net;
using System.Text;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.Resilience;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenAILLmService.Tests;

/// <summary>
/// Per-attempt limit of a non-streaming call and inactivity limit of a stream (audit H-4). The HTTP client used to have a
/// fixed 30-minute timeout and no limit per attempt: a provider that never answered cost the whole step. Now an attempt
/// that outlives <c>RequestTimeoutSeconds</c> is a retriable <see cref="TimeoutException"/> that names the setting, a
/// stream silent for <c>StreamingInactivityTimeoutSeconds</c> fails the same way, and a cancellation by the caller is
/// still an <see cref="OperationCanceledException"/>. The transport is a fake handler, so no request leaves the process.
/// </summary>
public class RequestTimeoutTests
{
    private const string Answer = """
        {"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"model":"fake-model"}
        """;

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(20);

    /// <summary>An entry of <see cref="ScriptedHandler"/>'s script that never answers.</summary>
    private static readonly Func<HttpResponseMessage>? Never = null;

    private static LlmRequest Request(int? requestTimeout = null, int? inactivity = null, bool streaming = false) => new()
    {
        BaseUrl = "https://fake.invalid/v1",
        Model = "fake-model",
        ApiKey = "k",
        Conversation = new ConversationContext(),
        UseStreaming = streaming,
        RequestTimeoutSeconds = requestTimeout,
        StreamingInactivityTimeoutSeconds = inactivity
    };

    private static RetryPolicy Retries(int attempts) => new()
    {
        MaxAttempts = attempts,
        InitialDelay = TimeSpan.FromMilliseconds(10),
        MaxDelay = TimeSpan.FromMilliseconds(10)
    };

    /// <summary>A service whose HTTP client runs on <paramref name="handler"/>.</summary>
    private sealed class FakeTransportService(HttpMessageHandler handler, RetryPolicy? retryPolicy = null)
        : OpenAILlmService(NullLogger.Instance, retryPolicy)
    {
        protected internal override HttpMessageHandler CreateHttpMessageHandler() => handler;
    }

    /// <summary>Answers each call with the next response of the script; a null entry never answers.</summary>
    private sealed class ScriptedHandler(params Func<HttpResponseMessage>?[] script) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls) - 1;
            var respond = script[Math.Min(call, script.Length - 1)];
            if (respond == null)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            return respond!();
        }
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Sse(StallingStream body) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(body) };

    private static string ContentLine(string text) =>
        $$$"""data: {"choices":[{"index":0,"delta":{"content":"{{{text}}}"}}]}""" + "\n\n";

    /// <summary>
    /// A response body that hands out its segments, each after its delay, and then either ends or stalls until the read
    /// is cancelled, like a provider that stops sending in the middle of a stream.
    /// </summary>
    private sealed class StallingStream(bool stallAtEnd, params (TimeSpan Delay, string Text)[] segments) : Stream
    {
        private int _next;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_next >= segments.Length)
            {
                if (!stallAtEnd)
                    return 0;
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            var (delay, text) = segments[_next++];
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);
            var bytes = Encoding.UTF8.GetBytes(text);
            bytes.CopyTo(buffer);
            return bytes.Length;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

    private static async Task<List<LlmStreamChunk>> CollectAsync(
        OpenAILlmService service, LlmRequest request, CancellationToken cancellationToken = default)
    {
        var chunks = new List<LlmStreamChunk>();
        await foreach (var chunk in service.InvokeStreamingAsync(request, cancellationToken))
            chunks.Add(chunk);
        return chunks;
    }

    // ------------------------------------------------------------------ resolución

    [Fact]
    public void Without_settings_the_limits_are_ten_minutes_per_attempt_and_two_of_inactivity()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), OpenAILlmService.RequestTimeoutOf(Request()));
        Assert.Equal(TimeSpan.FromMinutes(2), OpenAILlmService.StreamingInactivityTimeoutOf(Request()));
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(0, null)]
    [InlineData(-5, null)]
    [InlineData(int.MaxValue, null)] // más de lo que admite CancelAfter: sin límite en lugar de lanzar
    public void A_setting_overrides_the_default_and_zero_removes_the_limit(int seconds, int? expected)
    {
        TimeSpan? limit = expected is { } s ? TimeSpan.FromSeconds(s) : null;

        Assert.Equal(limit, OpenAILlmService.RequestTimeoutOf(Request(requestTimeout: seconds)));
        Assert.Equal(limit, OpenAILlmService.StreamingInactivityTimeoutOf(Request(inactivity: seconds)));
    }

    [Fact]
    public void The_timeout_settings_are_not_sent_to_the_provider()
    {
        var body = OpenAILlmService.BuildRequestBody(Request(requestTimeout: 5, inactivity: 7));

        Assert.Null(body["RequestTimeoutSeconds"]);
        Assert.Null(body["StreamingInactivityTimeoutSeconds"]);
    }

    // ------------------------------------------------------------------ sin streaming

    [Fact]
    public async Task An_attempt_with_no_response_times_out_and_is_retried()
    {
        var handler = new ScriptedHandler(Never);
        var service = new FakeTransportService(handler, Retries(2));

        var result = await service.InvokeAsync(Request(requestTimeout: 1)).WaitAsync(Guard);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsRetriable);
        var error = Assert.IsType<TimeoutException>(result.Error);
        Assert.Equal(
            "The request to model 'fake-model' got no response within the per-attempt limit of 1 s " +
            "(RequestTimeoutSeconds); raise it or set 0 to remove it.",
            error.Message);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task A_retry_after_a_timed_out_attempt_can_succeed()
    {
        var handler = new ScriptedHandler(Never, () => Ok(Answer));
        var service = new FakeTransportService(handler, Retries(3));

        var result = await service.InvokeAsync(Request(requestTimeout: 1)).WaitAsync(Guard);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("ok", result.Candidates[0].Content);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task A_cancellation_by_the_caller_is_not_a_timeout()
    {
        var handler = new ScriptedHandler(Never);
        var service = new FakeTransportService(handler, Retries(3));
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.InvokeAsync(Request(requestTimeout: 60), caller.Token).WaitAsync(Guard));
        Assert.Equal(1, handler.Calls);
    }

    // ------------------------------------------------------------------ streaming

    [Fact]
    public async Task A_stream_that_stops_sending_fails_with_a_timeout_after_what_it_sent()
    {
        var body = new StallingStream(stallAtEnd: true, (TimeSpan.Zero, ContentLine("Hola")));
        var service = new FakeTransportService(new ScriptedHandler(() => Sse(body)));
        var received = new List<string>();

        var error = await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var chunk in service.InvokeStreamingAsync(Request(inactivity: 1, streaming: true)))
                received.Add(chunk.Delta);
        }).WaitAsync(Guard);

        Assert.Equal(["Hola"], received);
        Assert.Equal(
            "The stream from model 'fake-model' sent nothing for 1 s while waiting for the next chunk " +
            "(StreamingInactivityTimeoutSeconds); raise it or set 0 to remove it.",
            error.Message);
    }

    [Fact]
    public async Task A_stream_whose_headers_never_arrive_times_out_too()
    {
        var service = new FakeTransportService(new ScriptedHandler(Never));

        var error = await Assert.ThrowsAsync<TimeoutException>(
            () => CollectAsync(service, Request(inactivity: 1, streaming: true))).WaitAsync(Guard);

        Assert.Contains("before the response headers arrived (StreamingInactivityTimeoutSeconds)", error.Message);
    }

    [Fact]
    public async Task The_inactivity_limit_restarts_with_every_chunk()
    {
        // Cuatro trozos a 600 ms: 2,4 s en total con un límite de 1 s, que solo cuenta el silencio entre trozos.
        var gap = TimeSpan.FromMilliseconds(600);
        var body = new StallingStream(stallAtEnd: false,
            (gap, ContentLine("uno ")), (gap, ContentLine("dos ")), (gap, ContentLine("tres ")), (gap, "data: [DONE]\n\n"));
        var service = new FakeTransportService(new ScriptedHandler(() => Sse(body)));

        var chunks = await CollectAsync(service, Request(inactivity: 1, streaming: true)).WaitAsync(Guard);

        Assert.Equal("uno dos tres ", string.Concat(chunks.Select(c => c.Delta)));
    }

    [Fact]
    public async Task A_cancellation_by_the_caller_while_the_stream_is_silent_is_not_a_timeout()
    {
        var body = new StallingStream(stallAtEnd: true, (TimeSpan.Zero, ContentLine("Hola")));
        var service = new FakeTransportService(new ScriptedHandler(() => Sse(body)));
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CollectAsync(service, Request(inactivity: 60, streaming: true), caller.Token)).WaitAsync(Guard);

        Assert.IsNotType<TimeoutException>(ex);
    }
}
