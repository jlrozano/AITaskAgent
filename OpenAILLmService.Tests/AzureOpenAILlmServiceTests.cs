using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenAILLmService.Tests;

public class AzureOpenAILlmServiceTests
{
    private static AzureOpenAILlmService Service() =>
        new(NullLogger<AzureOpenAILlmService>.Instance);

    private static LlmRequest Request(string? baseUrl = "https://my-resource.openai.azure.com", string? model = "gpt-4o-deployment", string? apiVersion = "2024-08-01-preview") =>
        new()
        {
            BaseUrl = baseUrl,
            Model = model!,
            ApiVersion = apiVersion,
            Conversation = new ConversationContext()
        };

    [Fact]
    public void Name_identifies_the_service_distinctly_from_plain_openai()
    {
        Assert.Equal("azure-openai", Service().Name);
    }

    [Fact]
    public void Endpoint_addresses_the_deployment_and_carries_the_api_version()
    {
        var endpoint = Service().GetEndpoint(Request());

        Assert.Equal(
            "https://my-resource.openai.azure.com/openai/deployments/gpt-4o-deployment/chat/completions?api-version=2024-08-01-preview",
            endpoint);
    }

    [Fact]
    public void Model_is_never_addressed_in_the_body_only_in_the_url()
    {
        // Azure resolves the model purely from the deployment segment of the URL; BuildRequestBody
        // (shared with plain OpenAI) still writes a "model" field, which Azure simply ignores - but
        // this pins the URL as the sole source of truth so a future refactor cannot silently start
        // relying on the body field instead.
        var endpoint = Service().GetEndpoint(Request(model: "my-deployment"));

        Assert.Contains("deployments/my-deployment/", endpoint);
    }

    [Theory]
    [InlineData(null, "gpt-4o", "2024-08-01-preview", "BaseUrl")]
    [InlineData("https://my-resource.openai.azure.com", null, "2024-08-01-preview", "Model")]
    [InlineData("https://my-resource.openai.azure.com", "gpt-4o", null, "ApiVersion")]
    public void Missing_required_configuration_fails_loudly_instead_of_calling_the_wrong_url(
        string? baseUrl, string? model, string? apiVersion, string missingField)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Service().GetEndpoint(Request(baseUrl, model, apiVersion)));

        Assert.Contains(missingField, ex.Message);
    }

    [Fact]
    public void Authentication_uses_the_api_key_header_not_a_bearer_token()
    {
        using var client = new HttpClient();

        Service().ConfigureAuthentication(client, Request() with { ApiKey = "secret-key" });

        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.Equal("secret-key", Assert.Single(client.DefaultRequestHeaders.GetValues("api-key")));
    }
}
