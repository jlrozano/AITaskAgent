# Installation

## Requirements

- **.NET 10.0** or later
- **C# 14** language version
- Visual Studio 2025+ / VS Code with C# extension / JetBrains Rider

## Reference the framework

The framework is **not published to NuGet** — no project in this repository sets
`PackageId` or `GeneratePackageOnBuild`, and `IsPackable` only appears as `false` (test projects and samples; the
`CodebaseAnalysis.Designer` sample plugin is packed as a plugin `.nupkg` by its own `pack-plugin.ps1`, not as a framework
package). Reference it as a project:

```bash
dotnet add reference ../AITaskAgent/AITaskAgent.Extensions.Llm/AITaskAgent.Extensions.Llm.csproj
```

Or in your `.csproj`:

```xml
<ProjectReference Include="..\AITaskAgent\AITaskAgent.Extensions.Llm\AITaskAgent.Extensions.Llm.csproj" />
```

The framework is split into projects (see [Architecture v2](../ARCHITECTURE-v2.md)). `AITaskAgent.Extensions.Llm` brings
`AITaskAgent.Core` and `AITaskAgent.Abstractions` with it and is what any application with LLM steps references; a pipeline
without LLM steps can reference `AITaskAgent.Core` alone and use `AddAITaskAgentCore()` / `UseAITaskAgentCore()`. Add
`AITaskAgent.Yaml` for declarative pipelines, `AITaskAgent.Extensions.Script` for JavaScript steps (it brings
`AITaskAgent.Extensions.Script.Worker`, the process that runs them) and `AITaskAgent.Extensions.CSharp` for `CSharpStep`
(references Core). `AITaskAgent.NugetLoader` (NuGet resolver and plugin loader) references `AITaskAgent.Abstractions` only. The tools
(`FileTools`, `OfficeTools`, `McpIntegration`) and the providers (`OpenAILLmService`, `GeminiLlmService`, `OllamaLlmService`) are separate projects
that reference the LLM extension.

For the full walkthrough from an empty folder, see
[Starting a New Project](new-project.md).

## Basic Setup

Setup has **two** steps: register the services, then initialize the framework on the built
`IServiceProvider`.

```csharp
using AITaskAgent.Configuration;
using AITaskAgent.LLM.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;    // package Microsoft.Extensions.Hosting
using OpenAILLmService;                // project OpenAILLmService/OpenAILLmService.csproj

var builder = Host.CreateApplicationBuilder(args);

// 1. Register your LLM service implementations BEFORE building the provider.
//    They are matched against the "ServiceType" of each configured provider.
builder.Services.AddSingleton<ILlmService, OpenAILlmService>();

// 2. Register the framework services
builder.Services.AddAITaskAgent();

var host = builder.Build();

// 3. Initialize the static factories (PipelineContextFactory, AvailableTools)
host.Services.UseAITaskAgent();

await host.RunAsync();
```

> [!IMPORTANT]
> `UseAITaskAgent()` is **not optional**. It forces creation of the `IPipeline` and
> `PipelineContextFactory` singletons and registers every DI-registered `ITool` into
> `AvailableTools.Instance`. Without it, the first call to `PipelineContextFactory.Create()`
> throws `InvalidOperationException: PipelineContextFactory is not initialized.`

`AddAITaskAgent()` (core plus LLM extension) registers, among others:

| Service | Lifetime | Notes |
|---------|----------|-------|
| `IPipeline` → `Pipeline` | Singleton | Built from `PipelineOptions` |
| `PipelineContextFactory` | Singleton | Replaced by `LlmPipelineContextFactory` (so the default context is an `LlmPipelineContext`); also sets the static instance used by `PipelineContextFactory.Create()` |
| `IStepRegistry` | Singleton (instance) | Core steps plus those the extensions register (`LlmStep`, ...) |
| `IEventChannel` → `EventChannel` | Singleton | Log level from `Observability.EventLogLevel` |
| `IPipelineContextStorage` → `InMemoryPipelineContextStorage` | Singleton | |
| `ITemplateRendered` → `TinyTemplateRendered` | Singleton | |
| `ITools` → `AvailableTools.Instance` | Singleton | |
| `ICacheService` → `MemoryCacheService` | Singleton | |
| `IConversationStorage` → `InMemoryConversationStorage` | Singleton | |
| `ConversationContext` | Transient | Pre-configured with `Conversation.MaxTokens` |

## Configuration (appsettings.json)

Everything lives under the single `AITaskAgent` root section — **including** `LlmProviders`.

```json
{
  "AITaskAgent": {
    "Timeouts": {
      "DefaultPipelineTimeout": "00:10:00",
      "DefaultStepTimeout": "00:01:00"
    },
    "Conversation": {
      "MaxTokens": 8192
    },
    "Observability": {
      "EventLogLevel": "Debug",
      "EventChannelCapacity": 100
    },
    "LlmProviders": {
      "Providers": {
        "OpenRouter": {
          "ServiceType": "openai",
          "BaseUrl": "https://openrouter.ai/api/v1/",
          "ApiKey": "${OPENROUTER_API_KEY}"
        }
      },
      "Roles": {
        "default": {
          "Provider": "OpenRouter",
          "Model": "openai/gpt-4o-mini",
          "Inference": { "Temperature": 0.7 }
        }
      },
      "DefaultRole": "default"
    }
  }
}
```

See [Configuration](../guides/configuration.md) for the full schema and the startup validation rules.

## Verify Installation

```csharp
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Steps;
using Microsoft.Extensions.DependencyInjection;

var pipeline = host.Services.GetRequiredService<IPipeline>();

var result = await pipeline.ExecuteAsync(
    name: "TestPipeline",
    steps: [new EmptyStep()],
    input: "ping");

Console.WriteLine($"Pipeline executed successfully: {!result.HasError}");
```

## Troubleshooting

### Common Issues

| Problem | Solution |
|---------|----------|
| `PipelineContextFactory is not initialized` | Call `provider.UseAITaskAgent()` after building the provider |
| `LLM provider 'X' requests service type 'Y', but no matching ILlmService is registered` | Register the `ILlmService` whose `Name` matches `ServiceType`, or fix `ServiceType` |
| `LLM role 'X' references provider 'Y', which is not configured` | Fix the `Provider` key in the role; it must match a key under `Providers` |
| `Default LLM role 'default' is not configured` | Add the role named by `DefaultRole`, or change `DefaultRole` |
| `Pipeline timeout` | Increase `Timeouts.DefaultPipelineTimeout` |

### Enable Debug Logging

```csharp
builder.Logging.AddFilter("AITaskAgent", LogLevel.Debug);
```
