using System.Net;
using System.Text;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Support;
using AITaskAgent.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace AITaskAgent.Ollama.Tests;

/// <summary>
/// Native Ollama service (<c>/api/chat</c>): request body (num_ctx, think, tools, tool messages), answers, streaming
/// NDJSON, errors, insufficient context, BaseUrl normalization and the API key never reaching a log. The transport is a
/// fake handler: no request leaves the process.
/// </summary>
public class OllamaLlmServiceTests
{
    private sealed record Captured(Uri Uri, JObject Body, string? Authorization, string? Accept);

    private sealed class FakeHandler(params Func<HttpResponseMessage>?[] script) : HttpMessageHandler
    {
        private int _calls;
        public List<Captured> Requests { get; } = [];
        public int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (Requests)
                Requests.Add(new Captured(request.RequestUri!, JObject.Parse(text), request.Headers.Authorization?.ToString(), request.Headers.Accept.ToString()));
            var respond = script[Math.Min(Interlocked.Increment(ref _calls) - 1, script.Length - 1)];
            if (respond == null)
                await Task.Delay(Timeout.Infinite, cancellationToken); // never answers
            return respond!();
        }
    }

    private sealed class FakeService(HttpMessageHandler handler, ILogger? logger = null, RetryPolicy? retry = null)
        : OllamaLlmService(logger ?? NullLogger.Instance, retry ?? Retries(3))
    {
        protected internal override HttpMessageHandler CreateHttpMessageHandler() => handler;
    }

    private static RetryPolicy Retries(int attempts) => new()
    {
        MaxAttempts = attempts,
        InitialDelay = TimeSpan.FromMilliseconds(10),
        MaxDelay = TimeSpan.FromMilliseconds(10)
    };

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Ndjson(params string[] lines) =>
        new(HttpStatusCode.OK) { Content = new StringContent(string.Join("\n", lines) + "\n", Encoding.UTF8, "application/x-ndjson") };

    private static string Whole(string content, string doneReason = "stop", string extraMessage = "", int prompt = 10, int eval = 5) =>
        $$"""{"model":"m","message":{"role":"assistant","content":{{JsonString(content)}}{{extraMessage}}},"done":true,"done_reason":"{{doneReason}}","prompt_eval_count":{{prompt}},"eval_count":{{eval}}}""";

    private static string Calls(string array) => ",\"tool_calls\":" + array;

    private static string Thinking(string t) => ",\"thinking\":" + JsonString(t);

    private static string JsonString(string s) => new JValue(s).ToString(Newtonsoft.Json.Formatting.None);

    private static string Chunk(string content, string extraMessage = "") =>
        $$"""{"model":"m","message":{"role":"assistant","content":{{JsonString(content)}}{{extraMessage}}},"done":false}""";

    private static string Final(string doneReason = "stop", int prompt = 10, int eval = 5) =>
        $$"""{"model":"m","message":{"role":"assistant","content":""},"done":true,"done_reason":"{{doneReason}}","prompt_eval_count":{{prompt}},"eval_count":{{eval}}}""";

    private static LlmRequest Request(Action<ConversationContext>? conversation = null, Func<LlmRequest, LlmRequest>? tweak = null,
        string baseUrl = "http://fake.invalid:11434", string? apiKey = null)
    {
        var ctx = new ConversationContext();
        if (conversation == null)
            ctx.AddUserMessage("hola");
        else
            conversation(ctx);
        var request = new LlmRequest
        {
            BaseUrl = baseUrl,
            Model = "gemma:26b",
            ApiKey = apiKey,
            SystemPrompt = "sistema",
            Conversation = ctx
        };
        return tweak == null ? request : tweak(request);
    }

    private static LlmRequest WithOptions(LlmRequest r, Action<JObject> set)
    {
        var o = new JObject();
        set(o);
        return r with { Options = o };
    }

    private static async Task<List<LlmStreamChunk>> CollectAsync(FakeService service, LlmRequest request)
    {
        var chunks = new List<LlmStreamChunk>();
        await foreach (var c in service.InvokeStreamingAsync(request, CancellationToken.None))
            chunks.Add(c);
        return chunks;
    }

    private static Task<LlmResult> InvokeAsync(FakeService service, LlmRequest request) =>
        service.InvokeAsync(request, CancellationToken.None);

    // ------------------------------------------------------------------ request body

    [Fact]
    public async Task Body_has_model_messages_stream_false_and_the_system_prompt_first()
    {
        var handler = new FakeHandler(() => Json(Whole("ok")));
        using var service = new FakeService(handler);

        var result = await InvokeAsync(service, Request());

        Assert.True(result.IsSuccess, result.Error?.Message);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("/api/chat", sent.Uri.AbsolutePath);
        Assert.False(sent.Body["stream"]!.Value<bool>());
        Assert.Equal("gemma:26b", sent.Body["model"]!.Value<string>());
        Assert.Equal(("system", "sistema"), (sent.Body["messages"]![0]!["role"]!.Value<string>(), sent.Body["messages"]![0]!["content"]!.Value<string>()));
        Assert.Equal(("user", "hola"), (sent.Body["messages"]![1]!["role"]!.Value<string>(), sent.Body["messages"]![1]!["content"]!.Value<string>()));
        Assert.Null(sent.Body["options"]); // nothing configured: server and Modelfile decide
        Assert.Null(sent.Body["think"]);
        Assert.Equal("application/json", sent.Accept);
    }

    [Fact]
    public async Task NumCtx_from_the_options_goes_in_options_num_ctx_with_num_predict_temperature_and_top_p()
    {
        var handler = new FakeHandler(() => Json(Whole("ok")));
        using var service = new FakeService(handler);

        await InvokeAsync(service, Request(tweak: r => WithOptions(r with { MaxTokens = 1000, Temperature = 0.2f, TopP = 0.9f }, o => o["num_ctx"] = 32768)));

        var options = handler.Requests.Single().Body["options"]!;
        Assert.Equal(32768, options["num_ctx"]!.Value<int>());
        Assert.Equal(1000, options["num_predict"]!.Value<int>());
        Assert.Equal(0.2f, options["temperature"]!.Value<float>());
        Assert.Equal(0.9f, options["top_p"]!.Value<float>());
        // the facade keys are not forwarded raw
        Assert.Null(options["MaxTokens"]);
        Assert.Null(options["Temperature"]);
        Assert.Null(handler.Requests.Single().Body["MaxTokens"]);
    }

    [Fact]
    public async Task ContextTokens_is_an_alias_and_num_ctx_wins_when_both_are_set()
    {
        var handler = new FakeHandler(() => Json(Whole("ok")));
        using var service = new FakeService(handler);

        await InvokeAsync(service, Request(tweak: r => WithOptions(r, o => o["ContextTokens"] = 16384)));
        await InvokeAsync(service, Request(tweak: r => WithOptions(r, o => { o["ContextTokens"] = 16384; o["num_ctx"] = 4096; })));

        Assert.Equal(16384, handler.Requests[0].Body["options"]!["num_ctx"]!.Value<int>());
        Assert.Equal(4096, handler.Requests[1].Body["options"]!["num_ctx"]!.Value<int>());
        Assert.Null(handler.Requests[0].Body["options"]!["ContextTokens"]);
    }

    [Fact]
    public async Task Think_keep_alive_and_format_go_at_the_root_and_other_free_options_go_in_options()
    {
        var handler = new FakeHandler(() => Json(Whole("ok")));
        using var service = new FakeService(handler);

        await InvokeAsync(service, Request(tweak: r => WithOptions(r, o =>
        {
            o["think"] = false;
            o["keep_alive"] = "30m";
            o["format"] = "json";
            o["num_gpu"] = 99;
            o["repeat_penalty"] = 1.1;
            o["RequestTimeoutSeconds"] = 30;
        })));

        var body = handler.Requests.Single().Body;
        Assert.False(body["think"]!.Value<bool>());
        Assert.Equal("30m", body["keep_alive"]!.Value<string>());
        Assert.Equal("json", body["format"]!.Value<string>());
        Assert.Equal(99, body["options"]!["num_gpu"]!.Value<int>());
        Assert.Equal(1.1, body["options"]!["repeat_penalty"]!.Value<double>(), 3);
        Assert.Null(body["options"]!["think"]);
        Assert.Null(body["options"]!["RequestTimeoutSeconds"]);
    }

    [Fact]
    public async Task Think_accepts_an_effort_level_string()
    {
        var handler = new FakeHandler(() => Json(Whole("ok")));
        using var service = new FakeService(handler);

        await InvokeAsync(service, Request(tweak: r => WithOptions(r, o => o["think"] = "high")));

        Assert.Equal("high", handler.Requests.Single().Body["think"]!.Value<string>());
    }

    [Fact]
    public async Task Json_response_format_sets_format_json()
    {
        var handler = new FakeHandler(() => Json(Whole("{}")));
        using var service = new FakeService(handler);

        await InvokeAsync(service, Request(tweak: r => r with { ResponseFormat = new ResponseFormatOptions { Type = ResponseFormatType.JsonObject } }));

        Assert.Equal("json", handler.Requests.Single().Body["format"]!.Value<string>());
    }

    [Fact]
    public async Task Tools_are_sent_as_functions()
    {
        var handler = new FakeHandler(() => Json(Whole("ok")));
        using var service = new FakeService(handler);
        var tool = new ToolDefinition
        {
            Name = "search_schema",
            Description = "Busca",
            ParametersJsonSchema = """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}"""
        };

        await InvokeAsync(service, Request(tweak: r => r with { Tools = [tool] }));

        var sent = handler.Requests.Single().Body["tools"]![0]!;
        Assert.Equal("function", sent["type"]!.Value<string>());
        Assert.Equal("search_schema", sent["function"]!["name"]!.Value<string>());
        Assert.Equal("Busca", sent["function"]!["description"]!.Value<string>());
        Assert.Equal("object", sent["function"]!["parameters"]!["type"]!.Value<string>());
    }

    [Fact]
    public async Task Tool_calls_in_the_history_go_with_object_arguments_and_tool_messages_carry_tool_name()
    {
        var handler = new FakeHandler(() => Json(Whole("ok")));
        using var service = new FakeService(handler);

        await InvokeAsync(service, Request(c =>
        {
            c.AddUserMessage("busca");
            c.AddAssistantMessageWithToolCalls(
                [new ToolCall { Id = "c1", Name = "search_schema", Arguments = """{"query":"facturas"}""" }], "Miro.");
            c.AddToolMessage("c1", """{"ok":true}""");
        }));

        var messages = handler.Requests.Single().Body["messages"]!;
        var assistant = messages[2]!;
        Assert.Equal("assistant", assistant["role"]!.Value<string>());
        Assert.Equal("Miro.", assistant["content"]!.Value<string>());
        Assert.Equal("facturas", assistant["tool_calls"]![0]!["function"]!["arguments"]!["query"]!.Value<string>());
        Assert.Equal(JTokenType.Object, assistant["tool_calls"]![0]!["function"]!["arguments"]!.Type);
        Assert.Equal(("tool", "search_schema", """{"ok":true}"""),
            (messages[3]!["role"]!.Value<string>(), messages[3]!["tool_name"]!.Value<string>(), messages[3]!["content"]!.Value<string>()));
    }

    [Fact]
    public async Task Invalid_history_arguments_are_forwarded_as_an_empty_object()
    {
        var handler = new FakeHandler(() => Json(Whole("ok")));
        using var service = new FakeService(handler);

        await InvokeAsync(service, Request(c =>
        {
            c.AddUserMessage("x");
            c.AddAssistantMessageWithToolCalls([new ToolCall { Id = "c1", Name = "t", Arguments = "{not json" }]);
        }));

        var args = handler.Requests.Single().Body["messages"]![2]!["tool_calls"]![0]!["function"]!["arguments"]!;
        Assert.Equal(JTokenType.Object, args.Type);
        Assert.Empty(args.Children());
    }

    // ------------------------------------------------------------------ BaseUrl and key

    [Theory]
    [InlineData("http://h:11434", "http://h:11434")]
    [InlineData("http://h:11434/", "http://h:11434")]
    [InlineData("http://h:11434/v1", "http://h:11434")]
    [InlineData("http://h:11434/v1/", "http://h:11434")]
    [InlineData("http://h:11434/V1", "http://h:11434")]
    [InlineData("https://ollama.com/api", "https://ollama.com")]
    [InlineData("http://h:11434/proxy/v1", "http://h:11434/proxy")]
    [InlineData(null, "http://localhost:11434")]
    [InlineData("  ", "http://localhost:11434")]
    public void NormalizeHost_removes_v1_api_and_trailing_slash(string? input, string expected) =>
        Assert.Equal(expected, OllamaLlmService.NormalizeHost(input));

    [Theory]
    [InlineData("http://h:11434")]
    [InlineData("http://h:11434/v1")]
    [InlineData("http://h:11434/v1/")]
    public async Task The_request_always_goes_to_api_chat(string baseUrl)
    {
        var handler = new FakeHandler(() => Json(Whole("ok")));
        using var service = new FakeService(handler);

        await InvokeAsync(service, Request(baseUrl: baseUrl));

        Assert.Equal("http://h:11434/api/chat", handler.Requests.Single().Uri.ToString());
    }

    [Fact]
    public async Task Authorization_is_sent_only_when_there_is_a_key()
    {
        var handler = new FakeHandler(() => Json(Whole("ok")));
        using var service = new FakeService(handler);

        await InvokeAsync(service, Request());
        await InvokeAsync(service, Request(apiKey: "sk-ollama-0123456789"));

        Assert.Null(handler.Requests[0].Authorization);
        Assert.Equal("Bearer sk-ollama-0123456789", handler.Requests[1].Authorization);
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly Lock _lock = new();
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages { get { lock (_lock) return [.. _messages]; } }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lock)
                _messages.Add(formatter(state, exception) + (exception == null ? "" : " | " + exception.Message));
        }
    }

    [Fact]
    public async Task The_api_key_never_appears_in_any_log_line_not_even_on_errors_or_at_trace_level()
    {
        const string key = "sk-SECRETPREFIX-0123456789";
        var logger = new CapturingLogger();
        var handler = new FakeHandler(() => Json(Whole("ok")), () => Json("""{"error":"boom"}""", HttpStatusCode.BadRequest), () => Ndjson(Chunk("a"), Final()));
        using var service = new FakeService(handler, logger);

        await InvokeAsync(service, Request(apiKey: key));
        await InvokeAsync(service, Request(apiKey: key));
        await CollectAsync(service, Request(apiKey: key));

        Assert.Contains($"ApiKey: set (length {key.Length})", logger.Messages);
        Assert.DoesNotContain(logger.Messages, m => m.Contains(key[..4], StringComparison.Ordinal) && m.Contains("SECRET", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, m => m.Contains(key, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ non-streaming answers

    [Fact]
    public async Task Text_thinking_and_tokens_are_mapped()
    {
        var handler = new FakeHandler(() => Json(Whole("Hola.", extraMessage: Thinking("pienso"), prompt: 7, eval: 3)));
        using var service = new FakeService(handler);

        var result = await InvokeAsync(service, Request());

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("Hola.", result.Content);
        Assert.Equal("pienso", result.Reasoning);
        Assert.Equal(FinishReason.Stop, result.FinishReason);
        Assert.Equal((7, 3, 10), (result.PromptTokens, result.CompletionTokens, result.TokensUsed));
        Assert.Equal("m", result.Model);
    }

    [Fact]
    public async Task Tool_calls_get_synthetic_ids_and_arguments_as_a_json_string()
    {
        var handler = new FakeHandler(() => Json(Whole("", extraMessage: Calls("""[{"function":{"name":"search_schema","arguments":{"query":"facturas"}}},{"function":{"name":"align","arguments":{}}}]"""))));
        using var service = new FakeService(handler);

        var result = await InvokeAsync(service, Request());

        var calls = result.ToolCalls!;
        Assert.Equal(2, calls.Count);
        Assert.Equal("search_schema", calls[0].Name);
        Assert.Equal("facturas", JObject.Parse(calls[0].Arguments)["query"]!.Value<string>());
        Assert.Equal("{}", calls[1].Arguments);
        Assert.StartsWith("ollama_", calls[0].Id, StringComparison.Ordinal);
        Assert.NotEqual(calls[0].Id, calls[1].Id);
        Assert.Equal(FinishReason.ToolCalls, result.FinishReason);
    }

    [Theory]
    [InlineData("stop", FinishReason.Stop, null)]
    [InlineData("load", FinishReason.Stop, null)]
    [InlineData("length", FinishReason.Length, null)]
    [InlineData("weird", FinishReason.Other, "weird")]
    public async Task Done_reason_is_mapped_to_the_finish_reason(string doneReason, FinishReason expected, string? raw)
    {
        var handler = new FakeHandler(() => Json(Whole("texto", doneReason)));
        using var service = new FakeService(handler);

        var result = await InvokeAsync(service, Request());

        Assert.Equal(expected, result.FinishReason);
        Assert.Equal(raw, result.Candidates[0].RawFinishReason);
    }

    // ------------------------------------------------------------------ insufficient context

    [Fact]
    public async Task An_empty_answer_cut_by_length_is_a_visible_non_retriable_context_error()
    {
        var handler = new FakeHandler(() => Json(Whole("", "length", prompt: 8191, eval: 1)));
        using var service = new FakeService(handler);

        var result = await InvokeAsync(service, Request(tweak: r => WithOptions(r with { MaxTokens = 1000 }, o => o["num_ctx"] = 8192)));

        Assert.False(result.IsSuccess);
        Assert.False(result.IsRetriable);
        var error = Assert.IsType<OllamaContextTooSmallException>(result.Error);
        Assert.Contains("num_ctx 8192", error.Message, StringComparison.Ordinal);
        Assert.Contains("prompt_eval_count 8191", error.Message, StringComparison.Ordinal);
        Assert.Contains("num_ctx", error.Message, StringComparison.Ordinal);
        Assert.Equal(8192, error.ConfiguredContext);
        Assert.Equal(8191, error.PromptTokens);
        Assert.Equal(1, handler.Calls); // not retried
    }

    [Fact]
    public async Task An_empty_answer_that_burned_num_predict_is_not_a_context_error()
    {
        var handler = new FakeHandler(() => Json(Whole("", "length", prompt: 500, eval: 1000)));
        using var service = new FakeService(handler);

        var result = await InvokeAsync(service, Request(tweak: r => r with { MaxTokens = 1000 }));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(FinishReason.Length, result.FinishReason);
    }

    [Fact]
    public async Task An_empty_answer_that_stopped_normally_is_a_valid_empty_success()
    {
        var handler = new FakeHandler(() => Json(Whole("", "stop", prompt: 500, eval: 0)));
        using var service = new FakeService(handler);

        var result = await InvokeAsync(service, Request());

        Assert.True(result.IsSuccess);
        Assert.Equal("", result.Content);
    }

    [Fact]
    public async Task A_prompt_that_reached_num_ctx_is_logged_as_a_warning_even_with_content()
    {
        var logger = new CapturingLogger();
        var handler = new FakeHandler(() => Json(Whole("algo", prompt: 8192, eval: 20)));
        using var service = new FakeService(handler, logger);

        var result = await InvokeAsync(service, Request(tweak: r => WithOptions(r, o => o["num_ctx"] = 8192)));

        Assert.True(result.IsSuccess);
        Assert.Contains(logger.Messages, m => m.Contains("probably truncated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetMaxContextTokens_returns_the_configured_num_ctx_once_known_else_8192()
    {
        var handler = new FakeHandler(() => Json(Whole("ok")));
        using var service = new FakeService(handler);
        Assert.Equal(8192, service.GetMaxContextTokens("gemma:26b"));

        await InvokeAsync(service, Request(tweak: r => WithOptions(r, o => o["num_ctx"] = 32768)));

        Assert.Equal(32768, service.GetMaxContextTokens("gemma:26b"));
        Assert.Equal(8192, service.GetMaxContextTokens("other"));
        Assert.Equal(8192, service.GetMaxContextTokens());
    }

    // ------------------------------------------------------------------ errors and retries

    [Theory]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    public async Task Http_errors_are_failures_retriable_only_when_transient(int status, bool retriable)
    {
        var handler = new FakeHandler(() => Json("""{"error":"nope"}""", (HttpStatusCode)status));
        using var service = new FakeService(handler, retry: Retries(2));

        var result = await InvokeAsync(service, Request());

        Assert.False(result.IsSuccess);
        Assert.Equal(retriable, result.IsRetriable);
        Assert.Contains($"HTTP {status}", result.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(retriable ? 2 : 1, handler.Calls);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_and_the_next_attempt_can_succeed()
    {
        var handler = new FakeHandler(() => Json("{}", HttpStatusCode.ServiceUnavailable), () => Json(Whole("ok")));
        using var service = new FakeService(handler);

        var result = await InvokeAsync(service, Request());

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task A_200_with_an_error_field_or_a_non_json_body_is_a_non_retriable_failure()
    {
        var handler = new FakeHandler(() => Json("""{"error":"model not found"}"""), () => Json("<html>gateway</html>"));
        using var service = new FakeService(handler);

        var withError = await InvokeAsync(service, Request());
        var notJson = await InvokeAsync(service, Request());

        Assert.False(withError.IsSuccess);
        Assert.False(withError.IsRetriable);
        Assert.Contains("model not found", withError.Error!.Message, StringComparison.Ordinal);
        Assert.False(notJson.IsSuccess);
        Assert.False(notJson.IsRetriable);
    }

    [Fact]
    public async Task A_request_with_no_answer_fails_as_a_retriable_timeout_naming_the_setting()
    {
        var handler = new FakeHandler((Func<HttpResponseMessage>?)null);
        using var service = new FakeService(handler, retry: Retries(1));

        var result = await InvokeAsync(service, Request(tweak: r => r with { RequestTimeoutSeconds = 1 }));

        Assert.False(result.IsSuccess);
        Assert.True(result.IsRetriable);
        Assert.IsType<TimeoutException>(result.Error);
        Assert.Contains("RequestTimeoutSeconds", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancellation_by_the_caller_propagates_as_cancellation()
    {
        var handler = new FakeHandler((Func<HttpResponseMessage>?)null);
        using var service = new FakeService(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InvokeAsync(Request(), cts.Token));
    }

    // ------------------------------------------------------------------ streaming

    [Fact]
    public async Task Streaming_sends_stream_true_and_yields_text_thinking_and_the_final_chunk_with_tokens()
    {
        var handler = new FakeHandler(() => Ndjson(
            Chunk("", extraMessage: Thinking("pienso")),
            Chunk("Ho"),
            Chunk("la"),
            Final("stop", prompt: 12, eval: 4)));
        using var service = new FakeService(handler);

        var chunks = await CollectAsync(service, Request(tweak: r => WithOptions(r, o => o["num_ctx"] = 4096)));

        var sent = handler.Requests.Single();
        Assert.True(sent.Body["stream"]!.Value<bool>());
        Assert.Equal(4096, sent.Body["options"]!["num_ctx"]!.Value<int>());
        Assert.Equal("application/x-ndjson", sent.Accept);
        Assert.Equal("pienso", string.Concat(chunks.Where(c => c.IsThinking).Select(c => c.Delta)));
        Assert.Equal("Hola", string.Concat(chunks.Where(c => !c.IsThinking && !c.IsComplete).Select(c => c.Delta)));
        var last = chunks[^1];
        Assert.True(last.IsComplete);
        Assert.Equal(FinishReason.Stop, last.FinishReason);
        Assert.Equal((12, 4, 16), (last.PromptTokens, last.CompletionTokens, last.TokensUsed));
        Assert.Single(chunks, c => c.IsComplete);
    }

    [Fact]
    public async Task Streaming_tool_call_arrives_as_an_update_with_synthetic_id_and_the_finish_reason_is_tool_calls()
    {
        var handler = new FakeHandler(() => Ndjson(
            Chunk("Miro."),
            Chunk("", extraMessage: Calls("""[{"function":{"name":"search_schema","arguments":{"query":"facturas"}}}]""")),
            Final("stop")));
        using var service = new FakeService(handler);

        var chunks = await CollectAsync(service, Request());

        var update = Assert.Single(chunks.Where(c => c.ToolCallUpdates != null).SelectMany(c => c.ToolCallUpdates!));
        Assert.Equal(0, update.Index);
        Assert.Equal("search_schema", update.FunctionName);
        Assert.StartsWith("ollama_", update.ToolCallId, StringComparison.Ordinal);
        Assert.Equal("facturas", JObject.Parse(update.FunctionArgumentsUpdate!)["query"]!.Value<string>());
        Assert.Equal(FinishReason.ToolCalls, chunks[^1].FinishReason);
    }

    [Fact]
    public async Task Streaming_two_tool_calls_get_consecutive_indexes()
    {
        var handler = new FakeHandler(() => Ndjson(
            Chunk("", extraMessage: Calls("""[{"function":{"name":"a","arguments":{}}}]""")),
            Chunk("", extraMessage: Calls("""[{"function":{"name":"b","arguments":{"x":1}}}]""")),
            Final()));
        using var service = new FakeService(handler);

        var chunks = await CollectAsync(service, Request());

        var updates = chunks.Where(c => c.ToolCallUpdates != null).SelectMany(c => c.ToolCallUpdates!).ToList();
        Assert.Equal([0, 1], updates.Select(u => u.Index));
        Assert.Equal(["a", "b"], updates.Select(u => u.FunctionName));
    }

    [Fact]
    public async Task A_stream_that_ends_without_done_closes_as_other_not_as_a_normal_stop()
    {
        var handler = new FakeHandler(() => Ndjson(Chunk("a")));
        using var service = new FakeService(handler);

        var chunks = await CollectAsync(service, Request());

        var last = chunks[^1];
        Assert.True(last.IsComplete);
        Assert.Equal(FinishReason.Other, last.FinishReason);
        Assert.Equal(OllamaLlmService.StreamClosedWithoutDone, last.RawFinishReason);
    }

    [Fact]
    public async Task Streaming_http_error_throws_with_the_status_and_body()
    {
        var handler = new FakeHandler(() => Json("""{"error":"model 'x' not found"}""", HttpStatusCode.NotFound));
        using var service = new FakeService(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => CollectAsync(service, Request()));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Contains("not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stream_error_line_throws()
    {
        var handler = new FakeHandler(() => Ndjson(Chunk("a"), """{"error":"out of memory"}"""));
        using var service = new FakeService(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => CollectAsync(service, Request()));

        Assert.Contains("out of memory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Streaming_empty_answer_cut_by_length_throws_the_context_error()
    {
        var handler = new FakeHandler(() => Ndjson(Final("length", prompt: 8191, eval: 1)));
        using var service = new FakeService(handler);

        var ex = await Assert.ThrowsAsync<OllamaContextTooSmallException>(() =>
            CollectAsync(service, Request(tweak: r => WithOptions(r with { MaxTokens = 1000 }, o => o["num_ctx"] = 8192))));

        Assert.Equal(8192, ex.ConfiguredContext);
        Assert.Equal(8191, ex.PromptTokens);
    }

    [Fact]
    public async Task A_silent_stream_fails_with_a_timeout_naming_the_inactivity_setting()
    {
        var handler = new FakeHandler((Func<HttpResponseMessage>?)null);
        using var service = new FakeService(handler);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            CollectAsync(service, Request(tweak: r => r with { StreamingInactivityTimeoutSeconds = 1 })));

        Assert.Contains("StreamingInactivityTimeoutSeconds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Name_is_ollama_and_token_estimate_is_chars_over_four()
    {
        using var service = new FakeService(new FakeHandler());
        Assert.Equal("ollama", service.Name);
        Assert.Equal(3, service.EstimateTokenCount("abcdefghi"));
    }
}
