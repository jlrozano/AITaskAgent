## Overview

Tools are functions that LLMs can call to perform actions or get information. The framework provides two ways to create tools:

1. **`LlmTool` (Recommended)** - Abstract base class with automatic observability
2. **`ITool`** - Interface for full control

![The tool loop and its four exits: final answer, iteration cap, repetition cut and tool stop request](../assets/images/tool-invocation-loop.png)

<!-- Source: docs/assets/diagrams/tool-invocation-loop.excalidraw -->

> [!TIP]
> For advanced topics including recursive execution, observability enrichment, and best practices, see the [Tools Advanced Guide](tools-advanced.md).

---

## Quick Start with LlmTool

The recommended way to create tools is by extending `LlmTool`:

```csharp
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Tools.Base;
using Microsoft.Extensions.Logging;
using System.Text.Json;

public class WeatherTool : LlmTool
{
    public override string Name => "get_weather";
    
    public override string Description => 
        "Gets current weather for a location";
    
    protected override BinaryData ParametersSchema => BinaryData.FromString("""
    {
        "type": "object",
        "properties": {
            "location": {
                "type": "string",
                "description": "City name, e.g. 'London'"
            }
        },
        "required": ["location"]
    }
    """);
    
    protected override async Task<string> InternalExecuteAsync(
        string argumentsJson,
        PipelineContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var args = JsonSerializer.Deserialize<WeatherArgs>(argumentsJson);
        var weather = await FetchWeatherAsync(args.Location);
        return JsonSerializer.Serialize(weather);
    }
}

record WeatherArgs(string Location);
```

**Benefits of `LlmTool`**:
- ✅ Automatic observability (traces, metrics, events)
- ✅ Template Method pattern for consistency
- ✅ Enrichment hooks for custom telemetry
- ✅ Error handling built-in

---

## Implementing `ITool` Directly

```csharp
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Tools.Abstractions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

public class CalculatorTool : ITool
{
    public string Name => "calculator";
    public string Description => "Performs basic arithmetic operations";

    // Pure computation, no side effects — see "Read-only tools" below
    public bool IsReadOnly => true;
    
    public ToolDefinition GetDefinition() => new()
    {
        Name = Name,
        Description = Description,
        // ToolDefinition carries the schema as a JSON string (required)
        ParametersJsonSchema = JsonConvert.SerializeObject(new
        {
            type = "object",
            properties = new
            {
                operation = new { type = "string", @enum = new[] { "add", "subtract", "multiply", "divide" } },
                a = new { type = "number", description = "First operand" },
                b = new { type = "number", description = "Second operand" }
            },
            required = new[] { "operation", "a", "b" }
        })
    };
    
    public async Task<string> ExecuteAsync(
        string argumentsJson,
        PipelineContext context,
        string stepName,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var args = JsonConvert.DeserializeObject<CalculatorArgs>(argumentsJson);
            
            var result = args.Operation switch
            {
                "add" => args.A + args.B,
                "subtract" => args.A - args.B,
                "multiply" => args.A * args.B,
                "divide" => args.B != 0 ? args.A / args.B : throw new DivideByZeroException(),
                _ => throw new ArgumentException($"Unknown operation: {args.Operation}")
            };
            
            logger.LogInformation("Calculator: {A} {Op} {B} = {Result}", 
                args.A, args.Operation, args.B, result);
            
            return result.ToString();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Calculator tool failed");
            return $"Error: {ex.Message}";
        }
    }
    
    private record CalculatorArgs(string Operation, double A, double B);
}
```

## Read-only tools

`ITool` declares:

```csharp
bool IsReadOnly => false;   // assume side effects unless the tool says otherwise
```

The tool-calling loop in `BaseLlmStep` uses it to decide how much repetition to tolerate. Calls
are compared by a normalised key (name + arguments):

| Calls | When the loop is cut |
|-------|----------------------|
| A call to an `IsReadOnly = true` tool | Its key appears in 3 of the last 10 iterations (this one included), consecutive or not |
| A batch with any mutating call | The same set of calls repeats on consecutive iterations a 2nd time (1 repeat allowed) |

An iteration whose response executed an action tag counts as progress: read-only occurrences are
only counted from the last such iteration on. When the guard fires, the step does not fail: the
response is turned into a final answer telling the model why it was cut off. A tool can also end
the loop itself by throwing `ToolLoopStopRequestedException`; its message becomes the tool result
and the final answer. `MaxToolIterations` (default 5) is the hard backstop: reaching it returns a
failed result with `ToolIterationLimitExceededException`.

Re-reading the same file can be legitimate exploration; repeating a write with identical arguments
cannot. The flag is declared per tool rather than inferred from a list of known names, so the
framework stays independent of any particular tool package.

## Using Tools in an LLM Step

```csharp
var step = new BaseLlmStep<LlmStringStepResult, LlmStringStepResult>(
    name: "Assistant",
    role: env.Roles["default"],
    prompt: "{{ input }}")
{
    Tools = [new CalculatorTool(), new WebSearchTool(), new WeatherTool()]
};
```

`Tools` defaults to `role.Tools ?? []`. `TemplateLlmStep` and `IntentionAnalyzerStep` also accept an
`IList<string>` of tool **names**, resolved through `AvailableTools.Instance.GetTools(...)` (with no
names they start with an empty list rather than `role.Tools`).

Requested calls are matched against `Tools` by exact `Name` (case-sensitive) and run one after
another. An unknown name, or an exception thrown by the tool, is reported back to the model as the
tool message (`Error: Tool '...' not found` / `Error executing tool: ...`); it does not fail the step.

## Tool Registry: `ITools` / `AvailableTools`

`IToolRegistry` / `ToolRegistry` are gone. The registry contract is `ITools`, implemented by
`AvailableTools`, which also exposes a process-wide `AvailableTools.Instance`.

```csharp
public interface ITools
{
    void Register(ITool tool);
    ITool? GetTool(string name);
    IReadOnlyList<ITool> GetTools(params string[] names);
    IReadOnlyList<ITool> GetAllTools(string? moduleName = null);
}
```

`AddAITaskAgent()` registers `ITools` as `AvailableTools.Instance`. `UseAITaskAgent()` then walks
every `ITool` registered in DI and calls `Register` on it:

```csharp
// Registration
services.AddSingleton<ITool, CalculatorTool>();
services.AddSingleton<ITool, WebSearchTool>();
services.AddAITaskAgent();

var provider = services.BuildServiceProvider();
provider.UseAITaskAgent();          // pushes both tools into AvailableTools.Instance

// Retrieval
var tools = provider.GetRequiredService<ITools>();
var calculator = tools.GetTool("calculator");
var subset = tools.GetTools("calculator", "web_search");
var all = tools.GetAllTools();
```

## Observability

Tools that extend `LlmTool` emit observability data automatically. A type that implements `ITool`
directly emits **nothing** — the events, metrics and traces below live in `LlmTool.ExecuteAsync`,
so a hand-rolled `ITool` must raise them itself or go unobserved.

### Events

| Event | When | Properties |
|-------|------|------------|
| `ToolStartedEvent` | Before tool execution | StepName, ToolName, CorrelationId, AdditionalData["parameters"] |
| `ToolCompletedEvent` | After execution (success or error) | ToolName, Success, Duration, ErrorMessage, AdditionalData["parameters"] (+ ["result"] on success) |

```csharp
using var subscription = eventChannel.Subscribe(async (evt, ct) =>
{
    if (evt is ToolCompletedEvent e)
    {
        Console.WriteLine($"Tool {e.ToolName}: {(e.Success ? "OK" : e.ErrorMessage)}");
        Console.WriteLine($"Duration: {e.Duration.TotalMilliseconds}ms");
    }
});
```

### Metrics

When using `LlmTool`, these metrics are automatically recorded:

| Metric | Type | Dimensions |
|--------|------|------------|
| `aitaskagent.tool.executions` | Counter | tool.name, step.success |
| `aitaskagent.tool.duration` | Histogram | tool.name |

### Traces

OpenTelemetry activities are created for each tool execution:

- **Activity Name**: `Tool.{Name}`
- **Tags**: tool.name, step.name, correlation.id, tool.duration_ms, tool.success

See [Tools Advanced Guide](tools-advanced.md#automatic-observability) for enrichment hooks.

---

## Best Practices

1. **Keep tools focused** - One tool, one purpose
2. **Return strings** - LLMs work with text
3. **Handle errors** - Return clear error messages; a thrown exception is caught by the step and sent to the model as `Error executing tool: <message>`
4. **Log execution** - Use the provided logger
5. **Document parameters** - Clear descriptions help LLMs
