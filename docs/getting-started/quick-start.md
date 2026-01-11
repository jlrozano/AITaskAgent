# Quick Start

Get a pipeline running in under 5 minutes.

## 1. Simple Pipeline (No LLM)

```csharp
using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.Core.StepResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

try
{
    // Setup
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddAITaskAgent();
    var host = builder.Build();
    
    // Initialize Pipeline logger
    Pipeline.LoggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
    
    // Define steps
    var step1 = new ActionStep<EmptyResult, StringResult>(
        "GetName",
        async (input, context) =>
        {
            // Simulate getting a name
            return new StringResult("World");
        });
    
    var step2 = new ActionStep<StringResult, StringResult>(
        "Greet",
        async (input, context) =>
        {
            return new StringResult($"Hello, {input.Value}!");
        });
    
    // Execute pipeline
    var result = await Pipeline.ExecuteAsync(
        name: "GreetingPipeline",
        steps: [step1, step2],
        input: new EmptyResult()
    );
    
    if (result is StringResult stringResult)
    {
        Console.WriteLine(stringResult.Value); // Output: Hello, World!
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Pipeline error: {ex.Message}");
}
```

## 2. Pipeline with LLM

```csharp
using AITaskAgent.Configuration;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddAITaskAgent();
    builder.Services.AddSingleton<ILlmService, OpenAILlmService>();
    var host = builder.Build();
    
    Pipeline.LoggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
    
    var llmService = host.Services.GetRequiredService<ILlmService>();
    var providerResolver = host.Services.GetRequiredService<ILlmProviderResolver>();
    var profile = providerResolver.Resolve("default");
    
    // Create context with conversation
    var contextFactory = host.Services.GetRequiredService<PipelineContextFactory>();
    var context = contextFactory.Create();
    
    // LLM Step
    var llmStep = new LlmStep<EmptyResult, LlmStepResult>(
        llmService,
        "AskAI",
        profile,
        messageBuilder: async (input, ctx) => "What is 2 + 2? Answer with just the number."
    );
    
    var result = await Pipeline.ExecuteAsync(
        name: "MathPipeline",
        steps: [llmStep],
        input: new EmptyResult(),
        context: context
    );
    
    if (result is LlmStepResult llmResult)
    {
        Console.WriteLine($"AI says: {llmResult.Value}");
        Console.WriteLine($"Tokens used: {llmResult.TokensUsed}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
```

## Expected Output

```
AI says: 4
Tokens used: 25
```

## Next Steps

- [Architecture](architecture.md) - Understand the framework design
- [Core Concepts](../guides/core-concepts.md) - Learn key terminology
- [Steps](../guides/steps.md) - Explore step types
