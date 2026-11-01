using AITaskAgent.LLM.Models;
using AITaskAgent.Resilience;
using Microsoft.Extensions.Logging;

namespace OpenAILLmService;

/// <summary>
/// Azure OpenAI Responses API. Two shapes, chosen by <see cref="LlmRequest.ApiVersion"/>:
/// <list type="bullet">
/// <item>empty: the v1 API, <c>{endpoint}/openai/v1/responses</c>, no <c>api-version</c>; <c>Model</c> is the deployment
/// name and travels in the body.</item>
/// <item>set: the legacy API, <c>{endpoint}/openai/responses?api-version=...</c>.</item>
/// </list>
/// Both authenticate with the <c>api-key</c> header.
/// </summary>
public sealed class AzureOpenAIResponsesLlmService(ILogger<AzureOpenAIResponsesLlmService> logger, RetryPolicy? retryPolicy = null)
    : OpenAIResponsesLlmService(logger, retryPolicy)
{
    public override string Name => "azure-openai-responses";

    protected internal override string GetEndpoint(LlmRequest request)
    {
        var baseUrl = request.BaseUrl?.TrimEnd('/')
            ?? throw new InvalidOperationException("Azure OpenAI requires BaseUrl (the resource endpoint).");

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new InvalidOperationException("Azure OpenAI requires Model to be the deployment name.");

        return string.IsNullOrWhiteSpace(request.ApiVersion)
            ? $"{baseUrl}/openai/v1/responses"
            : $"{baseUrl}/openai/responses?api-version={Uri.EscapeDataString(request.ApiVersion)}";
    }

    protected internal override void ConfigureAuthentication(HttpClient client, LlmRequest request)
    {
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Remove("api-key");
        client.DefaultRequestHeaders.TryAddWithoutValidation("api-key", request.ApiKey ?? "");
    }
}
