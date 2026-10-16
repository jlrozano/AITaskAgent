using System.Diagnostics;
using System.Net;
using System.Text;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace OpenAILLmService.Tests;

/// <summary>
/// Freno compartido de llamadas: límite de concurrencia por BaseUrl+deployment y pausa común tras un 429/503 con
/// <c>Retry-After</c>. Se probó contra un log real con hasta 16 llamadas simultáneas y 263 respuestas 429 (cada llamada
/// esperaba y reintentaba por su cuenta). El transporte es un handler simulado: ninguna petición sale del proceso, y cada
/// test usa su propio registro de frenos para no contaminar a los demás con el estado estático.
/// </summary>
public class CallGateTests
{
    private const string Answer = """
        {"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"model":"fake-model"}
        """;

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(20);

    private static LlmRequest Request(int? limit = null, string model = "dep-a", string baseUrl = "https://fake.invalid/v1") => new()
    {
        BaseUrl = baseUrl,
        Model = model,
        ApiKey = "k",
        Conversation = new ConversationContext(),
        Options = limit is { } l ? new JObject { [OpenAILlmService.MaxConcurrentRequestsKey] = l } : []
    };

    private static RetryPolicy Retries(int attempts = 3) => new()
    {
        MaxAttempts = attempts,
        InitialDelay = TimeSpan.FromMilliseconds(5),
        MaxDelay = TimeSpan.FromSeconds(5)
    };

    private static LlmCallGateRegistry FreshRegistry() => new(jitterStep: TimeSpan.Zero);

    private sealed class FakeService(HttpMessageHandler handler, LlmCallGateRegistry gates, int attempts = 3)
        : OpenAILlmService(NullLogger.Instance, Retries(attempts))
    {
        public bool ThrowOnEndpoint { get; set; }

        protected internal override HttpMessageHandler CreateHttpMessageHandler() => handler;

        protected internal override string GetEndpoint(LlmRequest request) =>
            ThrowOnEndpoint ? throw new InvalidOperationException("boom") : base.GetEndpoint(request);

        public FakeService Init() { Gates = gates; return this; }
    }

    /// <summary>
    /// Handler that counts calls in flight (and the maximum), stamps when each call was sent, and lets the test decide
    /// the answer of each call.
    /// </summary>
    private sealed class ProbeHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<(int Call, TimeSpan At, string Model)> _sends = [];
        private int _inFlight, _max, _calls;

        public int MaxInFlight => Volatile.Read(ref _max);
        public int Calls => Volatile.Read(ref _calls);
        public List<(int Call, TimeSpan At, string Model)> Sends { get { lock (_sends) return [.. _sends]; } }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _calls) - 1;
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen) { }
            lock (_sends) _sends.Add((call, _clock.Elapsed, request.RequestUri!.AbsolutePath + request.RequestUri.Query));
            try { return await respond(call, ct); }
            finally { Interlocked.Decrement(ref _inFlight); }
        }
    }

    private static HttpResponseMessage Ok() =>
        new(HttpStatusCode.OK) { Content = new StringContent(Answer, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage TooMany(int retryAfterMs)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("rate limit") };
        response.Headers.TryAddWithoutValidation("retry-after-ms", retryAfterMs.ToString());
        return response;
    }

    private static Task<T> Within<T>(Task<T> task) => task.WaitAsync(Guard);

    // ---------------------------------------------------------------- (a) límite

    [Fact]
    public async Task Never_more_calls_in_flight_than_the_limit()
    {
        var handler = new ProbeHandler(async (_, ct) => { await Task.Delay(40, ct); return Ok(); });
        using var service = new FakeService(handler, FreshRegistry()).Init();

        var results = await Within(Task.WhenAll(Enumerable.Range(0, 12).Select(_ => service.InvokeAsync(Request(limit: 3)))));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(12, handler.Calls);
        Assert.InRange(handler.MaxInFlight, 1, 3);
    }

    [Fact]
    public async Task The_limit_option_is_not_forwarded_in_the_request_body()
    {
        var body = OpenAILlmService.BuildRequestBody(Request(limit: 4));

        Assert.Null(body[OpenAILlmService.MaxConcurrentRequestsKey]);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Without_the_option_the_default_of_nine_applies()
    {
        var handler = new ProbeHandler(async (_, ct) => { await Task.Delay(40, ct); return Ok(); });
        using var service = new FakeService(handler, FreshRegistry()).Init();

        await Within(Task.WhenAll(Enumerable.Range(0, 20).Select(_ => service.InvokeAsync(Request()))));

        Assert.Equal(9, OpenAILlmService.DefaultMaxConcurrentRequests);
        Assert.InRange(handler.MaxInFlight, 1, 9);
    }

    // ---------------------------------------------------------------- (d) 0 = sin límite

    [Fact]
    public async Task Zero_means_no_limit()
    {
        const int n = 12;
        var arrived = new TaskCompletionSource();
        var count = 0;
        var handler = new ProbeHandler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref count) == n) arrived.TrySetResult();
            await arrived.Task.WaitAsync(ct); // solo avanzan cuando las n están dentro a la vez
            return Ok();
        });
        using var service = new FakeService(handler, FreshRegistry()).Init();

        var all = Task.WhenAll(Enumerable.Range(0, n).Select(_ => service.InvokeAsync(Request(limit: 0))));
        var results = await Within(all);

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(n, handler.MaxInFlight);
    }

    // ---------------------------------------------------------------- (b) pausa compartida

    [Fact]
    public async Task After_the_first_429_the_others_wait_for_the_retry_after()
    {
        // Límite 1: las llamadas salen una a una. La primera recibe un 429 de 400 ms; sin pausa compartida la segunda
        // saldría en cuanto la primera suelta el hueco.
        var handler = new ProbeHandler((call, _) => Task.FromResult(call == 0 ? TooMany(400) : Ok()));
        using var service = new FakeService(handler, FreshRegistry()).Init();

        var first = service.InvokeAsync(Request(limit: 1));
        await Task.Delay(30); // la primera ya está dentro
        var others = Enumerable.Range(0, 3).Select(_ => service.InvokeAsync(Request(limit: 1))).ToArray();
        var results = await Within(Task.WhenAll(others.Append(first)));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        var sends = handler.Sends.OrderBy(s => s.At).ToList();
        var firstSend = sends[0].At;
        // Todas las demás salidas (incluido el reintento de la primera) esperan a la pausa; margen por la resolución del reloj.
        Assert.All(sends.Skip(1), s => Assert.True(s.At - firstSend >= TimeSpan.FromMilliseconds(350), $"call {s.Call} sent at {s.At - firstSend}"));
    }

    [Fact]
    public async Task The_pause_is_shared_by_two_instances_with_the_same_key_and_not_by_another_key()
    {
        var registry = FreshRegistry();
        var handler = new ProbeHandler((call, _) => Task.FromResult(call == 0 ? TooMany(500) : Ok()));
        using var a = new FakeService(handler, registry, attempts: 1).Init();
        using var b = new FakeService(handler, registry).Init();

        var failed = await Within(a.InvokeAsync(Request(model: "dep-a")));
        Assert.False(failed.IsSuccess);
        var afterFirst = handler.Sends.Single().At;

        // Otro deployment: no espera. Mismo deployment desde otra instancia: espera la pausa.
        var other = await Within(b.InvokeAsync(Request(model: "dep-other")));
        var otherSend = handler.Sends.Single(s => s.Call == 1).At;
        var same = await Within(b.InvokeAsync(Request(model: "dep-a")));
        var sameSend = handler.Sends.Single(s => s.Call == 2).At;

        Assert.True(other.IsSuccess && same.IsSuccess);
        Assert.True(otherSend - afterFirst < TimeSpan.FromMilliseconds(300), $"other key waited {otherSend - afterFirst}");
        Assert.True(sameSend - afterFirst >= TimeSpan.FromMilliseconds(450), $"same key sent after {sameSend - afterFirst}");
    }

    [Fact]
    public void The_longest_retry_after_wins_and_is_capped()
    {
        var clock = new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
        var gate = new LlmCallGate(2, () => clock);

        gate.ReportThrottle(TimeSpan.FromSeconds(10));
        gate.ReportThrottle(TimeSpan.FromSeconds(3));
        gate.ReportThrottle(TimeSpan.Zero);
        Assert.Equal(clock.AddSeconds(10), gate.NotBefore);

        gate.ReportThrottle(TimeSpan.FromHours(1));
        Assert.Equal(clock + LlmCallGate.MaxPause, gate.NotBefore);
    }

    [Fact]
    public async Task Waiters_leave_the_pause_staggered_by_a_deterministic_jitter()
    {
        var clock = new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
        var delays = new List<TimeSpan>();
        var gate = new LlmCallGate(0, () => clock,
            (span, _) => { lock (delays) delays.Add(span); clock += span; return Task.CompletedTask; })
        { JitterStep = TimeSpan.FromMilliseconds(100) };
        gate.ReportThrottle(TimeSpan.FromSeconds(2));

        for (var i = 0; i < 3; i++)
            (await gate.EnterAsync(CancellationToken.None)).Dispose();

        // Primera: espera la pausa (2 s, sin jitter: n=1 -> 100 ms); las siguientes ya no tienen pausa que esperar.
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100)], delays);
    }

    [Fact]
    public async Task Cancelling_during_the_pause_releases_the_slot_and_honours_the_token()
    {
        var clock = new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
        var gate = new LlmCallGate(1, () => clock, (_, ct) => Task.Delay(Timeout.Infinite, ct));
        gate.ReportThrottle(TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.EnterAsync(cts.Token));

        // Si el hueco se hubiera perdido, esta entrada (ya sin pausa vigente) no volvería a completarse nunca.
        clock = clock.AddSeconds(31);
        (await gate.EnterAsync(CancellationToken.None).WaitAsync(Guard)).Dispose();
    }

    // ---------------------------------------------------------------- (c) el hueco se libera siempre

    [Fact]
    public async Task The_slot_is_released_when_the_call_throws()
    {
        var handler = new ProbeHandler((_, _) => Task.FromResult(Ok()));
        using var service = new FakeService(handler, FreshRegistry()).Init();
        service.ThrowOnEndpoint = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InvokeAsync(Request(limit: 1)));
        service.ThrowOnEndpoint = false;

        var result = await Within(service.InvokeAsync(Request(limit: 1)));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task The_slot_is_released_when_an_in_flight_call_is_cancelled_and_when_a_queued_one_is()
    {
        var hold = new TaskCompletionSource();
        var handler = new ProbeHandler(async (call, ct) =>
        {
            if (call == 0) await hold.Task.WaitAsync(ct);
            return Ok();
        });
        using var service = new FakeService(handler, FreshRegistry()).Init();

        using var inFlightCts = new CancellationTokenSource();
        var inFlight = service.InvokeAsync(Request(limit: 1), inFlightCts.Token);
        await Task.Delay(50);

        // Una en cola con su propio token: se cancela sin haber salido nunca y no consume nada.
        using var queuedCts = new CancellationTokenSource(80);
        var queued = service.InvokeAsync(Request(limit: 1), queuedCts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(queued));
        Assert.Equal(1, handler.Calls);

        // Cancelar la que está en vuelo libera el hueco para la siguiente.
        inFlightCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(inFlight));
        var next = await Within(service.InvokeAsync(Request(limit: 1)));

        Assert.True(next.IsSuccess);
    }

    // ---------------------------------------------------------------- (e) misma clave comparte, distinta no

    [Fact]
    public async Task Two_instances_with_the_same_key_share_the_limit()
    {
        var registry = FreshRegistry();
        var handler = new ProbeHandler(async (_, ct) => { await Task.Delay(40, ct); return Ok(); });
        using var a = new FakeService(handler, registry).Init();
        using var b = new FakeService(handler, registry).Init();

        var calls = Enumerable.Range(0, 4).Select(_ => a.InvokeAsync(Request(limit: 2)))
            .Concat(Enumerable.Range(0, 4).Select(_ => b.InvokeAsync(Request(limit: 2, baseUrl: "https://FAKE.invalid/v1/"))));
        await Within(Task.WhenAll(calls));

        Assert.Equal(8, handler.Calls);
        Assert.InRange(handler.MaxInFlight, 1, 2);
    }

    [Fact]
    public async Task Different_keys_do_not_share_the_limit()
    {
        var arrived = new TaskCompletionSource();
        var count = 0;
        var handler = new ProbeHandler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref count) == 2) arrived.TrySetResult();
            await arrived.Task.WaitAsync(ct); // las dos tienen que estar dentro a la vez
            return Ok();
        });
        var registry = FreshRegistry();
        using var a = new FakeService(handler, registry).Init();
        using var b = new FakeService(handler, registry).Init();

        var results = await Within(Task.WhenAll(
            a.InvokeAsync(Request(limit: 1, model: "dep-a")),
            b.InvokeAsync(Request(limit: 1, model: "dep-b"))));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(2, handler.MaxInFlight);
    }
}
