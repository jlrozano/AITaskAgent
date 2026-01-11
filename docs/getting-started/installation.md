# Installation

## Requirements

- **.NET 10.0** or later
- **C# 14** language version
- Visual Studio 2025+ / VS Code with C# extension / JetBrains Rider

## Install via NuGet

```bash
dotnet add package AITaskAgent
```

Or in your `.csproj`:

```xml
<PackageReference Include="AITaskAgent" Version="1.0.0" />
```

## Basic Setup

Add the framework to your service collection in `Program.cs`:

```csharp
using AITaskAgent.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Add AITaskAgent services
builder.Services.AddAITaskAgent();

// Add your LLM service implementation
builder.Services.AddSingleton<ILlmService, OpenAILlmService>();

var host = builder.Build();
await host.RunAsync();
```

## Configuration (appsettings.json)

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
    }
  },
  "LlmProviders": {
    "Profiles": {
      "default": {
        "Provider": "OpenAI",
        "Model": "gpt-4o",
        "Temperature": 0.7,
        "MaxTokens": 4096
      },
      "fast": {
        "Provider": "OpenAI",
        "Model": "gpt-4o-mini",
        "Temperature": 0.3
      }
    }
  }
}
```

## Verify Installation

```csharp
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Steps;
using AITaskAgent.Core.StepResults;

try
{
    var result = await Pipeline.ExecuteAsync(
        name: "TestPipeline",
        steps: [new EmptyStep()],
        input: new EmptyResult()
    );
    
    Console.WriteLine($"Pipeline executed successfully: {!result.HasError}");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
```

## Troubleshooting

### Common Issues

| Problem | Solution |
|---------|----------|
| `ILlmService not registered` | Add an LLM service implementation (e.g., `OpenAILlmService`) |
| `Pipeline timeout` | Increase `DefaultPipelineTimeout` in configuration |
| `Rate limit exceeded` | Adjust `RateLimit` settings |

### Enable Debug Logging

```csharp
builder.Logging.AddFilter("AITaskAgent", LogLevel.Debug);
```
