# Custom LLM Providers

## Overview

A provider is an implementation of `ILlmService`. It is a **protocol adapter**: it turns an
`LlmRequest` into an HTTP call and the answer back into an `LlmResult`. It holds no configuration
of its own — connection details arrive on every request — and it does not price the call.

Two implementations ship with the repository and are the best reference material:

- [`OpenAILlmService`](../../OpenAILLmService/OpenAILlmService.cs) — raw HTTP against any
  OpenAI-compatible chat-completions endpoint (OpenAI, OpenRouter, Groq, NVIDIA, Ollama, LM Studio).
- [`GeminiLlmService`](../../GeminiLlmService/GeminiLlmService.cs) — the Google GenAI SDK, with
  rate limiting, retry policy, file and cache managers.

## ILlmService Interface

```csharp
public interface ILlmService
{
    string Name { get; }

    Task<LlmResult> InvokeAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default);

    int EstimateTokenCount(string text);

    int GetMaxContextTokens(string? model = null);
}
```

### `Name` is load-bearing

`Name` is how configuration finds the service. When binding `AITaskAgent:LlmProviders`, each
provider's `ServiceType` (defaulting to the provider key when absent) is matched — case-insensitively,
exact match first, then substring — against the `Name` of every registered `ILlmService`. No match
means the binder throws at startup rather than silently sending the prompt to another vendor.

```csharp
public class OpenAILlmService : ILlmService, IDisposable
{
    public virtual string Name => "openai";
    // ...
}
```

```json
"Providers": {
  "OpenRouter": { "ServiceType": "openai", "BaseUrl": "https://openrouter.ai/api/v1/", "ApiKey": "${OPENROUTER_API_KEY}" },
  "Gemini":     { "ServiceType": "gemini", "BaseUrl": "https://generativelanguage.googleapis.com/", "ApiKey": "${GEMINI_API_KEY}", "ApiVersion": "v1beta" }
}
```

### Cost is not your job

`LlmResult.CostUsd` has an assembly-internal setter. A service reports token counts; it never sees
the model catalogue, so it cannot price them. `BaseLlmStep` fills the value in after the call:

```csharp
// private helper in BaseLlmStep: priced by the model the service reports, then by the requested one
result.CostUsd = PriceOf(provider, result.Model, model, result.PromptTokens, result.CompletionTokens, result.CachedPromptTokens);
```

`LlmProvider.CalculateCost` reads `InputPricePerMillion` / `OutputPricePerMillion` (and
`CachedInputPricePerMillion` for cached prompt tokens) from the provider's `Models` list. Report
`PromptTokens`, `CompletionTokens` and, if the vendor gives it, `CachedPromptTokens` accurately and
pricing takes care of itself.

### Errors are results, not exceptions

`LlmResult` implements the Result pattern. Return `LlmResult.Failure(exception, isRetriable)`
instead of throwing, so the step's retry and fallback machinery can decide what to do:

```csharp
if (!httpResponse.IsSuccessStatusCode)
{
    var statusCode = (int)httpResponse.StatusCode;
    var ex = new HttpRequestException($"HTTP {statusCode}: {rawJson}");
    return LlmResult.Failure(ex, IsRetriable(statusCode));   // 429, 500, 502, 503, 504
}
```

## Reading the request

Everything the call needs is on `LlmRequest`, which itself derives from `LlmInferenceSettings`:

```csharp
request.Model            // string, required
request.BaseUrl          // from LlmProvider
request.ApiKey
request.ApiVersion
request.SystemPrompt
request.Conversation     // ConversationContext
request.Tools            // IList<ToolDefinition>?
request.ResponseFormat
request.Temperature, request.MaxTokens, request.TopP, request.Stop, request.UseStreaming
request.Options          // JObject: everything vendor-specific
```

Vendor-specific parameters are read from `Options` with `GetOption<T>` (case-insensitive):

```csharp
var topK           = request.GetOption<int?>("TopK");
var enableThinking = request.GetOption<bool?>("EnableThinking");
var budget         = request.GetOption<int?>("ThinkingBudget");
```

`OpenAILlmService` takes the simpler route for an OpenAI-compatible body: it copies every property
of `Options` straight into the JSON payload.

```csharp
if (request.Options != null)
    foreach (var prop in request.Options.Properties())
        body[prop.Name] = prop.Value.DeepClone();
```

## Basic implementation

```csharp
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

public sealed class CustomLlmService(ILogger<CustomLlmService> logger) : ILlmService
{
    public string Name => "custom";

    public async Task<LlmResult> InvokeAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        var client = GetOrCreateClient(request.BaseUrl, request.ApiKey);

        var messages = request.Conversation.GetMessagesForRequest(
            maxTokens: request.SlidingWindowMaxTokens,
            useSlidingWindow: request.UseSlidingWindow);

        var payload = new
        {
            model       = request.Model,
            system      = request.SystemPrompt,
            messages    = messages.Select(m => new { role = m.Role, content = m.Content }),
            temperature = request.Temperature,
            max_tokens  = request.MaxTokens
        };

        HttpResponseMessage httpResponse;
        string rawJson;
        try
        {
            httpResponse = await client.PostAsJsonAsync("/v1/chat", payload, cancellationToken);
            rawJson = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not TaskCanceledException)
        {
            logger.LogError(ex, "HTTP request failed");
            return LlmResult.Failure(ex, isRetriable: true);
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            var status = (int)httpResponse.StatusCode;
            return LlmResult.Failure(
                new HttpRequestException($"HTTP {status}: {rawJson}"),
                isRetriable: status is 429 or 500 or 502 or 503 or 504);
        }

        var api = JsonConvert.DeserializeObject<ApiResponse>(rawJson)!;

        // One LlmResponse per candidate; usage belongs to the call as a whole.
        var candidates = api.Choices.Select(c =>
        {
            var (finishReason, rawFinishReason) = FinishReasonExtensions.Parse(c.FinishReason);
            return new LlmResponse
            {
                Index           = c.Index,
                Content         = c.Message.Content ?? string.Empty,
                Reasoning       = c.Message.Reasoning,
                FinishReason    = finishReason,
                RawFinishReason = rawFinishReason,
                ToolCalls       = c.Message.ToolCalls?.Select(tc => new ToolCall
                {
                    Id        = tc.Id,
                    Name      = tc.Function.Name,
                    Arguments = tc.Function.Arguments
                }).ToList()
            };
        }).ToList();

        return LlmResult.Success(
            candidates,
            tokensUsed:       api.Usage?.TotalTokens,
            promptTokens:     api.Usage?.PromptTokens,
            completionTokens: api.Usage?.CompletionTokens,
            model:            api.Model ?? request.Model,
            nativeResponse:   api);
    }

    public async IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var delta in ReadServerSentEventsAsync(request, cancellationToken))
        {
            yield return new LlmStreamChunk
            {
                Delta       = delta.Text,
                IsThinking  = delta.IsReasoning,
                IsComplete  = false,
                ChoiceIndex = delta.Index
            };
        }

        // Final chunk carries the usage; without it TokensUsed stays at zero.
        yield return new LlmStreamChunk
        {
            Delta            = string.Empty,
            IsComplete       = true,
            FinishReason     = FinishReason.Stop,
            TokensUsed       = totalTokens,
            PromptTokens     = promptTokens,
            CompletionTokens = completionTokens
        };
    }

    public int EstimateTokenCount(string text) => (int)Math.Ceiling(text.Length / 4.0);

    public int GetMaxContextTokens(string? model = null) => 128_000;
}
```

Both shipped services estimate tokens as `ceil(length / 4)`. `GeminiLlmService` additionally exposes
`CountTokensExactAsync` for when an exact number matters.

## Streaming

`InvokeStreamingAsync` yields `LlmStreamChunk`. Points that bite in practice:

- `Delta` is the increment, never the accumulated text.
- Set `IsThinking` for reasoning traces so the consumer can render them separately.
- Token counts only arrive on the final chunk. On OpenAI-compatible endpoints you have to ask for
  them: `body["stream_options"] = new JObject { ["include_usage"] = true }`. Without it,
  `total_tokens` never arrives.
- Use `ToolCallUpdates` (`IReadOnlyList<ToolCallUpdate>`) for incremental tool-call fragments and
  `ChoiceIndex` to keep multiple candidates apart.

## Tool calls

Tools reach the service as `IList<ToolDefinition>`, with the schema already serialized as a JSON
string:

```csharp
public sealed record ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string ParametersJsonSchema { get; init; }
    public NativeObject? Native { get; init; }
}
```

Translate them into the vendor's shape, and translate the model's answer back into `ToolCall`
(`Id`, `Name`, `Arguments` as a JSON string). Models served on open-weight endpoints often emit tool
calls as text rather than structured fields; `OpenAILlmService` handles that with a normalization
plugin chain rather than in the transport code — see
[Open-weight models](../guides/open-weight-models.md).

## Registration

Register every implementation as `ILlmService`. Multiple registrations are expected: the
configuration binder picks per provider by `Name`.

```csharp
services.AddSingleton<ILlmService, GeminiLlmService.GeminiLlmService>();
services.AddSingleton<ILlmService, OpenAILLmService.OpenAILlmService>();
services.AddSingleton<ILlmService, CustomLlmService>();

services.AddAITaskAgent();

var provider = services.BuildServiceProvider();
provider.UseAITaskAgent();
```

There is no keyed registration and no `ILlmServiceFactory`: selection happens through
`LlmProvider.ServiceType` in configuration.

## Provider configuration

```json
{
  "AITaskAgent": {
    "LlmProviders": {
      "DefaultRole": "default",
      "Providers": {
        "MyGateway": {
          "ServiceType": "custom",
          "BaseUrl": "https://my-api.com/v1/",
          "ApiKey": "${MY_API_KEY}",
          "Models": [
            {
              "Name": "my-model-large",
              "InputPricePerMillion": 3.0,
              "OutputPricePerMillion": 15.0,
              "Inference": { "MaxTokens": 8192 }
            }
          ]
        }
      },
      "Roles": {
        "default": {
          "Provider": "MyGateway",
          "Model": "my-model-large",
          "SystemPrompt": "You are a helpful assistant.",
          "Inference": {
            "Temperature": 0.7,
            "UseStreaming": true,
            "Options": { "my_vendor_flag": true }
          }
        }
      }
    }
  }
}
```

Binding fails at startup — deliberately, and loudly — when `ServiceType` matches no registered
service, when `BaseUrl` is missing, when a role names an unknown provider, when a role omits
`Model`, or when `DefaultRole` is not among the configured roles.
