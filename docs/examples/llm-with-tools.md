# LLM with Tools Example

## Complete Example

```csharp
using AITaskAgent.Configuration;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

try
{
    // Setup
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddLogging(b => b.AddConsole());

    // Register every provider implementation as ILlmService.
    // Configuration picks one per provider by matching ServiceType against ILlmService.Name.
    services.AddSingleton<ILlmService, OpenAILLmService.OpenAILlmService>();
    services.AddSingleton<ILlmService, GeminiLlmService.GeminiLlmService>();

    services.AddAITaskAgent();

    var provider = services.BuildServiceProvider();
    provider.UseAITaskAgent();

    var pipeline     = provider.GetRequiredService<IPipeline>();
    var eventChannel = provider.GetRequiredService<IEventChannel>();

    // Roles come from AITaskAgent:LlmProviders:Roles - no ILlmProviderResolver involved.
    var env  = provider.GetRequiredService<IOptions<LlmEnvironment>>().Value;
    var role = env.Roles[env.DefaultRole];

    // Subscribe to events. One handler, filtering by type.
    using var subscription = eventChannel.Subscribe((evt, ct) =>
    {
        switch (evt)
        {
            case LlmResponseEvent llm when llm.FinishReason == FinishReason.Streaming:
                Console.Write(llm.Content);
                break;

            case ToolCompletedEvent tool:
                Console.WriteLine($"\n[Tool: {tool.ToolName} - {(tool.Success ? "OK" : "FAILED")}]");
                break;
        }
        return Task.CompletedTask;
    });

    // Create the LLM step. The role carries provider, model, system prompt and fallbacks.
    var llmStep = new BaseLlmStep<LlmStringStepResult, LlmStringStepResult>(
        name: "Assistant",
        role: role,
        prompt: "What's 25 * 4? Also, what's the weather in Paris?")
    {
        Tools = [new CalculatorTool(), new WeatherTool()],
        MaxToolIterations = 5
    };

    var context = PipelineContextFactory.Create();

    // Execute
    var result = await pipeline.ExecuteAsync(
        name: "LlmWithTools",
        steps: [llmStep],
        input: "start",
        context: context);

    if (result is LlmStringStepResult llmResult)
    {
        Console.WriteLine($"\n\nFinal Answer: {llmResult.Content}");
        Console.WriteLine($"Tokens: {llmResult.TokensUsed}");
        Console.WriteLine($"Cost:   {llmResult.CostUsd:C}");
        Console.WriteLine($"Model:  {llmResult.Model}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}

// Tool implementations
public class CalculatorTool : ITool
{
    public string Name => "calculator";
    public string Description => "Performs arithmetic: add, subtract, multiply, divide";

    // Pure computation, no side effects: the tool-calling loop tolerates 3 identical
    // repeats instead of 1.
    public bool IsReadOnly => true;

    // ParametersJsonSchema is a JSON Schema string.
    public ToolDefinition GetDefinition() => new()
    {
        Name = Name,
        Description = Description,
        ParametersJsonSchema = """
        {
            "type": "object",
            "properties": {
                "operation": { "type": "string", "enum": ["add", "subtract", "multiply", "divide"] },
                "a": { "type": "number" },
                "b": { "type": "number" }
            },
            "required": ["operation", "a", "b"]
        }
        """
    };

    public Task<string> ExecuteAsync(
        string argumentsJson,
        PipelineContext context,
        string stepName,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var args = JsonConvert.DeserializeObject<Args>(argumentsJson)!;
            var result = args.Operation switch
            {
                "add"      => args.A + args.B,
                "subtract" => args.A - args.B,
                "multiply" => args.A * args.B,
                "divide"   => args.B != 0 ? args.A / args.B : throw new DivideByZeroException(),
                _ => throw new ArgumentException("Unknown operation")
            };
            return Task.FromResult(result.ToString());
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Error: {ex.Message}");
        }
    }

    record Args(string Operation, double A, double B);
}

public class WeatherTool : ITool
{
    public string Name => "weather";
    public string Description => "Gets weather for a city";
    public bool IsReadOnly => true;

    public ToolDefinition GetDefinition() => new()
    {
        Name = Name,
        Description = Description,
        ParametersJsonSchema = """
        {
            "type": "object",
            "properties": { "city": { "type": "string" } },
            "required": ["city"]
        }
        """
    };

    public Task<string> ExecuteAsync(
        string argumentsJson,
        PipelineContext context,
        string stepName,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var args = JsonConvert.DeserializeObject<Args>(argumentsJson)!;
        // Simulated response
        return Task.FromResult($"{{\"city\":\"{args.City}\",\"temp\":\"22°C\",\"condition\":\"Sunny\"}}");
    }

    record Args(string City);
}
```

**Output:**
```
[Tool: calculator - OK]
[Tool: weather - OK]

Final Answer: 25 × 4 = 100. The weather in Paris is 22°C and Sunny.
Tokens: 245
Cost:   $0.0004
Model:  gpt-4o
```

---

## Notes

**Tools by name.** `TemplateLlmStep` and `IntentionAnalyzerStep` take an `IList<string>` of tool
names and resolve them through `AvailableTools.Instance`, so those tools must be registered before
the step is constructed:

```csharp
AvailableTools.Instance.Register(new CalculatorTool());

var step = new TemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
    name: "Assistant",
    role: role,
    templateProvider: new FileTemplateProvider("./prompts"),
    tools: ["calculator", "weather"]);
```

A role can also declare its own `Tools`, which a step built from it inherits.

**Cost.** `LlmStringStepResult.CostUsd` is filled by `BaseLlmStep` from
`LlmProvider.CalculateCost`, using the per-million prices declared in the provider's `Models`
list. Without prices in configuration it stays null.

**Loop bounds.** `MaxToolIterations` (default 5) caps the tool-calling loop. On top of that the
step detects a model repeating the same calls with identical arguments and stops early — after 3
repeats when every call is `IsReadOnly`, after 1 otherwise. It does not fail the step: the tool
calls are dropped and the model is told to answer with what it has.

**Fallbacks.** If the role declares `Fallbacks` (or the environment declares
`FallbackProviders`), a failed attempt switches to the next model and rewinds the conversation to
the bookmark taken before the step ran.
