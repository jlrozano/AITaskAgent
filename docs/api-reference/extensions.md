# Extensions

`AITaskAgent.Configuration.*`

## Service registration

From v2 registration is split by assembly. The two-step shape is unchanged: `Add...` on the collection, then
`UseAITaskAgent()` on the **built** provider.

```csharp
// AITaskAgent.Core
public static IServiceCollection AddAITaskAgentCore(this IServiceCollection services);
public static IServiceProvider   UseAITaskAgentCore(this IServiceProvider serviceProvider);

// AITaskAgent.Extensions.Llm
public static IServiceCollection AddAITaskAgent(this IServiceCollection services);      // Core + LLM
public static IServiceCollection AddAITaskAgentLlm(this IServiceCollection services);   // only the LLM extension
public static IServiceProvider   UseAITaskAgent(this IServiceProvider serviceProvider); // UseCore + register ITools

// AITaskAgent.Yaml
public static IServiceCollection AddAITaskAgentYaml(this IServiceCollection services);

// AITaskAgent.Extensions.Script
public static IServiceCollection AddScriptExtension(this IServiceCollection services);

// AITaskAgent.Extensions.CSharp
public static IServiceCollection AddCSharpExtension(this IServiceCollection services);                          // registers the CSharpStep tag
public static IServiceCollection AddCSharpUnit(this IServiceCollection services, CSharpUnit unit, string packageName); // registers the compiled unit

// AITaskAgent.Core: extensions and the step registry
public static IServiceCollection AddExtension(this IServiceCollection services, IExtension extension);
public static IServiceCollection AddExtension<TExtension>(this IServiceCollection services) where TExtension : IExtension, new();
public static IStepRegistry      GetOrAddStepRegistry(this IServiceCollection services);
```

`AddAITaskAgent()` is `AddAITaskAgentCore()` plus `AddAITaskAgentLlm()`; an application without LLM steps (for instance one
that only runs YAML pipelines with `ScriptStep`) uses `AddAITaskAgentCore()` and `UseAITaskAgentCore()`. Both
`AddAITaskAgent*` forms take no `IConfiguration` argument: they bind `AITaskAgentOptions` from the `IConfiguration`
already present in DI, under the `AITaskAgent` root section.

`UseAITaskAgent()` forces creation of the `IPipeline` and `PipelineContextFactory` singletons
(the factory publishes itself to a static field used by `PipelineContextFactory.Create`) and
registers every `ITool` resolvable from DI into `AvailableTools.Instance`. Skipping it leaves
`PipelineContextFactory.Create` throwing `InvalidOperationException`.

`AddExtension` runs `IExtension.Register` against the collection and its step registry and is idempotent by
`IExtension.Name`: a second extension with the same name is ignored, so calling `AddFileTools()` twice no longer
registers its tools twice. `GetOrAddStepRegistry` creates the `IStepRegistry` singleton as an *instance* (with the
core steps) the first time, so registration code can fill it before the provider exists; it throws if someone
registered `IStepRegistry` by type or factory.

The extensions shipped in this repository are `LlmExtension` (`AddAITaskAgentLlm`), `ScriptExtension`
(`AddScriptExtension`), `CSharpExtension` (`AddCSharpExtension`), `FileToolsExtension` (`AddFileTools`), `OfficeToolsExtension` (`AddOfficeTools`) and
`McpExtension` (`AddMcpToolProvider`). Writing your own is covered in
[Custom extensions](../extensibility/custom-extensions.md).

### IExtension and IExtensionBuilder

`AITaskAgent.Core.Abstractions` (assembly `AITaskAgent.Abstractions`). Members with a body are default interface members.

```csharp
public interface IExtension
{
    string Name { get; }                                   // unique; a second extension with the same name is ignored
    void Register(IExtensionBuilder builder);
    Type? ConfigurationType => null;                       // class whose public properties are its configuration
    JsonSchema? ConfigurationModel();                      // default: TypeSchemaBuilder.Build(ConfigurationType, configuration: true)
    ExtensionInfo Info() => ExtensionInfo.Empty;           // what it declares it will register (information only)
    IReadOnlyDictionary<string, string?> ConfigurationDefaults(PluginContext context);   // default: none
    IReadOnlyList<string> RootSections => [];              // top-level sections a plugin host lifts to the configuration root
}

public interface IExtensionBuilder
{
    IServiceCollection Services { get; }
    IStepRegistry Steps { get; }
    PluginContext? Plugin => null;                         // identity and folder when a host loads it as a plugin; null otherwise

    IExtensionBuilder AddStep(Type stepType, string? tag = null);
    IExtensionBuilder AddStep<TStep>(string? tag = null) where TStep : IStep;
    IExtensionBuilder AddStepsFromAssembly(Assembly assembly);

    IExtensionBuilder AddHook(string name, HookKind kind, MethodInfo method, string? description = null);
    IExtensionBuilder AddHooksFromAssembly(Assembly assembly);          // [Hook], [Validator], [ResultValidator] methods
    IExtensionBuilder AddResponseType(string name, Type type);
    IExtensionBuilder AddResponseTypesFromAssembly(Assembly assembly);  // [ResponseType] classes

    IExtensionBuilder AddToolType(Type toolType);                       // ITool class with a parameterless constructor
    IExtensionBuilder AddToolInstance(object tool);
    IExtensionBuilder AddToolSourceInstance(object toolSource);         // IToolSource
    IExtensionBuilder AddTemplateSourceInstance(object templateSource); // ITemplateSource
    IExtensionBuilder AddScriptObject(string name, Type type, Func<IServiceProvider, object>? factory = null, string? description = null);
}
```

The builder that `services.AddExtension(...)` hands to `Register` implements every method above. The default bodies that throw
`NotSupportedException` (`AddHook`, `AddResponseType`, `AddToolType`, `AddToolInstance`, `AddToolSourceInstance`,
`AddTemplateSourceInstance`) only apply to a builder of your own that does not override them. `AddHooksFromAssembly` and
`AddResponseTypesFromAssembly` have default bodies that discover the attributed members (`ExtensionDiscovery`) and call
`AddHook` / `AddResponseType`; `AddScriptObject` by default adds a `RegisteredScriptObject` to `Services`. `ITool` and the typed tool
forms live in `AITaskAgent.Extensions.Llm`.

`ExtensionInfo` is the declaration returned by `Info()`: `Entries` (a list of `ExtensionEntry(Kind, Name, Type?, Description?)`), `Of(kind)`,
`With(...)`, `Merge(...)` and the static `Difference(declared, actual)`; `ExtensionInfo.Empty` is the default. The kinds are the constants of
`ExtensionKinds` (`step`, `tool`, `toolSource`, `hook`, `validator`, `resultValidator`, `responseType`, `templateSource`, `configuration`,
`rootSection`, `scriptObject`, `scriptLibrary`). `ExtensionCapture` is an `IExtensionBuilder` that registers nothing and only keeps what
`Register` asked for; `ExtensionCapture.ActualInfo(extension?)` returns that as an `ExtensionInfo`, so a host that loads the extension as a
plugin can compare it with `Info()` through `ExtensionInfo.Difference`.

**Source:** [IExtension.cs](../../AITaskAgent.Abstractions/Abstractions/IExtension.cs), [ExtensionInfo.cs](../../AITaskAgent.Abstractions/Abstractions/ExtensionInfo.cs),
[ExtensionCapture.cs](../../AITaskAgent.Abstractions/Abstractions/ExtensionCapture.cs)

### Registered Services

Core (`AddAITaskAgentCore`) registers the options, `IPipeline`, `ITemplateRendered`, `IEventChannel`, `IPipelineContextStorage`, `PipelineContextFactory`, `ICacheService` and the `IStepRegistry`; the LLM extension adds the `LlmEnvironment` binding, `IConversationStorage`, `ITools`, the transient `ConversationContext` and replaces the factory with `LlmPipelineContextFactory` (only if the registered one is the core's).

| Service | Implementation | Lifetime |
|---------|----------------|----------|
| `IOptions<AITaskAgentOptions>` | Bound from `AITaskAgent` section | Singleton |
| `IOptions<PipelineOptions>` | Derived from `AITaskAgentOptions.Timeouts` | Singleton |
| `IConfigureOptions<LlmEnvironment>` | Binds `AITaskAgent:LlmProviders` | Singleton |
| `IPipeline` | `Pipeline` | Singleton (`TryAdd`) |
| `ITemplateRendered` | `TinyTemplateRendered` | Singleton (`TryAdd`) |
| `IEventChannel` | `EventChannel` | Singleton (`TryAdd`) |
| `IPipelineContextStorage` | Chosen by `AITaskAgent:Storage:Context`: `memory` (default, `InMemoryPipelineContextStorage`) or `file` (`FilePipelineContextStorage`, needs `Directory`) | Singleton (`TryAdd`) |
| `PipelineContextFactory` | - | Singleton (`TryAdd`) |
| `ICacheService` | `MemoryCacheService` | Singleton (`TryAdd`) |
| `IConversationStorage` | Chosen by `AITaskAgent:Storage:Conversation`: `memory` (default, `InMemoryConversationStorage`), `file` (JSON) or `yaml` (needs `Directory`) | Singleton (`TryAdd`) |
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
// AITaskAgent.Core
public class PipelineContextFactory
{
    public static PipelineContext Create(
        IEnumerable<Func<IProgressEvent, CancellationToken, Task>>? subscribers = null);

    // Same as Create, through this factory instance
    public PipelineContext NewContext(
        IEnumerable<Func<IProgressEvent, CancellationToken, Task>>? subscribers = null);

    protected virtual PipelineContext CreateContext(IEventChannel? eventChannel);

    public static Task<PipelineContext> CreateFromId(string id);
}

// AITaskAgent.Extensions.Llm (namespace AITaskAgent.LLM.Conversation.Context)
public class LlmPipelineContextFactory : PipelineContextFactory
{
    public static LlmPipelineContext Create(
        ConversationContext? conversation = null,
        IEnumerable<Func<IProgressEvent, CancellationToken, Task>>? subscribers = null);
}
```

`PipelineContextFactory.Create(conversation)` no longer exists: a context with an already loaded conversation is
created with `LlmPipelineContextFactory.Create(conversation)`. `Create` is static and reads the singleton captured during `UseAITaskAgent()`. When `subscribers`
are supplied, the context gets its own `EventChannel` instead of the shared singleton, so events
from that run stay isolated.

```csharp
var context = LlmPipelineContextFactory.Create(conversation);
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

Binding fails loudly at startup for a role without a `Provider` or pointing at an unknown provider,
a provider whose `ServiceType` matches no registered `ILlmService.Name`, a missing `BaseUrl` or
`Model`, a fallback pointing at an unknown provider or without a `Model`, or a `DefaultRole` that is
not among the configured roles. See [LLM Namespace](llm-namespace.md).

---

## Configuration Keys

```csharp
public static class AITaskAgentConfigurationKeys
{
    public const string RootSection    = "AITaskAgent";
    public const string LlmProviders   = "AITaskAgent:LlmProviders";
    public const string Observability  = "AITaskAgent:Observability";
    public const string Timeouts       = "AITaskAgent:Timeouts";
    [Obsolete] public const string Resilience = "AITaskAgent:Resilience";   // nothing is bound under it
    public const string Conversation   = "AITaskAgent:Conversation";
    public const string Storage        = "AITaskAgent:Storage";
    public const string ContextStorage = "AITaskAgent:Storage:Context";
    public const string ConversationStorage = "AITaskAgent:Storage:Conversation";
}
```

`Resilience` is `[Obsolete]`: no section is bound under `AITaskAgent:Resilience`; the real sections are `AITaskAgent:CircuitBreaker` and
`AITaskAgent:RateLimit`. `AITaskAgent:Storage` (`StorageOptions`) has two entries, `Context` and `Conversation`, each
`{ "Type": ..., "Directory": ... }`. `Context` accepts `memory` (default) or `file`; `Conversation` accepts `memory` (default), `file` (JSON) or `yaml`.
A file type without `Directory`, or a type not valid for the entry, is an `InvalidOperationException` naming the key. A storage the
application registers itself takes precedence over this section.

**Source:** [ServiceCollectionExtensions.cs](../../AITaskAgent.Core/Configuration/ServiceCollectionExtensions.cs),
[ExtensionServiceCollectionExtensions.cs](../../AITaskAgent.Core/Configuration/ExtensionServiceCollectionExtensions.cs),
[LlmServiceCollectionExtensions.cs](../../AITaskAgent.Extensions.Llm/Configuration/LlmServiceCollectionExtensions.cs)
