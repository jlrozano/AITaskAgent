# Starting a New Project

How to create a new application on top of AITaskAgent, from an empty folder to a
running pipeline. Every command and snippet on this page was executed against
this repository (.NET SDK 10.0.303) before being written down.

> [!IMPORTANT]
> The framework is **not published to NuGet**. No project in this repository sets
> `PackageId`, `IsPackable` or `GeneratePackageOnBuild`, so `dotnet add package
> AITaskAgent` resolves nothing. Consume it with a `ProjectReference` to
> `Framework/AITaskAgent.csproj` — either from inside this repository (add your
> project to `AITaskAgent.slnx`) or from a sibling checkout.

## 1. Create the project

```bash
dotnet new console -o MyAgentApp
cd MyAgentApp

# The framework itself
dotnet add reference ../AITaskAgent/Framework/AITaskAgent.csproj

# One LLM provider per backend you intend to use (skip if you have no LLM steps)
dotnet add reference ../AITaskAgent/OpenAILLmService/OpenAILLmService.csproj
# dotnet add reference ../AITaskAgent/GeminiLlmService/GeminiLlmService.csproj

# Generic host + configuration. The framework only references the *Abstractions*
# packages, so the host implementation is yours to bring.
dotnet add package Microsoft.Extensions.Hosting
```

Then make `appsettings.json` reach the output folder — without this the file is
never copied and `IConfiguration` sees nothing at runtime:

```xml
<ItemGroup>
  <None Update="appsettings.json">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
  <None Update="appsettings.Development.json">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

The framework targets `net10.0` with `LangVersion` 14. Your project must target
`net10.0` or later.

## 2. Minimal pipeline, no LLM

The smallest thing that runs. `AddAITaskAgent()` works with no `LlmProviders`
section at all — LLM configuration is only validated when it is present.

```csharp
using AITaskAgent.Configuration;
using AITaskAgent.Core.Execution;      // IPipeline, PipelineContextFactory
using AITaskAgent.Core.StepResults;    // StepResult<T>
using AITaskAgent.Core.Steps;          // DelegatedStep<,>
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddAITaskAgent();

var host = builder.Build();
host.Services.UseAITaskAgent();        // required, see step 4

var pipeline = host.Services.GetRequiredService<IPipeline>();

var step = new DelegatedStep<StepResult<string>, StepResult<string>>(
    "Greet",
    (s, input, ctx, attempt, last) =>
        Task.FromResult(new StepResult<string>(s, $"Hello, {input.Value}!")));

var result = await pipeline.ExecuteAsync("GreetingPipeline", [step], "World");
Console.WriteLine(result.HasError ? $"ERROR: {result.Error?.Message}" : result.Value);
```

`dotnet run` prints `Hello, World!` between two `Pipeline` log lines.

## 3. Add an LLM provider

Three things have to line up, and the framework enforces all three at startup:

| Piece | Where | Rule |
|---|---|---|
| `ILlmService` registration | your `Program.cs` | one `AddSingleton<ILlmService, …>` per backend, **before** `Build()` |
| `ServiceType` | `appsettings.json`, per provider | must match the `ILlmService.Name` of a registered service |
| `Provider` | `appsettings.json`, per role | must match a key under `Providers` |

Current `ILlmService.Name` values: `openai` (`OpenAILLmService.OpenAILlmService`)
and `gemini` (`GeminiLlmService.GeminiLlmService`).

`appsettings.json`:

```json
{
  "AITaskAgent": {
    "LlmProviders": {
      "Providers": {
        "OpenRouter": {
          "ServiceType": "openai",
          "BaseUrl": "https://openrouter.ai/api/v1/",
          "ApiKey": "${OPENROUTER_API_KEY}"
        }
      },
      "Roles": {
        "default": { "Provider": "OpenRouter", "Model": "openai/gpt-4o-mini" }
      },
      "DefaultRole": "default"
    }
  }
}
```

`ServiceType` is what decouples the provider *key* from the service that serves
it. `"OpenRouter"` is a free-form label; without `ServiceType` the framework
looks for an `ILlmService` literally named `OpenRouter`, finds none, and throws.

`${VAR}` placeholders are expanded from environment variables by the framework
itself (`GetEx`/`BindEx`), for every string value under `LlmProviders`. You do
not need your own substitution pass. Keep real keys out of `appsettings.json`:
either export the variable, or put the literal in `appsettings.Development.json`
(covered by `.gitignore`).

`Program.cs`:

```csharp
using AITaskAgent.Configuration;
using AITaskAgent.Core.Execution;
using AITaskAgent.LLM.Abstractions;    // ILlmService
using AITaskAgent.LLM.Configuration;   // LlmEnvironment
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

// Register every backend you need. Registering the same interface twice is fine:
// providers are matched by ServiceType, not by DI order.
builder.Services.AddSingleton<ILlmService, OpenAILLmService.OpenAILlmService>();
builder.Services.AddAITaskAgent();

var host = builder.Build();
host.Services.UseAITaskAgent();

// Roles are a dictionary on the bound options. There is no resolver service.
var env = host.Services.GetRequiredService<IOptions<LlmEnvironment>>().Value;
var role = env.Roles[env.DefaultRole];
```

`role` is what you hand to a `BaseLlmStep` — see
[Quick Start §2](quick-start.md) for the full LLM pipeline, and
[LLM Steps](../guides/llm-steps.md) for prompts, tools and structured output.

## 4. Two calls you cannot skip

- **`services.AddAITaskAgent()`** — registers `IPipeline`, `PipelineContextFactory`,
  `IEventChannel`, storages, the template renderer, cache and
  `ConfigureLlmEnvironmentOptions`. Full table in
  [Installation](installation.md#basic-setup).
- **`provider.UseAITaskAgent()`** — forces creation of those singletons and copies
  every DI-registered `ITool` into `AvailableTools.Instance`. Omit it and the
  first `PipelineContextFactory.Create()` throws
  `InvalidOperationException: PipelineContextFactory is not initialized.`

The order in which you register `ILlmService` relative to `AddAITaskAgent()` does
not matter — `ConfigureLlmEnvironmentOptions` resolves the services lazily, the
first time `IOptions<LlmEnvironment>` is read. `PipelineVisualizer` registers it
after; `SimpleChatDemo` before. Both work.

> [!NOTE]
> `YamlPipelineDemo` reaches the same static initialization with a bare
> `sp.GetRequiredService<PipelineContextFactory>()` instead of
> `UseAITaskAgent()`. That does publish the static instance (it is assigned in the
> factory's constructor), so `PipelineContextFactory.Create()` works — but it
> skips the `ITool` registration pass. That sample declares no tools, so nothing
> breaks today. Do not copy the shortcut: add one `ITool` and it will be invisible
> to `AvailableTools.Instance`.

## 5. Do not hand-roll the configuration

Two samples in this repository (`FileToolsTestApp`, `AdvancedLlmPipeline`)
rebuild the `LlmEnvironment` by hand: plain `Bind()`, their own `${VAR}` regex,
and reflection to attach an `ILlmService` guessed from a substring of the
provider key, with a silent fallback to "the first provider in the dictionary"
when a role name does not match. They are **not** the pattern to copy — see
[the configuration audit](../project_notes/sample-configuration-audit.md).
Follow `SimpleChatDemo`, `McpIntegrationDemo`, `PipelineVisualizer` or
`YamlPipelineDemo` instead.

What hand-rolling costs you: startup validation (a typo in a role's `Provider`
silently points at the wrong backend instead of throwing), automatic `${VAR}`
expansion, and matching by `ServiceType` instead of by whatever words happen to
appear in a provider key.

## 6. Startup errors you will actually hit

The first three are thrown by `ConfigureLlmEnvironmentOptions` when
`IOptions<LlmEnvironment>` is first resolved — not at `AddAITaskAgent()`.

| Message | Cause |
|---|---|
| `LLM provider 'X' requests service type 'X', but no matching ILlmService is registered. Registered services: openai.` | missing `ServiceType`, or the named service is not registered |
| `LLM role 'X' references provider 'Y', which is not configured` | the role's `Provider` is not a key under `Providers` |
| `Default LLM role 'X' is not configured` | `DefaultRole` names a role that does not exist |
| `PipelineContextFactory is not initialized` | `UseAITaskAgent()` was never called |
| `env.Providers` is empty | `appsettings.json` was not copied to the output folder |

## Next Steps

- [Quick Start](quick-start.md) — the LLM pipeline in full
- [Architecture](architecture.md) — how the pieces fit together
- [Configuration](../guides/configuration.md) — full schema and validation rules
- [Core Concepts](../guides/core-concepts.md) — terminology
