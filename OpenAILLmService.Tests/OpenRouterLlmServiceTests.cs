using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenAILLmService.Tests;

public class OpenRouterLlmServiceTests
{
    private static OpenRouterLlmService Service() =>
        new(NullLogger<OpenRouterLlmService>.Instance);

    private static LlmRequest Request() => new()
    {
        BaseUrl = "https://openrouter.ai/api/v1",
        Model = "test-model",
        ApiKey = "secret-key",
        Conversation = new ConversationContext()
    };

    [Fact]
    public void Name_identifies_the_service_distinctly_from_plain_openai()
    {
        Assert.Equal("openrouter", Service().Name);
    }

    [Fact]
    public void Uses_the_shared_chat_completions_endpoint_shape_unchanged()
    {
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", Service().GetEndpoint(Request()));
    }

    [Fact]
    public void Attribution_headers_identify_this_app_to_openrouters_own_dashboard()
    {
        using var client = new HttpClient();

        Service().ConfigureExtraHeaders(client, Request());

        Assert.Equal("https://github.com/AITaskFramework", Assert.Single(client.DefaultRequestHeaders.GetValues("HTTP-Referer")));
        Assert.Equal("AITaskFramework", Assert.Single(client.DefaultRequestHeaders.GetValues("X-Title")));
    }

    [Fact]
    public void Still_authenticates_with_a_bearer_token_like_plain_openai()
    {
        // OpenRouter's only particularity is the attribution headers; auth is unchanged, so this
        // must not need (and must not get) its own ConfigureAuthentication override.
        using var client = new HttpClient();

        Service().ConfigureAuthentication(client, Request());

        Assert.Equal("Bearer", client.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.Equal("secret-key", client.DefaultRequestHeaders.Authorization?.Parameter);
    }
}
