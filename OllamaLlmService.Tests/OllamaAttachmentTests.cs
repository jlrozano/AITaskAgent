using System.Net;
using System.Text;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.Resilience;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace AITaskAgent.Ollama.Tests;

/// <summary>Image attachments on the native Ollama wire format (<c>images[]</c> base64), PDF rejected, no base64 in logs.</summary>
public class OllamaAttachmentTests
{
    private const string PngB64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGD4DwABBAEAHnOcQAAAAABJRU5ErkJggg==";

    private static MessageAttachment Png() => new() { Kind = AttachmentKind.Image, MediaType = "image/png", DataBase64 = PngB64 };

    private static MessageAttachment Pdf() => new() { Kind = AttachmentKind.Pdf, MediaType = "application/pdf", DataBase64 = "JVBERi0=", Name = "d.pdf" };

    private static LlmRequest Request(string text, params MessageAttachment[] attachments)
    {
        var ctx = new ConversationContext();
        ctx.AddUserMessage(text, attachments);
        return new LlmRequest { BaseUrl = "http://fake.invalid:11434", Model = "gemma:26b", SystemPrompt = "sistema", Conversation = ctx };
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

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"model":"m","message":{"role":"assistant","content":"ok"},"done":true,"done_reason":"stop"}""", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FakeService(HttpMessageHandler handler, ILogger logger)
        : OllamaLlmService(logger, new RetryPolicy { MaxAttempts = 1, InitialDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(1) })
    {
        protected internal override HttpMessageHandler CreateHttpMessageHandler() => handler;
    }

    [Fact]
    public async Task Image_travels_as_base64_in_the_images_array_of_the_user_message()
    {
        var handler = new RecordingHandler();
        using var service = new FakeService(handler, new CapturingLogger());

        var result = await service.InvokeAsync(Request("que ves", Png()));

        Assert.True(result.IsSuccess, result.Error?.Message);
        var messages = JObject.Parse(Assert.Single(handler.Bodies))["messages"]!;
        Assert.True(JToken.DeepEquals(
            JObject.Parse($$"""{"role":"user","content":"que ves","images":["{{PngB64}}"]}"""), messages[1]));
    }

    [Fact]
    public void Message_without_attachments_has_no_images_field()
    {
        var user = OllamaLlmService.BuildRequestBody(Request("hola"), stream: false)["messages"]![1]!;
        Assert.Null(user["images"]);
        Assert.True(JToken.DeepEquals(JObject.Parse("""{"role":"user","content":"hola"}"""), user));
    }

    [Fact]
    public async Task Pdf_fails_with_a_clear_non_retriable_error_and_sends_nothing()
    {
        var handler = new RecordingHandler();
        using var service = new FakeService(handler, new CapturingLogger());

        var result = await service.InvokeAsync(Request("lee", Pdf()));

        Assert.False(result.IsSuccess);
        Assert.False(result.IsRetriable);
        Assert.IsType<NotSupportedException>(result.Error);
        Assert.Contains("no admite PDF", result.Error!.Message);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task Pdf_in_streaming_throws_the_same_clear_error()
    {
        using var service = new FakeService(new RecordingHandler(), new CapturingLogger());

        var ex = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in service.InvokeStreamingAsync(Request("lee", Pdf()), CancellationToken.None)) { }
        });
        Assert.Contains("no admite PDF", ex.Message);
    }

    [Fact]
    public async Task Request_log_has_no_base64()
    {
        var logger = new CapturingLogger();
        var handler = new RecordingHandler();
        using var service = new FakeService(handler, logger);

        await service.InvokeAsync(Request("mira", Png()));

        Assert.Contains(PngB64, handler.Bodies[0]);
        Assert.DoesNotContain(logger.Messages, m => m.Contains(PngB64));
        Assert.Contains(logger.Messages, m => m.Contains("base64 chars omitted"));
    }
}
