using System.Net;
using System.Text;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Support;
using AITaskAgent.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OpenAILLmService.Tests;

/// <summary>
/// OpenAI Responses API services (<see cref="OpenAIResponsesLlmService"/>, <see cref="AzureOpenAIResponsesLlmService"/>):
/// request body, answer parsing (plain and SSE), HTTP errors, Azure URL shapes and the API key staying out of the logs.
/// No network: a fake handler answers and records what was sent.
/// </summary>
public class ResponsesLlmServiceTests
{
    private const string Secret = "sk-very-secret-key-123";
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(20);

    // ------------------------------------------------------------------ helpers

    private sealed class Recorder
    {
        public List<(Uri Uri, string Body)> Requests { get; } = [];
        public JObject LastBody => JObject.Parse(Requests[^1].Body);
        public Uri LastUri => Requests[^1].Uri;
    }

    private sealed class ScriptedHandler(Recorder recorder, Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            recorder.Requests.Add((request.RequestUri!, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return respond(recorder.Requests.Count);
        }
    }

    private sealed class FakeService(HttpMessageHandler handler, ILogger logger, RetryPolicy? policy = null)
        : OpenAIResponsesLlmService(logger, policy)
    {
        protected internal override HttpMessageHandler CreateHttpMessageHandler() => handler;
    }

    private static readonly RetryPolicy NoRetry = new() { MaxAttempts = 1, InitialDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(5) };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Sse(params string[] events) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Concat(events.Select(Ev)), Encoding.UTF8, "text/event-stream")
        };

    private static string Ev(string json) => $"data: {json}\n\n";

    private static FakeService Service(Recorder rec, Func<int, HttpResponseMessage> respond, ILogger? logger = null, RetryPolicy? policy = null) =>
        new(new ScriptedHandler(rec, respond), logger ?? NullLogger.Instance, policy ?? NoRetry);

    private static ConversationContext Conversation() => new();

    private static LlmRequest Request(ConversationContext? conversation = null, string model = "gpt-6-luna", JObject? options = null, bool tools = false) => new()
    {
        BaseUrl = "https://fake.invalid/v1",
        ApiKey = Secret,
        Model = model,
        Conversation = conversation ?? Conversation(),
        Options = options ?? [],
        Tools = tools
            ? [new ToolDefinition { Name = "read_file", Description = "Reads a file", ParametersJsonSchema = """{"type":"object","properties":{"path":{"type":"string"}}}""" }]
            : null
    };

    private static string Answer(string output, string extra = "") =>
        $$"""{"id":"resp_1","model":"gpt-6-luna","status":"completed","output":[{{output}}]{{extra}}}""";

    private const string TextOutput = """{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Hola"}]}""";

    private static async Task<List<LlmStreamChunk>> StreamAsync(FakeService service, LlmRequest request)
    {
        var chunks = new List<LlmStreamChunk>();
        async Task Collect()
        {
            await foreach (var c in service.InvokeStreamingAsync(request)) chunks.Add(c);
        }
        await Collect().WaitAsync(Guard);
        return chunks;
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
            lock (_lock) _messages.Add(formatter(state, exception) + exception);
        }
    }

    // ------------------------------------------------------------------ names and endpoints

    [Fact]
    public void Names_identify_the_services()
    {
        Assert.Equal("openai-responses", new OpenAIResponsesLlmService(NullLogger<OpenAIResponsesLlmService>.Instance).Name);
        Assert.Equal("azure-openai-responses", new AzureOpenAIResponsesLlmService(NullLogger<AzureOpenAIResponsesLlmService>.Instance).Name);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1/responses")]
    [InlineData("https://gw.example/v1", "https://gw.example/v1/responses")]
    public void OpenAI_endpoint_is_base_url_plus_responses(string baseUrl, string expected) =>
        Assert.Equal(expected, new OpenAIResponsesLlmService(NullLogger<OpenAIResponsesLlmService>.Instance)
            .GetEndpoint(Request() with { BaseUrl = baseUrl }));

    private static AzureOpenAIResponsesLlmService Azure() => new(NullLogger<AzureOpenAIResponsesLlmService>.Instance);

    [Fact]
    public void Azure_v1_mode_when_there_is_no_api_version()
    {
        var req = Request() with { BaseUrl = "https://res.openai.azure.com/", Model = "my-deploy", ApiVersion = null };
        Assert.Equal("https://res.openai.azure.com/openai/v1/responses", Azure().GetEndpoint(req));
        Assert.Equal("https://res.openai.azure.com/openai/v1/responses", Azure().GetEndpoint(req with { ApiVersion = "  " }));
    }

    [Fact]
    public void Azure_legacy_mode_when_there_is_an_api_version()
    {
        var req = Request() with { BaseUrl = "https://res.openai.azure.com", Model = "my-deploy", ApiVersion = "2025-04-01-preview" };
        Assert.Equal("https://res.openai.azure.com/openai/responses?api-version=2025-04-01-preview", Azure().GetEndpoint(req));
    }

    [Fact]
    public void Azure_without_base_url_or_model_fails_loudly()
    {
        Assert.Throws<InvalidOperationException>(() => Azure().GetEndpoint(Request() with { BaseUrl = null }));
        Assert.Throws<InvalidOperationException>(() => Azure().GetEndpoint(Request() with { Model = "" }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2025-04-01-preview")]
    public void Azure_authenticates_with_the_api_key_header_in_both_modes(string? apiVersion)
    {
        using var service = Azure();
        var client = service.GetOrCreateClient(Request() with { BaseUrl = "https://res.openai.azure.com", ApiVersion = apiVersion });

        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.Equal(Secret, Assert.Single(client.DefaultRequestHeaders.GetValues("api-key")));
    }

    [Fact]
    public void OpenAI_authenticates_with_a_bearer_token()
    {
        using var service = new OpenAIResponsesLlmService(NullLogger<OpenAIResponsesLlmService>.Instance);
        var client = service.GetOrCreateClient(Request());

        Assert.Equal("Bearer", client.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.Equal(Secret, client.DefaultRequestHeaders.Authorization?.Parameter);
    }

    // ------------------------------------------------------------------ request body

    [Fact]
    public void Body_maps_system_prompt_conversation_tool_calls_and_tools()
    {
        var conversation = Conversation()
            .AddUserMessage("lee a.txt")
            .AddAssistantMessageWithToolCalls(
                [new ToolCall { Id = "call_1", Name = "read_file", Arguments = """{"path":"a.txt"}""" }], "voy")
            .AddToolMessage("call_1", "contenido")
            .AddAssistantMessage("listo");
        var request = Request(conversation, tools: true) with { SystemPrompt = "Eres útil", MaxTokens = 500, User = "u1" };

        var body = OpenAIResponsesLlmService.BuildResponsesRequestBody(request);

        Assert.Equal("gpt-6-luna", (string?)body["model"]);
        Assert.Equal("Eres útil", (string?)body["instructions"]);
        Assert.False((bool)body["store"]!);
        Assert.Equal(500, (int)body["max_output_tokens"]!);
        Assert.Equal("u1", (string?)body["user"]);
        Assert.Null(body["messages"]);
        Assert.Null(body["stream"]);

        var input = (JArray)body["input"]!;
        Assert.Equal(5, input.Count);
        Assert.Equal("message", (string?)input[0]["type"]);
        Assert.Equal("user", (string?)input[0]["role"]);
        Assert.Equal("input_text", (string?)input[0]["content"]![0]!["type"]);
        Assert.Equal("lee a.txt", (string?)input[0]["content"]![0]!["text"]);
        Assert.Equal("assistant", (string?)input[1]["role"]);
        Assert.Equal("output_text", (string?)input[1]["content"]![0]!["type"]);
        Assert.Equal("function_call", (string?)input[2]["type"]);
        Assert.Equal("call_1", (string?)input[2]["call_id"]);
        Assert.Equal("read_file", (string?)input[2]["name"]);
        Assert.Equal("""{"path":"a.txt"}""", (string?)input[2]["arguments"]);
        Assert.Null(input[2]["id"]);
        Assert.Equal("function_call_output", (string?)input[3]["type"]);
        Assert.Equal("call_1", (string?)input[3]["call_id"]);
        Assert.Equal("contenido", (string?)input[3]["output"]);
        Assert.Equal("listo", (string?)input[4]["content"]![0]!["text"]);

        var tool = Assert.Single((JArray)body["tools"]!);
        Assert.Equal("function", (string?)tool["type"]);
        Assert.Equal("read_file", (string?)tool["name"]);
        Assert.Equal("Reads a file", (string?)tool["description"]);
        Assert.Equal("object", (string?)tool["parameters"]!["type"]);
        Assert.Null(tool["function"]);
    }

    [Fact]
    public void Reasoning_effort_becomes_reasoning_object_and_sampling_is_not_sent()
    {
        var request = Request(options: new JObject { ["reasoning_effort"] = "medium", ["reasoning_summary"] = "auto" })
            with { Temperature = 0.3f, TopP = 0.9f };

        var body = OpenAIResponsesLlmService.BuildResponsesRequestBody(request);

        Assert.Equal("medium", (string?)body["reasoning"]!["effort"]);
        Assert.Equal("auto", (string?)body["reasoning"]!["summary"]);
        Assert.Null(body["reasoning_effort"]);
        Assert.Null(body["temperature"]);
        Assert.Null(body["top_p"]);
    }

    [Fact]
    public void Sampling_is_sent_for_non_reasoning_models_and_when_effort_is_none()
    {
        var plain = Request(model: "gpt-4.1") with { Temperature = 0.3f, TopP = 0.9f };
        var plainBody = OpenAIResponsesLlmService.BuildResponsesRequestBody(plain);
        Assert.Equal(0.3, (double)plainBody["temperature"]!, 3);
        Assert.Equal(0.9, (double)plainBody["top_p"]!, 3);

        var none = Request(options: new JObject { ["reasoning_effort"] = "none" }) with { Temperature = 0.3f };
        var noneBody = OpenAIResponsesLlmService.BuildResponsesRequestBody(none);
        Assert.Equal(0.3, (double)noneBody["temperature"]!, 3);
        Assert.Equal("none", (string?)noneBody["reasoning"]!["effort"]);

        // A reasoning model with no effort configured does not take sampling either.
        var guess = Request(model: "gpt-5.2") with { Temperature = 0.3f };
        Assert.Null(OpenAIResponsesLlmService.BuildResponsesRequestBody(guess)["temperature"]);
    }

    [Fact]
    public void Free_options_are_forwarded_and_chat_only_ones_are_dropped()
    {
        var options = new JObject
        {
            ["service_tier"] = "flex",
            ["parallel_tool_calls"] = false,
            ["metadata"] = new JObject { ["k"] = "v" },
            ["MaxConcurrentRequests"] = 3,
            ["stream_options"] = new JObject { ["include_usage"] = true },
            ["max_completion_tokens"] = 10,
            ["verbosity"] = "low"
        };

        var body = OpenAIResponsesLlmService.BuildResponsesRequestBody(Request(options: options), stream: true);

        Assert.Equal("flex", (string?)body["service_tier"]);
        Assert.False((bool)body["parallel_tool_calls"]!);
        Assert.Equal("v", (string?)body["metadata"]!["k"]);
        Assert.True((bool)body["stream"]!);
        Assert.Equal("low", (string?)body["text"]!["verbosity"]);
        Assert.Null(body["MaxConcurrentRequests"]);
        Assert.Null(body["stream_options"]);
        Assert.Null(body["max_completion_tokens"]);
    }

    [Fact]
    public async Task Response_format_goes_to_text_format()
    {
        var json = Request() with { ResponseFormat = new ResponseFormatOptions { Type = ResponseFormatType.JsonObject } };
        Assert.Equal("json_object", (string?)OpenAIResponsesLlmService.BuildResponsesRequestBody(json)["text"]!["format"]!["type"]);

        var schema = await NJsonSchema.JsonSchema.FromJsonAsync("""{"type":"object","properties":{"a":{"type":"string"}}}""");
        var structured = Request() with { ResponseFormat = new ResponseFormatOptions { Type = ResponseFormatType.JsonObject, JsonSchema = schema } };
        var format = OpenAIResponsesLlmService.BuildResponsesRequestBody(structured)["text"]!["format"]!;
        Assert.Equal("json_schema", (string?)format["type"]);
        Assert.Equal("custom_schema", (string?)format["name"]);
        Assert.True((bool)format["strict"]!);
        Assert.Equal("object", (string?)format["schema"]!["type"]);
    }

    [Fact]
    public async Task Request_goes_to_the_responses_url_and_sends_the_body()
    {
        var rec = new Recorder();
        using var service = Service(rec, _ => Json(HttpStatusCode.OK, Answer(TextOutput)));

        await service.InvokeAsync(Request(Conversation().AddUserMessage("hola")) with { SystemPrompt = "sys" });

        Assert.Equal("https://fake.invalid/v1/responses", rec.LastUri.ToString());
        Assert.Equal("sys", (string?)rec.LastBody["instructions"]);
    }

    // ------------------------------------------------------------------ non streaming answers

    [Fact]
    public async Task Text_answer_with_usage()
    {
        var usage = ""","usage":{"input_tokens":100,"output_tokens":40,"total_tokens":140,"input_tokens_details":{"cached_tokens":64},"output_tokens_details":{"reasoning_tokens":25}}""";
        var rec = new Recorder();
        using var service = Service(rec, _ => Json(HttpStatusCode.OK, Answer(TextOutput, usage)));

        var result = await service.InvokeAsync(Request());

        Assert.True(result.IsSuccess);
        Assert.Equal("Hola", result.Content);
        Assert.Equal(FinishReason.Stop, result.FinishReason);
        Assert.Null(result.ToolCalls);
        Assert.Equal(100, result.PromptTokens);
        Assert.Equal(40, result.CompletionTokens);
        Assert.Equal(140, result.TokensUsed);
        Assert.Equal(25, result.ReasoningTokens);
        Assert.Equal(64, result.CachedPromptTokens);
        Assert.Equal("gpt-6-luna", result.Model);
    }

    [Fact]
    public async Task Tool_calls_use_call_id_and_finish_with_tool_calls()
    {
        var output = """
            {"type":"reasoning","id":"rs_1","summary":[]},
            {"type":"function_call","id":"fc_1","call_id":"call_abc","name":"read_file","arguments":"{\"path\":\"a.txt\"}","status":"completed"},
            {"type":"function_call","id":"fc_2","call_id":"call_def","name":"read_file","arguments":"{\"path\":\"b.txt\"}","status":"completed"}
            """;
        var rec = new Recorder();
        using var service = Service(rec, _ => Json(HttpStatusCode.OK, Answer(output)));

        var result = await service.InvokeAsync(Request(tools: true));

        Assert.Equal(FinishReason.ToolCalls, result.FinishReason);
        Assert.Equal(2, result.ToolCalls!.Count);
        Assert.Equal("call_abc", result.ToolCalls[0].Id);
        Assert.Equal("read_file", result.ToolCalls[0].Name);
        Assert.Equal("""{"path":"a.txt"}""", result.ToolCalls[0].Arguments);
        Assert.Equal("call_def", result.ToolCalls[1].Id);
    }

    [Fact]
    public async Task Reasoning_summary_is_returned_as_reasoning()
    {
        var output = """
            {"type":"reasoning","id":"rs_1","summary":[{"type":"summary_text","text":"Primero A."},{"type":"summary_text","text":"Luego B."}]},
            {"type":"message","role":"assistant","content":[{"type":"output_text","text":"Hola"},{"type":"output_text","text":" mundo"}]}
            """;
        var rec = new Recorder();
        using var service = Service(rec, _ => Json(HttpStatusCode.OK, Answer(output)));

        var result = await service.InvokeAsync(Request());

        Assert.Equal("Primero A.\n\nLuego B.", result.Reasoning);
        Assert.Equal("Hola mundo", result.Content);
    }

    [Theory]
    [InlineData("max_output_tokens", FinishReason.Length)]
    [InlineData("content_filter", FinishReason.ContentFilter)]
    [InlineData("other_reason", FinishReason.Other)]
    public async Task Incomplete_response_maps_the_finish_reason(string reason, FinishReason expected)
    {
        var body = $$"""{"status":"incomplete","incomplete_details":{"reason":"{{reason}}"},"model":"m","output":[{{TextOutput}}]}""";
        var rec = new Recorder();
        using var service = Service(rec, _ => Json(HttpStatusCode.OK, body));

        var result = await service.InvokeAsync(Request());

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.FinishReason);
        Assert.Equal("Hola", result.Content);
    }

    [Theory]
    [InlineData("server_error", true)]
    [InlineData("invalid_prompt", false)]
    public async Task A_failed_response_is_a_failure_with_the_provider_message(string code, bool retriable)
    {
        var body = $$"""{"status":"failed","error":{"code":"{{code}}","message":"it broke"},"output":[]}""";
        var rec = new Recorder();
        using var service = Service(rec, _ => Json(HttpStatusCode.OK, body));

        var result = await service.InvokeAsync(Request());

        Assert.False(result.IsSuccess);
        Assert.Equal(retriable, result.IsRetriable);
        Assert.Contains("it broke", result.Error!.Message);
    }

    // ------------------------------------------------------------------ HTTP errors

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    public async Task Http_errors_fail_with_the_provider_body_and_the_right_retriability(HttpStatusCode status, bool retriable)
    {
        const string errorBody = """{"error":{"message":"Unsupported parameter: foo","type":"invalid_request_error"}}""";
        var rec = new Recorder();
        using var service = Service(rec, _ => Json(status, errorBody));

        var result = await service.InvokeAsync(Request());

        Assert.False(result.IsSuccess);
        Assert.Equal(retriable, result.IsRetriable);
        Assert.Contains($"HTTP {(int)status}", result.Error!.Message);
        Assert.Contains("Unsupported parameter: foo", result.Error.Message);
        Assert.DoesNotContain(Secret, result.Error.Message);
    }

    [Fact]
    public async Task A_transient_error_is_retried_and_a_bad_request_is_not()
    {
        var rec = new Recorder();
        var policy = new RetryPolicy { MaxAttempts = 3, InitialDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(5) };
        using var retried = Service(rec, n => n == 1 ? Json(HttpStatusCode.InternalServerError, "{}") : Json(HttpStatusCode.OK, Answer(TextOutput)), policy: policy);
        Assert.True((await retried.InvokeAsync(Request())).IsSuccess);
        Assert.Equal(2, rec.Requests.Count);

        var rec2 = new Recorder();
        using var notRetried = Service(rec2, _ => Json(HttpStatusCode.BadRequest, "{}"), policy: policy);
        Assert.False((await notRetried.InvokeAsync(Request())).IsSuccess);
        Assert.Single(rec2.Requests);
    }

    // ------------------------------------------------------------------ streaming

    [Fact]
    public async Task Streaming_text_reasoning_and_usage()
    {
        var rec = new Recorder();
        using var service = Service(rec, _ => Sse(
            """{"type":"response.created","response":{"status":"in_progress"}}""",
            """{"type":"response.reasoning_summary_part.added","item_id":"rs_1","summary_index":0}""",
            """{"type":"response.reasoning_summary_text.delta","item_id":"rs_1","delta":"Pienso"}""",
            """{"type":"response.reasoning_summary_text.delta","item_id":"rs_1","delta":" bien"}""",
            """{"type":"response.output_text.delta","item_id":"msg_1","delta":"Ho"}""",
            """{"type":"response.output_text.delta","item_id":"msg_1","delta":"la"}""",
            """{"type":"response.completed","response":{"status":"completed","output":[{"type":"message"}],"usage":{"input_tokens":10,"output_tokens":7,"total_tokens":17,"input_tokens_details":{"cached_tokens":4},"output_tokens_details":{"reasoning_tokens":3}}}}"""));

        var chunks = await StreamAsync(service, Request());

        Assert.Equal("Pienso bien", string.Concat(chunks.Where(c => c.IsThinking).Select(c => c.Delta)));
        Assert.Equal("Hola", string.Concat(chunks.Where(c => !c.IsThinking).Select(c => c.Delta)));
        var last = chunks[^1];
        Assert.True(last.IsComplete);
        Assert.Equal(FinishReason.Stop, last.FinishReason);
        Assert.Equal(10, last.PromptTokens);
        Assert.Equal(7, last.CompletionTokens);
        Assert.Equal(17, last.TokensUsed);
        Assert.Equal(3, last.ReasoningTokens);
        Assert.Equal(4, last.CachedPromptTokens);
        Assert.Single(chunks, c => c.IsComplete);
        Assert.True((bool)rec.LastBody["stream"]!);
        Assert.Null(rec.LastBody["stream_options"]);
    }

    [Fact]
    public async Task Streaming_tool_call_by_argument_deltas()
    {
        var rec = new Recorder();
        using var service = Service(rec, _ => Sse(
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"reasoning","id":"rs_1"}}""",
            """{"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","id":"fc_1","call_id":"call_9","name":"read_file","arguments":""}}""",
            """{"type":"response.function_call_arguments.delta","item_id":"fc_1","output_index":1,"delta":"{\"path\":"}""",
            """{"type":"response.function_call_arguments.delta","item_id":"fc_1","output_index":1,"delta":"\"a.txt\"}"}""",
            """{"type":"response.function_call_arguments.done","item_id":"fc_1","output_index":1,"arguments":"{\"path\":\"a.txt\"}"}""",
            """{"type":"response.output_item.done","output_index":1,"item":{"type":"function_call","id":"fc_1","call_id":"call_9","name":"read_file","arguments":"{\"path\":\"a.txt\"}"}}""",
            """{"type":"response.completed","response":{"status":"completed","output":[{"type":"function_call","id":"fc_1","call_id":"call_9","name":"read_file","arguments":"{\"path\":\"a.txt\"}"}],"usage":{"input_tokens":5,"output_tokens":2,"total_tokens":7}}}"""));

        var chunks = await StreamAsync(service, Request(tools: true));

        var updates = chunks.SelectMany(c => c.ToolCallUpdates ?? []).ToList();
        Assert.All(updates, u => Assert.Equal(0, u.Index));
        Assert.Equal("call_9", updates[0].ToolCallId);
        Assert.Equal("read_file", updates[0].FunctionName);
        Assert.Equal("""{"path":"a.txt"}""", string.Concat(updates.Select(u => u.FunctionArgumentsUpdate)));
        Assert.Equal(FinishReason.ToolCalls, chunks[^1].FinishReason);
        Assert.Equal(7, chunks[^1].TokensUsed);
    }

    [Fact]
    public async Task Streaming_two_tool_calls_get_distinct_indexes_and_a_call_without_deltas_is_completed_on_done()
    {
        var rec = new Recorder();
        using var service = Service(rec, _ => Sse(
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"fc_1","call_id":"c1","name":"read_file","arguments":""}}""",
            """{"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","id":"fc_2","call_id":"c2","name":"read_file","arguments":""}}""",
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","id":"fc_1","call_id":"c1","name":"read_file","arguments":"{\"path\":\"a\"}"}}""",
            """{"type":"response.function_call_arguments.delta","item_id":"fc_2","output_index":1,"delta":"{\"path\":\"b\"}"}""",
            """{"type":"response.completed","response":{"status":"completed","output":[]}}"""));

        var chunks = await StreamAsync(service, Request(tools: true));

        var updates = chunks.SelectMany(c => c.ToolCallUpdates ?? []).ToList();
        Assert.Equal("""{"path":"a"}""", string.Concat(updates.Where(u => u.Index == 0).Select(u => u.FunctionArgumentsUpdate)));
        Assert.Equal("""{"path":"b"}""", string.Concat(updates.Where(u => u.Index == 1).Select(u => u.FunctionArgumentsUpdate)));
        Assert.Equal("c2", updates.First(u => u.Index == 1).ToolCallId);
        Assert.Equal(FinishReason.ToolCalls, chunks[^1].FinishReason);
    }

    [Fact]
    public async Task Streaming_incomplete_maps_to_length()
    {
        var rec = new Recorder();
        using var service = Service(rec, _ => Sse(
            """{"type":"response.output_text.delta","delta":"cortad"}""",
            """{"type":"response.incomplete","response":{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}}"""));

        var chunks = await StreamAsync(service, Request());

        Assert.Equal(FinishReason.Length, chunks[^1].FinishReason);
        Assert.Equal(2, chunks[^1].TokensUsed);
    }

    [Fact]
    public async Task Streaming_without_a_terminal_event_ends_as_other_with_a_warning()
    {
        var logger = new CapturingLogger();
        var rec = new Recorder();
        using var service = Service(rec, _ => Sse("""{"type":"response.output_text.delta","delta":"hola"}"""), logger);

        var chunks = await StreamAsync(service, Request());

        Assert.Equal(FinishReason.Other, chunks[^1].FinishReason);
        Assert.Equal(OpenAILlmService.StreamClosedWithoutFinishReason, chunks[^1].RawFinishReason);
        Assert.Contains(logger.Messages, m => m.Contains("without a terminal"));
    }

    [Fact]
    public async Task Streaming_failed_event_and_http_error_throw_with_the_provider_message()
    {
        var rec = new Recorder();
        using var failed = Service(rec, _ => Sse("""{"type":"response.failed","response":{"status":"failed","error":{"code":"server_error","message":"boom"}}}"""));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => StreamAsync(failed, Request()));
        Assert.Contains("boom", ex.Message);

        using var http = Service(new Recorder(), _ => Json(HttpStatusCode.TooManyRequests, """{"error":{"message":"slow down"}}"""));
        var ex2 = await Assert.ThrowsAsync<HttpRequestException>(() => StreamAsync(http, Request()));
        Assert.Contains("429", ex2.Message);
        Assert.Contains("slow down", ex2.Message);
        Assert.Equal(HttpStatusCode.TooManyRequests, ex2.StatusCode);
    }

    // ------------------------------------------------------------------ the key never reaches the logs

    [Fact]
    public async Task The_api_key_never_appears_in_the_logs()
    {
        var logger = new CapturingLogger();
        var rec = new Recorder();
        using var service = Service(rec, n => n switch
        {
            1 => Json(HttpStatusCode.OK, Answer(TextOutput)),
            2 => Sse("""{"type":"response.output_text.delta","delta":"x"}""", """{"type":"response.completed","response":{"status":"completed"}}"""),
            _ => Json(HttpStatusCode.BadRequest, """{"error":{"message":"nope"}}""")
        }, logger);

        await service.InvokeAsync(Request());
        await StreamAsync(service, Request());
        await service.InvokeAsync(Request());

        Assert.NotEmpty(logger.Messages);
        Assert.DoesNotContain(logger.Messages, m => m.Contains(Secret));
        Assert.Contains(logger.Messages, m => m.Contains($"set (length {Secret.Length})"));
    }
}
