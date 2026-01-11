# Intention Routing

## Overview

AITaskAgent includes LLM-powered intent classification and routing, enabling intelligent workflows based on user intent.

## Components

| Component | Purpose |
|-----------|---------|
| `IntentionAnalyzerStep` | Classifies user input into predefined intents using LLM |
| `IntentionRouterStep` | Routes to different pipelines based on analyzed intent |

---

## IntentionAnalyzerStep

Analyzes user input and returns an `Intention<TEnum>` with the classified intent.

### Usage

```csharp
using AITaskAgent.LLM.Steps;
using AITaskAgent.LLM.Results;
using System.ComponentModel;

// 1. Define your intents
public enum UserIntent
{
    [Description("User wants to buy something")]
    Sales,
    
    [Description("User needs help with an issue")]
    Support,
    
    [Description("User is asking a general question")]
    FAQ
}

// 2. Create analyzer step
var analyzer = new IntentionAnalyzerStep<StringResult, UserIntent>(
    llmService,
    profile,
    systemPrompt: "You are an expert at understanding user intentions."
);

// 3. Execute
var result = await Pipeline.ExecuteAsync(
    name: "IntentAnalysis",
    steps: [analyzer],
    input: new StringResult("I want to return my order")
);

// 4. Access result
if (result is LlmStepResult<Intention<UserIntent>> intentResult)
{
    Console.WriteLine($"Intent: {intentResult.Value.Option}");      // Support
    Console.WriteLine($"Reason: {intentResult.Value.Reasoning}");   // User mentions returning order
    Console.WriteLine($"Confidence: {intentResult.Value.Confidence}"); // 0.95
}
```

### Intention Result Type

```csharp
public record Intention<TEnum> where TEnum : struct, Enum
{
    public required TEnum Option { get; init; }
    public required string Reasoning { get; init; }
    public float? Confidence { get; init; }
}
```

---

## IntentionRouterStep

Routes to different steps based on analyzed intention. Extends `SwitchStep`.

### Usage

```csharp
// Define routes for each intent
var routes = new Dictionary<UserIntent, IStep>
{
    [UserIntent.Sales] = new SalesPipelineStep(),
    [UserIntent.Support] = new SupportPipelineStep(),
    [UserIntent.FAQ] = new FaqPipelineStep()
};

var router = new IntentionRouterStep<UserIntent>(
    routes,
    defaultRoute: new FallbackStep()  // Optional
);
```

### Complete Pipeline

```csharp
var pipeline = await Pipeline.ExecuteAsync(
    name: "IntentionBasedRouting",
    steps: [
        // Step 1: Analyze intent
        new IntentionAnalyzerStep<StringResult, UserIntent>(llmService, profile),
        
        // Step 2: Route based on intent
        new IntentionRouterStep<UserIntent>(routes)
    ],
    input: new StringResult("How do I return an item?")
);
```

### Flow Diagram

![Intention Router Flow](../assets/images/intention_router_flow.png)

---

## Events

IntentionRouterStep emits a `StepRoutingEvent`:

```csharp
eventChannel.Subscribe<StepRoutingEvent>(async e =>
{
    Console.WriteLine($"Routed to: {e.SelectedRoute}");
    Console.WriteLine($"Reason: {e.RoutingReason}");
    // Example: "Intention: Support - User mentions returning order"
});
```

---

## Custom Prompt Builder

Override the prompt for intent analysis:

```csharp
var analyzer = new IntentionAnalyzerStep<MyInputType, UserIntent>(
    llmService,
    profile,
    promptBuilder: async (input, context) =>
    {
        // Custom prompt logic
        return $"Analyze: {input.Value}\nContext: {context.Metadata["customerType"]}";
    }
);
```
