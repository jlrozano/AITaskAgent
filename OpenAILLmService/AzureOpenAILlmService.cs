using AITaskAgent.LLM.Models;
using AITaskAgent.Resilience;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;

namespace OpenAILLmService;

/// <summary>
/// Azure OpenAI Service implementation. The chat-completions wire format is the same as OpenAI's -
/// <see cref="OpenAILlmService"/> supplies all of it - but Azure addresses a model by deployment
/// name in the URL path rather than in the request body, requires an <c>api-version</c> query
/// string, and authenticates with an <c>api-key</c> header instead of a bearer token. Those three
/// are Azure's own concept (never meaningful for OpenAI, OpenRouter, or any other vendor served by
/// the base class), so they live here rather than as a branch inside it.
/// </summary>
/// <remarks>
/// Configure <c>Model</c> as the Azure deployment name (not the underlying model name) and
/// <c>Provider.ApiVersion</c> as the API version, e.g. <c>2024-08-01-preview</c>.
/// </remarks>
public sealed class AzureOpenAILlmService(ILogger<AzureOpenAILlmService> logger, RetryPolicy? retryPolicy = null)
    : OpenAILlmService(logger, retryPolicy)
{
    public override string Name => "azure-openai";

    protected internal override string GetEndpoint(LlmRequest request)
    {
        var baseUrl = request.BaseUrl?.TrimEnd('/')
            ?? throw new InvalidOperationException("Azure OpenAI requires BaseUrl (the resource endpoint).");

        if (string.IsNullOrWhiteSpace(request.Model))
        {
            throw new InvalidOperationException("Azure OpenAI requires Model to be the deployment name.");
        }

        if (string.IsNullOrWhiteSpace(request.ApiVersion))
        {
            throw new InvalidOperationException(
                "Azure OpenAI requires ApiVersion (e.g. \"2024-08-01-preview\") - set it on the provider's " +
                "configuration entry.");
        }

        return $"{baseUrl}/openai/deployments/{Uri.EscapeDataString(request.Model)}/chat/completions" +
               $"?api-version={Uri.EscapeDataString(request.ApiVersion)}";
    }

    protected internal override void ConfigureAuthentication(HttpClient client, LlmRequest request)
    {
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Remove("api-key");
        client.DefaultRequestHeaders.TryAddWithoutValidation("api-key", request.ApiKey ?? "");
    }
}
