using AITaskAgent.LLM.Models;
using AITaskAgent.Resilience;
using Microsoft.Extensions.Logging;

namespace OpenAILLmService;

/// <summary>
/// OpenRouter implementation. The chat-completions protocol is identical to OpenAI's, so today this
/// only adds the attribution headers OpenRouter's own dashboard reads (they used to be sent to every
/// vendor unconditionally by the base class, which leaked OpenRouter's identity into requests that
/// had nothing to do with it). This class is also the intended home for OpenRouter-specific concepts
/// that are not implemented yet - model routing/fallback preferences, per-generation cost lookups -
/// since those are OpenRouter's own product surface, never meaningful for another vendor.
/// </summary>
public sealed class OpenRouterLlmService(ILogger<OpenRouterLlmService> logger, RetryPolicy? retryPolicy = null)
    : OpenAILlmService(logger, retryPolicy)
{
    public override string Name => "openrouter";

    protected internal override void ConfigureExtraHeaders(HttpClient client, LlmRequest request)
    {
        client.DefaultRequestHeaders.Remove("HTTP-Referer");
        client.DefaultRequestHeaders.Remove("X-Title");
        client.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/AITaskFramework");
        client.DefaultRequestHeaders.Add("X-Title", "AITaskFramework");
    }
}
