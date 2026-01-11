# Extensions

`AITaskAgent.Configuration.*`

## ServiceCollectionExtensions

```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAITaskAgent(
        this IServiceCollection services,
        IConfiguration? configuration = null);
}
```

### Registered Services

| Service | Implementation | Lifetime |
|---------|----------------|----------|
| `AITaskAgentOptions` | From config | Singleton |
| `PipelineContextFactory` | - | Singleton |
| `IEventChannel` | `EventChannel` | Singleton |
| `IToolRegistry` | `ToolRegistry` | Singleton |
| `ITemplateEngine` | `JsonTemplateEngine` | Singleton |
| `ICacheService` | `MemoryCacheService` | Singleton |
| `CircuitBreaker` | - | Singleton |
| `RateLimiter` | - | Singleton |
| `IConversationStorage` | `InMemoryConversationStorage` | Singleton |
| `ILlmProviderResolver` | `LlmProviderResolver` | Singleton |
| `ConversationContext` | - | Transient |

### Usage

```csharp
var builder = Host.CreateApplicationBuilder(args);

// Basic setup
builder.Services.AddAITaskAgent();

// With custom configuration section
builder.Services.AddAITaskAgent(
    builder.Configuration.GetSection("CustomAISection"));
```

---

## PipelineContextFactory

```csharp
public class PipelineContextFactory
{
    public static PipelineContext Create(IEventChannel? eventChannel = null);
    public PipelineContext Create();
}
```

### Usage

```csharp
var factory = services.GetRequiredService<PipelineContextFactory>();
var context = factory.Create();

// Or static
var context = PipelineContextFactory.Create();
```

---

## ILlmProviderResolver

```csharp
public interface ILlmProviderResolver
{
    LlmProviderConfig Resolve(string profileName);
}
```

### Usage

```csharp
var resolver = services.GetRequiredService<ILlmProviderResolver>();

var defaultProfile = resolver.Resolve("default");
var fastProfile = resolver.Resolve("fast");
```

---

## Configuration Keys

```csharp
public static class AITaskAgentConfigurationKeys
{
    public const string RootSection = "AITaskAgent";
    public const string LlmProviders = "LlmProviders";
}
```
