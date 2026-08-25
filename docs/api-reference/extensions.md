# Extensions

`AITaskAgent.Configuration.*`

## ServiceCollectionExtensions

Registration is a two-step process: `AddAITaskAgent()` on the collection, then
`UseAITaskAgent()` on the **built** provider.

```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAITaskAgent(this IServiceCollection services);

    public static IServiceProvider UseAITaskAgent(this IServiceProvider serviceProvider);
}
```

`AddAITaskAgent()` takes no `IConfiguration` argument: it binds `AITaskAgentOptions` from the
`IConfiguration` already present in DI, under the `AITaskAgent` root section.

`UseAITaskAgent()` forces creation of the `IPipeline` and `PipelineContextFactory` singletons
(the factory publishes itself to a static field used by `PipelineContextFactory.Create`) and
registers every `ITool` resolvable from DI into `AvailableTools.Instance`. Skipping it leaves
`PipelineContextFactory.Create` throwing `InvalidOperationException`.

### Registered Services

| Service | Implementation | Lifetime |
|---------|----------------|----------|
| `IOptions<AITaskAgentOptions>` | Bound from `AITaskAgent` section | Singleton |
| `IOptions<PipelineOptions>` | Derived from `AITaskAgentOptions.Timeouts` | Singleton |
| `IConfigureOptions<LlmEnvironment>` | Binds `AITaskAgent:LlmProviders` | Singleton |
| `IPipeline` | `Pipeline` | Singleton (`TryAdd`) |
| `ITemplateRendered` | `TinyTemplateRendered` | Singleton (`TryAdd`) |
| `IEventChannel` | `EventChannel` | Singleton (`TryAdd`) |
| `IPipelineContextStorage` | `InMemoryPipelineContextStorage` | Singleton (`TryAdd`) |
| `PipelineContextFactory` | - | Singleton (`TryAdd`) |
| `ICacheService` | `MemoryCacheService` | Singleton (`TryAdd`) |
| `IConversationStorage` | `InMemoryConversationStorage` | Singleton |
| `ITools` | `AvailableTools.Instance` | Singleton |
| `ConversationContext` | - | Transient |

> `CircuitBreaker` and `RateLimiter` are **not** registered by `AddAITaskAgent()`. They are
> options-driven helpers (`CircuitBreakerOptions`, `RateLimitOptions`) constructed by whoever needs
> them — see [Resilience](../guides/resilience.md). `ITemplateProvider` is likewise not registered;
> `FileTemplateProvider` is constructed explicitly with the folder that holds the templates.

### Usage

```csharp
var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(configuration);
services.AddLogging();

// One or more LLM services; the config binder matches them by ILlmService.Name
services.AddSingleton<ILlmService, GeminiLlmService.GeminiLlmService>();
services.AddSingleton<ILlmService, OpenAILLmService.OpenAILlmService>();

services.AddAITaskAgent();

var provider = services.BuildServiceProvider();
provider.UseAITaskAgent();
```

The same shape works on a host builder:

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddAITaskAgent();
var app = builder.Build();
app.Services.UseAITaskAgent();
```

---

## PipelineContextFactory

```csharp
public class PipelineContextFactory
{
    public static PipelineContext Create(ConversationContext? conversation = null);

    public static PipelineContext Create(
        ConversationContext? conversation = null,
        IEnumerable<Func<IProgressEvent, CancellationToken, Task>>? subscribers = null);

    public static Task<PipelineContext> CreateFromId(string id);
}
```

`Create` is static and reads the singleton captured during `UseAITaskAgent()`. When `subscribers`
are supplied, the context gets its own `EventChannel` instead of the shared singleton, so events
from that run stay isolated.

```csharp
var context = PipelineContextFactory.Create(conversation);
context.Metadata["userId"] = "user-123";
```

`CreateFromId` rehydrates a suspended context from `IPipelineContextStorage`.

---

## Pipeline Middleware Registration

`AITaskAgent.Core.Execution.ServiceProviderPipelineMiddlewareExtensions`

```csharp
public static IServiceProvider RegisterPipelineMiddleware<TMiddleware>(this IServiceProvider serviceProvider)
    where TMiddleware : class, IPipelineMiddleware;

public static IServiceProvider RegisterPipelineMiddlewares(
    this IServiceProvider serviceProvider,
    params Type[] middlewareTypes);
```

The middleware is resolved from DI, or created with `ActivatorUtilities` when it is not registered,
and then stored in the static `PipelineMiddlewareRegistry`. Middlewares are global — they apply to
every pipeline execution in the process, in registration order.

```csharp
app.Services.UseAITaskAgent();
app.Services.RegisterPipelineMiddleware<ContextBroadcastMiddleware>();
```

---

## LLM Configuration Access

There is no `ILlmProviderResolver`. The bound configuration is a single `LlmEnvironment` object
read through `IOptions<T>`:

```csharp
var env = serviceProvider.GetRequiredService<IOptions<LlmEnvironment>>().Value;

LlmRole role = env.Roles["default"];        // model + inference + fallbacks + system prompt
LlmProvider provider = env.Providers["Gemini"];  // connection + model catalogue with prices
```

Binding fails loudly at startup for a role pointing at an unknown provider, a provider whose
`ServiceType` matches no registered `ILlmService.Name`, a missing `BaseUrl` or `Model`, or a
`DefaultRole` that is not among the configured roles. See [LLM Namespace](llm-namespace.md).

---

## Configuration Keys

```csharp
public static class AITaskAgentConfigurationKeys
{
    public const string RootSection    = "AITaskAgent";
    public const string LlmProviders   = "AITaskAgent:LlmProviders";
    public const string Observability  = "AITaskAgent:Observability";
    public const string Timeouts       = "AITaskAgent:Timeouts";
    public const string Resilience     = "AITaskAgent:Resilience";
    public const string Conversation   = "AITaskAgent:Conversation";
}
```

**Source:** [ServiceCollectionExtensions.cs](../../Framework/Configuration/ServiceCollectionExtensions.cs)
