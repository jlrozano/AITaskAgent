using System.Net;
using System.Text;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace OpenAILLmService.Tests;

/// <summary>
/// Image and PDF attachments of a user message on the OpenAI-family wire formats: Responses API
/// (<c>input_image</c> / <c>input_file</c>), chat completions (<c>image_url</c> parts, PDF rejected), request logs
/// without base64, and unchanged bodies for messages without attachments. No network.
/// </summary>
public class MultimodalAttachmentTests
{
    private const string PngB64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGD4DwABBAEAHnOcQAAAAABJRU5ErkJggg==";
    private const string PdfB64 = "JVBERi0xLjQKJSVFT0YK";

    private static readonly RetryPolicy NoRetry = new() { MaxAttempts = 1, InitialDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(5) };

    private static MessageAttachment Png() => new() { Kind = AttachmentKind.Image, MediaType = "image/png", DataBase64 = PngB64, Name = "a.png" };

    private static MessageAttachment Pdf() => new() { Kind = AttachmentKind.Pdf, MediaType = "application/pdf", DataBase64 = PdfB64, Name = "doc.pdf" };

    private static LlmRequest Request(string text, params MessageAttachment[] attachments)
    {
        var conversation = new ConversationContext();
        conversation.AddUserMessage(text, attachments);
        return new LlmRequest
        {
            BaseUrl = "https://fake.invalid/v1",
            ApiKey = "k",
            Model = "gpt-6-luna",
            Conversation = conversation
        };
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

    private sealed class RecordingHandler(string answer) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }

    private const string ChatAnswer = """{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"model":"m"}""";
    private const string ResponsesAnswer = """{"id":"r","model":"m","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}]}""";

    private sealed class ChatService(HttpMessageHandler handler, ILogger logger) : OpenAILlmService(logger, NoRetry)
    {
        protected internal override HttpMessageHandler CreateHttpMessageHandler() => handler;
    }

    private sealed class ResponsesService(HttpMessageHandler handler, ILogger logger) : OpenAIResponsesLlmService(logger, NoRetry)
    {
        protected internal override HttpMessageHandler CreateHttpMessageHandler() => handler;
    }

    // ------------------------------------------------------------------ Responses API

    [Fact]
    public async Task Responses_sends_input_image_and_input_file_with_data_urls_over_http()
    {
        var handler = new RecordingHandler(ResponsesAnswer);
        using var service = new ResponsesService(handler, NullLogger.Instance);

        var result = await service.InvokeAsync(Request("mira esto", Png(), Pdf()));

        Assert.True(result.IsSuccess, result.Error?.Message);
        var content = JObject.Parse(Assert.Single(handler.Bodies))["input"]![0]!["content"]!;
        Assert.Equal("user", JObject.Parse(handler.Bodies[0])["input"]![0]!["role"]!.Value<string>());
        Assert.True(JToken.DeepEquals(JArray.Parse($$"""
            [
              {"type":"input_text","text":"mira esto"},
              {"type":"input_image","image_url":"data:image/png;base64,{{PngB64}}"},
              {"type":"input_file","filename":"doc.pdf","file_data":"data:application/pdf;base64,{{PdfB64}}"}
            ]
            """), content));
    }

    [Fact]
    public void Responses_pdf_without_a_name_gets_a_default_filename()
    {
        var pdf = Pdf() with { Name = null };
        var body = OpenAIResponsesLlmService.BuildResponsesRequestBody(Request("x", pdf));
        Assert.Equal("document.pdf", body["input"]![0]!["content"]![1]!["filename"]!.Value<string>());
    }

    [Fact]
    public void Responses_message_without_attachments_is_unchanged()
    {
        var body = OpenAIResponsesLlmService.BuildResponsesRequestBody(Request("hola"));
        Assert.True(JToken.DeepEquals(
            JArray.Parse("""[{"type":"message","role":"user","content":[{"type":"input_text","text":"hola"}]}]"""),
            body["input"]));
    }

    [Fact]
    public async Task Responses_request_log_has_no_base64()
    {
        var logger = new CapturingLogger();
        var handler = new RecordingHandler(ResponsesAnswer);
        using var service = new ResponsesService(handler, logger);

        var result = await service.InvokeAsync(Request("mira", Png(), Pdf()));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Contains(PngB64, handler.Bodies[0]);
        Assert.DoesNotContain(logger.Messages, m => m.Contains(PngB64) || m.Contains(PdfB64));
        Assert.Contains(logger.Messages, m => m.Contains("base64 chars omitted"));
    }

    // ------------------------------------------------------------------ chat completions

    [Fact]
    public async Task Chat_sends_text_and_image_url_parts_over_http()
    {
        var handler = new RecordingHandler(ChatAnswer);
        using var service = new ChatService(handler, NullLogger.Instance);

        var result = await service.InvokeAsync(Request("que ves", Png()));

        Assert.True(result.IsSuccess, result.Error?.Message);
        var message = JObject.Parse(Assert.Single(handler.Bodies))["messages"]![0]!;
        Assert.Equal("user", message["role"]!.Value<string>());
        Assert.True(JToken.DeepEquals(JArray.Parse($$$"""
            [
              {"type":"text","text":"que ves"},
              {"type":"image_url","image_url":{"url":"data:image/png;base64,{{{PngB64}}}","detail":"auto"}}
            ]
            """), message["content"]));
    }

    [Fact]
    public void Chat_message_without_attachments_keeps_a_plain_string_content()
    {
        var body = OpenAILlmService.BuildRequestBody(Request("hola"));
        Assert.Equal(JTokenType.String, body["messages"]![0]!["content"]!.Type);
        Assert.Equal("hola", body["messages"]![0]!["content"]!.Value<string>());
    }

    [Fact]
    public async Task Chat_pdf_fails_with_a_clear_non_retriable_error_and_sends_nothing()
    {
        var handler = new RecordingHandler(ChatAnswer);
        using var service = new ChatService(handler, NullLogger.Instance);

        var result = await service.InvokeAsync(Request("lee", Pdf()));

        Assert.False(result.IsSuccess);
        Assert.False(result.IsRetriable);
        Assert.IsType<NotSupportedException>(result.Error);
        Assert.Contains("PDF solo con Responses API", result.Error!.Message);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task Chat_pdf_in_streaming_throws_the_same_clear_error()
    {
        var handler = new RecordingHandler(ChatAnswer);
        using var service = new ChatService(handler, NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in service.InvokeStreamingAsync(Request("lee", Pdf()))) { }
        });

        Assert.Contains("PDF solo con Responses API", ex.Message);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task Chat_request_log_has_no_base64()
    {
        var logger = new CapturingLogger();
        var handler = new RecordingHandler(ChatAnswer);
        using var service = new ChatService(handler, logger);

        await service.InvokeAsync(Request("mira", Png()));

        Assert.Contains(PngB64, handler.Bodies[0]);
        Assert.DoesNotContain(logger.Messages, m => m.Contains(PngB64));
        Assert.Contains(logger.Messages, m => m.Contains("data:image/png;base64,<"));
    }

    [Fact]
    public void Azure_chat_uses_the_same_body_as_openai_chat()
    {
        // AzureOpenAILlmService only changes URL and auth; the body comes from the shared builder.
        var body = OpenAILlmService.BuildRequestBody(Request("hola", Png()));
        Assert.Equal("image_url", body["messages"]![0]!["content"]![1]!["type"]!.Value<string>());
    }

    [Fact]
    public void Attachment_to_string_never_prints_the_base64()
    {
        Assert.DoesNotContain(PngB64, Png().ToString());
        Assert.DoesNotContain(PngB64, new Message { Role = "user", Content = "x", Attachments = [Png()] }.ToString());
    }
}
