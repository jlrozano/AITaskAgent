using System.Net;
using System.Text;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using Microsoft.Extensions.Logging;

namespace OpenAILLmService.Tests;

/// <summary>
/// The log line written when the service builds the HTTP client of a base URL and key (audit H-11): it used to carry the
/// first four characters of the API key. Now it only says whether a key is set and how long it is.
/// </summary>
public class ApiKeyLoggingTests
{
    private const string Answer = """
        {"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"model":"fake-model"}
        """;

    private sealed class CapturingLogger : ILogger
    {
        private readonly Lock _lock = new();
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_lock)
                {
                    return [.. _messages];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lock)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Answer, Encoding.UTF8, "application/json") });
    }

    private sealed class FakeTransportService(ILogger logger) : OpenAILlmService(logger)
    {
        protected internal override HttpMessageHandler CreateHttpMessageHandler() => new OkHandler();
    }

    [Fact]
    public async Task Creating_the_client_logs_the_key_length_and_no_part_of_the_key()
    {
        const string key = "sk-SECRETPREFIX-0123456789";
        var logger = new CapturingLogger();
        using var service = new FakeTransportService(logger);

        var result = await service.InvokeAsync(new LlmRequest
        {
            BaseUrl = "https://fake.invalid/v1",
            Model = "fake-model",
            ApiKey = key,
            Conversation = new ConversationContext()
        });

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Contains($"ApiKey: set (length {key.Length})", logger.Messages);
        Assert.DoesNotContain(logger.Messages, m => m.Contains(key[..4], StringComparison.Ordinal));
    }
}
