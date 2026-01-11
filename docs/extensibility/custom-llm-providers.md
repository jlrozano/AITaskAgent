# Custom LLM Providers

## Overview

Create custom LLM providers by implementing `ILlmService`.

## ILlmService Interface

```csharp
public interface ILlmService
{
    Task<LlmResponse> InvokeAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default);
    
    IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default);
    
    int EstimateTokenCount(string text);
    
    int GetMaxContextTokens(string? model = null);
}
```

## Basic Implementation

```csharp
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Models;

public class CustomLlmService(HttpClient httpClient, ILogger<CustomLlmService> logger) 
    : ILlmService
{
    public async Task<LlmResponse> InvokeAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        var messages = request.Conversation.GetMessagesForRequest(
            maxTokens: request.SlidingWindowMaxTokens,
            useSlidingWindow: request.UseSlidingWindow);
        
        var payload = new
        {
            model = request.Profile.Model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            temperature = request.Temperature,
            max_tokens = request.MaxTokens
        };
        
        var response = await httpClient.PostAsJsonAsync(
            "/v1/chat/completions", 
            payload, 
            cancellationToken);
        
        var result = await response.Content.ReadFromJsonAsync<ApiResponse>(cancellationToken);
        
        return new LlmResponse
        {
            Content = result.Choices[0].Message.Content,
            TokensUsed = result.Usage.TotalTokens,
            PromptTokens = result.Usage.PromptTokens,
            CompletionTokens = result.Usage.CompletionTokens,
            FinishReason = result.Choices[0].FinishReason,
            Model = result.Model
        };
    }
    
    public async IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Implement streaming...
        yield return new LlmStreamChunk
        {
            Delta = "chunk content",
            IsComplete = false
        };
    }
    
    public int EstimateTokenCount(string text) => text.Length / 4;
    
    public int GetMaxContextTokens(string? model = null) => 128000;
}
```

## Extending BaseLlmService

For providers with common patterns:

```csharp
public class AzureOpenAIService : BaseLlmService
{
    public AzureOpenAIService(HttpClient httpClient, ILogger<AzureOpenAIService> logger)
        : base(httpClient, logger)
    {
    }
    
    protected override string GetEndpoint(LlmRequest request)
        => $"openai/deployments/{request.Profile.Model}/chat/completions?api-version=2024-02-15-preview";
    
    protected override void ConfigureRequest(HttpRequestMessage request, LlmRequest llmRequest)
    {
        request.Headers.Add("api-key", _apiKey);
    }
}
```

## Registration

```csharp
// Single provider
services.AddSingleton<ILlmService, CustomLlmService>();

// Named providers
services.AddKeyedSingleton<ILlmService, OpenAILlmService>("openai");
services.AddKeyedSingleton<ILlmService, AnthropicLlmService>("anthropic");

// Factory pattern
services.AddSingleton<ILlmServiceFactory, LlmServiceFactory>();
```

## Provider Configuration

```json
{
  "LlmProviders": {
    "Profiles": {
      "custom": {
        "Provider": "Custom",
        "Model": "my-model",
        "Temperature": 0.7,
        "Endpoint": "https://my-api.com/v1"
      }
    }
  }
}
```

## Handling Tool Calls

```csharp
public async Task<LlmResponse> InvokeAsync(LlmRequest request, CancellationToken ct)
{
    // Include tools in request
    var payload = new
    {
        model = request.Profile.Model,
        messages = BuildMessages(request),
        tools = request.Tools?.Select(t => new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = t.Parameters
            }
        })
    };
    
    // ... call API ...
    
    // Parse tool calls from response
    return new LlmResponse
    {
        Content = result.Content,
        ToolCalls = result.ToolCalls?.Select(tc => new ToolCall
        {
            Id = tc.Id,
            Name = tc.Function.Name,
            Arguments = tc.Function.Arguments
        }).ToList()
    };
}
```
