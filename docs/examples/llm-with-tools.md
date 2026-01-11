# LLM with Tools Example

## Complete Example

```csharp
using AITaskAgent.Configuration;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

try
{
    // Setup
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddAITaskAgent();
    builder.Services.AddSingleton<ILlmService, OpenAILlmService>();
    var host = builder.Build();
    
    Pipeline.LoggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
    
    var llmService = host.Services.GetRequiredService<ILlmService>();
    var resolver = host.Services.GetRequiredService<ILlmProviderResolver>();
    var profile = resolver.Resolve("default");
    var eventChannel = host.Services.GetRequiredService<IEventChannel>();
    var contextFactory = host.Services.GetRequiredService<PipelineContextFactory>();
    
    // Subscribe to events
    eventChannel.Subscribe<LlmResponseEvent>(async e =>
    {
        if (e.FinishReason == FinishReason.Streaming)
            Console.Write(e.Content);
    });
    
    eventChannel.Subscribe<ToolCompletedEvent>(async e =>
    {
        Console.WriteLine($"\n[Tool: {e.ToolName} - {(e.Success ? "OK" : "FAILED")}]");
    });
    
    // Define tools
    var tools = new List<ITool>
    {
        new CalculatorTool(),
        new WeatherTool()
    };
    
    // Create LLM step
    var llmStep = new LlmStep<EmptyResult, LlmStepResult>(
        llmService,
        "Assistant",
        profile,
        messageBuilder: (input, ctx) => Task.FromResult(
            "What's 25 * 4? Also, what's the weather in Paris?"),
        tools: tools
    );
    
    var context = contextFactory.Create();
    
    // Execute
    var result = await Pipeline.ExecuteAsync(
        name: "LlmWithTools",
        steps: [llmStep],
        input: new EmptyResult(),
        context: context
    );
    
    if (result is LlmStepResult llmResult)
    {
        Console.WriteLine($"\n\nFinal Answer: {llmResult.Value}");
        Console.WriteLine($"Tokens: {llmResult.TokensUsed}");
        Console.WriteLine($"Model: {llmResult.Model}");
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
    
    public ToolDefinition GetDefinition() => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new
        {
            type = "object",
            properties = new
            {
                operation = new { type = "string", @enum = new[] { "add", "subtract", "multiply", "divide" } },
                a = new { type = "number" },
                b = new { type = "number" }
            },
            required = new[] { "operation", "a", "b" }
        }
    };
    
    public Task<string> ExecuteAsync(
        string argumentsJson,
        PipelineContext context,
        string stepName,
        ILogger logger,
        CancellationToken ct = default)
    {
        try
        {
            var args = JsonConvert.DeserializeObject<Args>(argumentsJson);
            var result = args.Operation switch
            {
                "add" => args.A + args.B,
                "subtract" => args.A - args.B,
                "multiply" => args.A * args.B,
                "divide" => args.B != 0 ? args.A / args.B : throw new DivideByZeroException(),
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
    
    public ToolDefinition GetDefinition() => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new
        {
            type = "object",
            properties = new { city = new { type = "string" } },
            required = new[] { "city" }
        }
    };
    
    public Task<string> ExecuteAsync(
        string argumentsJson,
        PipelineContext context,
        string stepName,
        ILogger logger,
        CancellationToken ct = default)
    {
        var args = JsonConvert.DeserializeObject<Args>(argumentsJson);
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
Model: gpt-4o
```
