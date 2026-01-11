# Configuration

## Service Registration

```csharp
using AITaskAgent.Configuration;

services.AddAITaskAgent();
```

This registers:
- `AITaskAgentOptions` from configuration
- `PipelineContextFactory`
- `IEventChannel`
- `IToolRegistry`
- `ITemplateEngine`
- `ICacheService`
- `CircuitBreaker`
- `RateLimiter`
- `IConversationStorage`

## Configuration Options

### appsettings.json

```json
{
  "AITaskAgent": {
    "Timeouts": {
      "DefaultPipelineTimeout": "00:10:00",
      "DefaultStepTimeout": "00:01:00"
    },
    "Conversation": {
      "MaxTokens": 4000
    },
    "CircuitBreaker": {
      "FailureThreshold": 5,
      "OpenDurationSeconds": 30
    },
    "RateLimit": {
      "MaxTokens": 100,
      "RefillIntervalMs": 1000,
      "TokensPerRefill": 10
    },
    "Observability": {
      "EventLogLevel": "Information"
    }
  },
  "LlmProviders": {
    "Profiles": {
      "default": {
        "Provider": "OpenAI",
        "Model": "gpt-4o",
        "Temperature": 0.7,
        "MaxTokens": 4096,
        "JsonCapability": "JsonSchema"
      },
      "fast": {
        "Provider": "OpenAI",
        "Model": "gpt-4o-mini",
        "Temperature": 0.3,
        "MaxTokens": 1024
      },
      "reasoning": {
        "Provider": "OpenAI",
        "Model": "o1-preview",
        "MaxTokens": 8192
      }
    }
  }
}
```

## Options Classes

### AITaskAgentOptions

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Timeouts.DefaultPipelineTimeout` | `TimeSpan` | 10 min | Max pipeline duration |
| `Timeouts.DefaultStepTimeout` | `TimeSpan` | 1 min | Max step duration |
| `Conversation.MaxTokens` | `int` | 4000 | Context window limit |
| `CircuitBreaker.FailureThreshold` | `int` | 5 | Failures before open |
| `CircuitBreaker.OpenDurationSeconds` | `int` | 30 | Time before half-open |
| `RateLimit.MaxTokens` | `int` | 100 | Bucket capacity |
| `RateLimit.RefillIntervalMs` | `int` | 1000 | Refill interval |
| `RateLimit.TokensPerRefill` | `int` | 10 | Tokens per refill |

### LlmProviderConfig

| Property | Type | Description |
|----------|------|-------------|
| `Provider` | `string` | Provider name (OpenAI, Azure, etc.) |
| `Model` | `string` | Model identifier |
| `Temperature` | `float?` | Randomness (0.0-2.0) |
| `MaxTokens` | `int?` | Max output tokens |
| `TopP` | `float?` | Nucleus sampling |
| `TopK` | `int?` | Top-K sampling |
| `FrequencyPenalty` | `float?` | Repetition penalty |
| `PresencePenalty` | `float?` | Topic diversity |
| `JsonCapability` | `JsonResponseCapability` | JSON support level |

### JsonResponseCapability

```csharp
public enum JsonResponseCapability
{
    None,        // No native JSON support
    JsonObject,  // JSON mode without schema
    JsonSchema   // Full structured output
}
```

## Resolving LLM Profiles

```csharp
var resolver = services.GetRequiredService<ILlmProviderResolver>();

// Get profile by name
var defaultProfile = resolver.Resolve("default");
var fastProfile = resolver.Resolve("fast");

// Use in step
var step = new LlmStep<In, Out>(llmService, "Step", fastProfile, ...);
```

## Programmatic Configuration

```csharp
services.Configure<AITaskAgentOptions>(options =>
{
    options.Timeouts.DefaultStepTimeout = TimeSpan.FromSeconds(30);
    options.Conversation.MaxTokens = 8000;
});

services.Configure<LlmProvidersConfig>(options =>
{
    options.Profiles["custom"] = new LlmProviderConfig
    {
        Provider = "OpenAI",
        Model = "gpt-4-turbo",
        Temperature = 0.5f
    };
});
```
