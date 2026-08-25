# Custom Tools

## Overview

Create tools that LLMs can invoke to perform actions. You can use `LlmTool` (recommended) for automatic observability or implement `ITool` directly for full control.

---

## Using LlmTool (Recommended)

`LlmTool` provides automatic observability and the Template Method pattern.

### Basic Example

```csharp
using AITaskAgent.LLM.Tools.Base;
using System.Text.Json;

public class WeatherTool : LlmTool
{
    public override string Name => "get_weather";
    public override string Description => "Gets current weather for a location";
    
    protected override BinaryData ParametersSchema => BinaryData.FromString("""
    {
        "type": "object",
        "properties": {
            "location": { 
                "type": "string", 
                "description": "City name, e.g., 'London'" 
            },
            "units": { 
                "type": "string", 
                "enum": ["celsius", "fahrenheit"],
                "description": "Temperature units"
            }
        },
        "required": ["location"]
    }
    """);
    
    // A tool that only reads state should say so - see "Read-only tools" below.
    public override bool IsReadOnly => true;
    
    protected override async Task<string> InternalExecuteAsync(
        string argumentsJson,
        PipelineContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var args = JsonSerializer.Deserialize<WeatherArgs>(argumentsJson);
        
        // Call weather API
        var weather = await FetchWeatherAsync(args.Location, args.Units, cancellationToken);
        
        return JsonSerializer.Serialize(weather);
    }
    
    private record WeatherArgs(string Location, string? Units = "celsius");
}
```

`LlmTool.ExecuteAsync` is the Template Method: it opens the OpenTelemetry activity, emits
`tool.started` / `tool.completed`, records metrics, and calls `InternalExecuteAsync` in between.
Exceptions thrown from `InternalExecuteAsync` are logged and re-thrown, after the completed event
has been emitted with `Success = false`.

---

## Read-only tools

`ITool.IsReadOnly` defaults to `false`: assume a tool has side effects unless it declares
otherwise. Declaring it matters because the tool-calling loop uses it to decide how much repetition
to tolerate.

When the model asks for exactly the same set of calls (same names, same arguments) in consecutive
iterations, `BaseLlmStep` counts the repeats and cuts the loop once a threshold is passed:

| All calls in the iteration are read-only | Identical repeats tolerated |
|---|---|
| yes | 3 |
| no | 1 |

Re-reading the same file is often legitimate exploration; repeating a write with identical
arguments means the model is stuck. When the threshold is crossed the step does not fail — the
tool calls are dropped and the model is told why, so it answers with what it already has.

```csharp
public class ReadFileTool : LlmTool
{
    public override string Name => "read_file";
    public override string Description => "Reads a text file";
    public override bool IsReadOnly => true;    // three identical reads are tolerated
    // ...
}

public class WriteFileTool : LlmTool
{
    public override string Name => "write_file";
    public override string Description => "Writes a text file";
    // IsReadOnly stays false: one identical repeat and the loop stops
}
```

The decision is per tool, not per name list, so the framework stays independent of any particular
tool package.

### UsageGuidelines

`ITool.UsageGuidelines` is optional behavioural guidance injected into the **system prompt**, as
opposed to `Description`, which travels in the `ToolDefinition` handed to the model.

```csharp
public override string? UsageGuidelines =>
    "Call read_file before proposing an edit. Never guess file contents.";
```

### With Observability Enrichment

Add custom telemetry data using enrichment hooks:

```csharp
public class WeatherTool : LlmTool
{
    // ... Name, Description, ParametersSchema, InternalExecuteAsync ...
    
    // Add custom tags BEFORE execution
    protected override void EnrichActivityBefore(
        Activity? activity,
        string argumentsJson,
        PipelineContext context)
    {
        var args = JsonSerializer.Deserialize<WeatherArgs>(argumentsJson);
        activity?.SetTag("weather.location", args.Location);
        activity?.SetTag("weather.unit", args.Units ?? "celsius");
    }
    
    // Add custom tags AFTER execution
    protected override void EnrichActivityAfter(
        Activity? activity,
        string result,
        PipelineContext context)
    {
        var weather = JsonSerializer.Deserialize<WeatherResult>(result);
        activity?.SetTag("weather.temperature", weather.Temp);
        activity?.SetTag("weather.condition", weather.Condition);
    }
    
    // Enrich started event
    protected override ToolStartedEvent EnrichStartedEvent(
        ToolStartedEvent baseEvent,
        string argumentsJson,
        PipelineContext context)
    {
        return baseEvent with
        {
            AdditionalData = new Dictionary<string, object>
            {
                ["location"] = JsonSerializer.Deserialize<WeatherArgs>(argumentsJson).Location
            }
        };
    }
}
```

---

## Implementing ITool

```csharp
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Tools.Abstractions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

public class WeatherTool : ITool
{
    public string Name => "get_weather";
    public string Description => "Gets current weather for a location";
    public bool IsReadOnly => true;
    
    // ParametersJsonSchema is a JSON Schema *string*, not an object graph.
    public ToolDefinition GetDefinition() => new()
    {
        Name = Name,
        Description = Description,
        ParametersJsonSchema = """
        {
            "type": "object",
            "properties": {
                "location": { "type": "string", "description": "City name, e.g., 'London'" },
                "units": {
                    "type": "string",
                    "enum": ["celsius", "fahrenheit"],
                    "description": "Temperature units"
                }
            },
            "required": ["location"]
        }
        """
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
            var args = JsonConvert.DeserializeObject<WeatherArgs>(argumentsJson);
            
            logger.LogInformation("Fetching weather for {Location}", args.Location);
            
            // Call weather API
            var weather = await FetchWeatherAsync(args.Location, args.Units, cancellationToken);
            
            return JsonConvert.SerializeObject(weather);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Weather fetch failed");
            return $"Error: {ex.Message}";
        }
    }
    
    private record WeatherArgs(string Location, string? Units = "celsius");
}
```

## Tool with Dependencies

Use constructor injection:

```csharp
public class DatabaseTool(IDbConnection db, ILogger<DatabaseTool> logger) : ITool
{
    public string Name => "query_database";
    public string Description => "Executes SQL SELECT queries";
    
    public bool IsReadOnly => true;   // SELECT only, enforced below
    
    public ToolDefinition GetDefinition() => new()
    {
        Name = Name,
        Description = Description,
        ParametersJsonSchema = """
        {
            "type": "object",
            "properties": {
                "query": { "type": "string", "description": "SQL SELECT query" }
            },
            "required": ["query"]
        }
        """
    };
    
    public async Task<string> ExecuteAsync(
        string argumentsJson,
        PipelineContext context,
        string stepName,
        ILogger _,
        CancellationToken cancellationToken = default)
    {
        var args = JsonConvert.DeserializeObject<QueryArgs>(argumentsJson);
        
        // Validate query is SELECT only
        if (!args.Query.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            return "Error: Only SELECT queries allowed";
        }
        
        var results = await db.QueryAsync(args.Query, cancellationToken);
        return JsonConvert.SerializeObject(results);
    }
    
    private record QueryArgs(string Query);
}
```

## Registration

### Manual

Any `BaseLlmStep` exposes a settable `Tools` collection:

```csharp
var step = new BaseLlmStep<LlmStringStepResult, LlmStringStepResult>("Assistant", role, prompt)
{
    Tools = [new WeatherTool(), new DatabaseTool(db, logger)]
};
```

An `LlmRole` can also carry `Tools`, in which case a step built from it starts with those.

### Via the registry

`IToolRegistry`/`ToolRegistry` were replaced by `ITools`/`AvailableTools`. `AvailableTools` is
registered as `ITools` by `AddAITaskAgent()`, using the process-wide `AvailableTools.Instance`, and
`UseAITaskAgent()` registers into it every `ITool` resolvable from DI.

```csharp
services.AddSingleton<ITool, WeatherTool>();
services.AddSingleton<ITool, DatabaseTool>();
services.AddAITaskAgent();

var provider = services.BuildServiceProvider();
provider.UseAITaskAgent();     // copies the ITool registrations into AvailableTools.Instance
```

Or register directly:

```csharp
AvailableTools.Instance.Register(new WeatherTool());
```

Reading back:

```csharp
var tools = serviceProvider.GetRequiredService<ITools>();

ITool? one           = tools.GetTool("get_weather");         // case-insensitive
IReadOnlyList<ITool> some = tools.GetTools("get_weather", "query_database");
IReadOnlyList<ITool> all  = tools.GetAllTools();
IReadOnlyList<ITool> mine = tools.GetAllTools("MyApp.Tools"); // filter by declaring assembly
```

`GetAllTools(moduleName)` matches the assembly name of the tool's own type, which is how tool packs
such as `FileTools` or `McpIntegration` are selected as a group.

Steps that take tool **names** (`TemplateLlmStep`, `IntentionAnalyzerStep`) resolve them through
`AvailableTools.Instance`, so those tools must be registered before the step is constructed.

## Testing Tools

```csharp
[Fact]
public async Task WeatherTool_ReturnsWeather()
{
    // Arrange
    var tool = new WeatherTool();
    var context = new PipelineContext();
    var logger = NullLogger.Instance;
    
    // Act
    var result = await tool.ExecuteAsync(
        "{\"location\":\"London\"}",
        context,
        "TestStep",
        logger,
        CancellationToken.None);
    
    // Assert
    Assert.Contains("temperature", result);
}
```
