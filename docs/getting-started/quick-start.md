# Quick Start

Get a pipeline running in under 5 minutes.

## 1. Simple Pipeline (No LLM)

```csharp
using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddAITaskAgent();

var host = builder.Build();
host.Services.UseAITaskAgent();   // required: initializes the static factories

// The pipeline is a DI service, not a static class
var pipeline = host.Services.GetRequiredService<IPipeline>();

// Define steps. DelegatedStep receives itself as first argument so it can
// build a result bound to the right step instance.
var step1 = new DelegatedStep<StepResult<string>, StepResult<string>>(
    "GetName",
    (step, input, context, attempt, last) =>
        Task.FromResult(new StepResult<string>(step, "World")));

var step2 = new DelegatedStep<StepResult<string>, StepResult<string>>(
    "Greet",
    (step, input, context, attempt, last) =>
        Task.FromResult(new StepResult<string>(step, $"Hello, {input.Value}!")));

// Execute. `input` may be a raw value: the pipeline wraps it into the
// input type declared by the first step.
var result = await pipeline.ExecuteAsync(
    name: "GreetingPipeline",
    steps: [step1, step2],
    input: string.Empty);

if (result.HasError)
{
    Console.WriteLine($"Pipeline error: {result.Error?.Message}");
}
else
{
    Console.WriteLine(result.Value);   // Hello, World!
}
```

## 2. Pipeline with LLM

LLM behaviour comes from a **role** (`LlmRole`), resolved out of the bound
`LlmEnvironment`. There is no provider resolver service: roles are a dictionary on
`IOptions<LlmEnvironment>`.

```csharp
using AITaskAgent.Configuration;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<ILlmService, OpenAILlmService>();
builder.Services.AddAITaskAgent();

var host = builder.Build();
host.Services.UseAITaskAgent();

var pipeline = host.Services.GetRequiredService<IPipeline>();
var env = host.Services.GetRequiredService<IOptions<LlmEnvironment>>().Value;
var role = env.Roles[env.DefaultRole];

// A context carries the conversation, metadata and the step-result cache
var conversation = new ConversationContext();
var context = PipelineContextFactory.Create(conversation);

var llmStep = new BaseLlmStep<LlmStringStepResult, LlmStringStepResult>(
    name: "AskAI",
    role: role,
    prompt: "What is 2 + 2? Answer with just the number.");

var result = await pipeline.ExecuteAsync(
    name: "MathPipeline",
    steps: [llmStep],
    input: "What is 2 + 2? Answer with just the number.",
    context: context);

if (result is LlmStringStepResult llmResult)
{
    Console.WriteLine($"AI says: {llmResult.Content}");
    Console.WriteLine($"Tokens used: {llmResult.TokensUsed}");
    Console.WriteLine($"Cost (USD): {llmResult.CostUsd}");
}
```

## Expected Output

```
AI says: 4
Tokens used: 25
Cost (USD): 0.0000125
```

## Next Steps

- [Architecture](architecture.md) - Understand the framework design
- [Configuration](../guides/configuration.md) - Providers, roles and inference settings
- [Core Concepts](../guides/core-concepts.md) - Learn key terminology
- [Steps](../guides/steps.md) - Explore step types
