using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenAILLmService.Tests;

/// <summary>
/// The base class's own hook defaults - what a plain OpenAI-compatible provider (OpenAI itself,
/// Ollama, LM Studio) gets when no subclass overrides anything.
/// </summary>
public class OpenAILlmServiceHooksTests
{
    private static OpenAILlmService Service() => new(NullLogger<OpenAILlmService>.Instance);

    private static LlmRequest Request() => new()
    {
        BaseUrl = "https://api.openai.com/v1",
        Model = "gpt-4o",
        ApiKey = "secret-key",
        Conversation = new ConversationContext()
    };

    [Fact]
    public void Default_authentication_is_a_bearer_token()
    {
        using var client = new HttpClient();

        Service().ConfigureAuthentication(client, Request());

        Assert.Equal("Bearer", client.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.Equal("secret-key", client.DefaultRequestHeaders.Authorization?.Parameter);
    }

    [Fact]
    public void Default_extra_headers_are_none()
    {
        // These used to be OpenRouter's attribution headers, sent unconditionally to every vendor
        // (including plain OpenAI and Azure). Now they live only in OpenRouterLlmService.
        using var client = new HttpClient();

        Service().ConfigureExtraHeaders(client, Request());

        Assert.False(client.DefaultRequestHeaders.Contains("HTTP-Referer"));
        Assert.False(client.DefaultRequestHeaders.Contains("X-Title"));
    }

    [Fact]
    public void Default_endpoint_is_the_plain_chat_completions_path()
    {
        Assert.Equal("https://api.openai.com/v1/chat/completions", Service().GetEndpoint(Request()));
    }
}
