# AITaskAgent Framework

> [!NOTE]
> This project is under active development and is not yet production-ready. APIs and behavior may change without notice.

A powerful .NET framework for building AI agent pipelines with LLM integration, tools, observability, and resilience.

## Features

- **Pipeline Architecture** - Compose steps into complex workflows
- **Dynamic Execution Graph** - Branching (groups), parallelism, and dynamic routing
- **LLM Integration** - Provider-agnostic LLM service with OpenAI support
- **Intention Routing** - LLM-based intent classification and routing
- **Tool System** - Define tools that LLMs can invoke
- **Conversation Management** - Context with bookmarks and sliding window
- **Observability** - Built-in events, OpenTelemetry traces, and metrics
- **Resilience** - Retry, timeout, circuit breaker, rate limiting
- **Extensible** - Custom steps, tools, middlewares, and providers

## Pipeline Flow Control

Pipelines are not just linear sequences. They support complex flows:

```
Pipeline
├── Step1 (sequential)
├── GroupStep ────┬─ NestedStep1 (branch: sequential)
│                 ├─ NestedStep2 (branch: sequential)
│                 └─ ... (can nest another GroupStep)
├── ParallelStep ─┬─ TaskA (concurrent)
│                 ├─ TaskB (concurrent)
│                 └─ TaskC (concurrent)
└── SwitchStep ───┬─ RouteA (conditional)
                  └─ RouteB (conditional)
```

> After GroupStep completes its nested steps, execution continues with the next step in the parent flow.

### Flow Control Steps

| Step Type | Behavior | Use Case |
|-----------|----------|----------|
| `GroupStep` | Sequential branch (nestable) | Logical grouping, sub-workflows |
| `ParallelStep` | Concurrent execution | Fetch multiple APIs simultaneously |
| `SwitchStep` | Dynamic routing | Intent-based routing |
| `IntentionRouterStep` | LLM-based routing | AI-classified routing |

### Key Concept: NextSteps

Steps can dynamically inject more steps via `NextSteps`:

```csharp
result.NextSteps.Add(new AdditionalStep());
// Pipeline will execute AdditionalStep after current step
```

## Quick Start

```csharp
using AITaskAgent.Configuration;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Steps;

// Register services
services.AddAITaskAgent();

// Simple linear pipeline
var result = await Pipeline.ExecuteAsync(
    name: "GreetingPipeline",
    steps: [
        new ActionStep<EmptyResult, StringResult>("Greet", 
            (input, ctx) => Task.FromResult(new StringResult("Hello!")))
    ],
    input: new EmptyResult()
);

// Parallel execution
var parallel = new ParallelStep<EmptyResult>("FetchAll",
    new FetchUsersStep(),
    new FetchOrdersStep()
);
```

## Documentation

### Getting Started
- [Installation](docs/getting-started/installation.md)
- [Quick Start](docs/getting-started/quick-start.md)
- [Architecture](docs/getting-started/architecture.md)

### Guides
- [Core Concepts](docs/guides/core-concepts.md)
- [Steps](docs/guides/steps.md) - Including flow control steps
- [LLM Steps](docs/guides/llm-steps.md)
- [Tools](docs/guides/tools.md)
- [Conversation](docs/guides/conversation.md)
- [Conversation Storage](docs/guides/conversation-storage.md)
- [Middlewares](docs/guides/middlewares.md)
- [Event System](docs/guides/event-system.md)
- [OpenTelemetry](docs/guides/opentelemetry.md)
- [Resilience](docs/guides/resilience.md)
- [Intention Routing](docs/guides/intention-routing.md)
- [Configuration](docs/guides/configuration.md)

### Extensibility
- [Custom Steps](docs/extensibility/custom-steps.md)
- [Custom Tools](docs/extensibility/custom-tools.md)
- [Custom Middlewares](docs/extensibility/custom-middlewares.md)
- [Custom Events](docs/extensibility/custom-events.md)
- [Custom LLM Providers](docs/extensibility/custom-llm-providers.md)

### API Reference
- [Interfaces](docs/api-reference/interfaces.md)
- [Core Namespace](docs/api-reference/core-namespace.md)
- [LLM Namespace](docs/api-reference/llm-namespace.md)
- [Observability Namespace](docs/api-reference/observability-namespace.md)
- [Extensions](docs/api-reference/extensions.md)

### Examples
- [Basic Pipeline](docs/examples/basic-pipeline.md) - Linear and parallel
- [LLM with Tools](docs/examples/llm-with-tools.md)
- [Custom Middleware](docs/examples/custom-middleware.md)

## Requirements

- .NET 10.0+
- C# 14

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| Microsoft.Extensions.* | 10.0.0 | DI, Logging, Options |
| Newtonsoft.Json | 13.0.4 | JSON serialization |
| NJsonSchema | 11.0.0 | JSON Schema for tools |
| OpenAI | 2.7.0 | OpenAI SDK |

## License

MIT License
