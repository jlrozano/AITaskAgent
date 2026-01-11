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
    
    protected override async Task<string> InternalExecuteAsync(
        string argumentsJson,
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
            AdditionalData = new Dictionary<string, object?>
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
    
    public ToolDefinition GetDefinition() => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new
        {
            type = "object",
            properties = new
            {
                location = new 
                { 
                    type = "string", 
                    description = "City name, e.g., 'London'" 
                },
                units = new 
                { 
                    type = "string", 
                    @enum = new[] { "celsius", "fahrenheit" },
                    description = "Temperature units"
                }
            },
            required = new[] { "location" }
        }
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
    
    public ToolDefinition GetDefinition() => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string", description = "SQL SELECT query" }
            },
            required = new[] { "query" }
        }
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

```csharp
var tools = new List<ITool>
{
    new WeatherTool(),
    new DatabaseTool(db, logger)
};

var step = new LlmStep<In, Out>(..., tools: tools);
```

### Via Registry

```csharp
// Registration
services.AddSingleton<IToolRegistry, ToolRegistry>();

var registry = services.GetRequiredService<IToolRegistry>();
registry.Register(new WeatherTool());
registry.Register(services.GetRequiredService<DatabaseTool>());

// Usage
var tools = registry.GetAll().ToList();
```

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
