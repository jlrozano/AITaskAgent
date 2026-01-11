# JsonTemplateEngine - Usage Examples

## Overview
`JsonTemplateEngine` is optimized for interpolating typed step results into LLM prompts with token-efficient formatting.

## Basic Syntax

### Simple Property Access
```csharp
var data = new { Name = "Juan", Age = 30 };
var result = engine.Render("Hello {{Name}}, you are {{Age}} years old", data);
// Output: "Hello Juan, you are 30 years old"
```

### Nested Properties (JPath)
```csharp
var data = new 
{ 
    User = new { Name = "Juan", Address = new { City = "Madrid" } }
};
var result = engine.Render("{{User.Name}} lives in {{User.Address.City}}", data);
// Output: "Juan lives in Madrid"
```

### Array Indexing
```csharp
var data = new { Items = new[] { "Apple", "Banana", "Cherry" } };
var result = engine.Render("First: {{Items[0]}}, Last: {{Items[2]}}", data);
// Output: "First: Apple, Last: Cherry"
```

### Default Values
```csharp
var data = new { Name = "Juan" };
var result = engine.Render("{{Name}} - {{MissingProp ?? N/A}}", data);
// Output: "Juan - N/A"
```

---

## Custom Formatters for Token Optimization

### `:csv` - Array to CSV (Token-Efficient)
**Use case**: Database results, tabular data

```csharp
var dbResults = new 
{
    Users = new[]
    {
        new { Name = "John", Age = 30, City = "NYC" },
        new { Name = "Jane", Age = 25, City = "LA" },
        new { Name = "Bob", Age = 35, City = "Chicago" }
    }
};

var prompt = engine.Render(@"
Analyze the following users:
{{Users:csv}}
", dbResults);

/* Output:
Analyze the following users:
Name,Age,City
John,30,NYC
Jane,25,LA
Bob,35,Chicago
*/
```

**Token savings**: CSV format uses ~40% fewer tokens than JSON for tabular data.

### `:json` - Compact JSON
**Use case**: Structured data that needs to preserve hierarchy

```csharp
var data = new 
{
    Config = new { MaxRetries = 3, Timeout = 30, Endpoints = new[] { "api1", "api2" } }
};

var prompt = engine.Render("Use this config: {{Config:json}}", data);
// Output: Use this config: {"MaxRetries":3,"Timeout":30,"Endpoints":["api1","api2"]}
```

### `:json:indent` - Pretty JSON
**Use case**: Complex structures where readability matters for LLM

```csharp
var prompt = engine.Render("Review this config:\n{{Config:json:indent}}", data);
/* Output:
Review this config:
{
  "MaxRetries": 3,
  "Timeout": 30,
  "Endpoints": [
    "api1",
    "api2"
  ]
}
*/
```

---

## Standard .NET Formatting

### Date Formatting
```csharp
var data = new { Timestamp = DateTime.Now };
var result = engine.Render("Date: {{Timestamp:yyyy-MM-dd HH:mm}}", data);
// Output: "Date: 2026-01-06 12:00"
```

### Number Formatting
```csharp
var data = new { Score = 95.567, Count = 1234567 };
var result = engine.Render("Score: {{Score:F2}}, Count: {{Count:N0}}", data);
// Output: "Score: 95.57, Count: 1,234,567"
```

---

## Real-World Example: LLM Pipeline

### Before (Manual String Concatenation)
```csharp
var criticStep = new StatelessLlmStep<StepResult, CriticResult>(
    llmService,
    name: "Critic",
    profile: nVideaProfile,
    promptBuilder: (input, context) =>
    {
        var results = (context.StepResults["Router/NewStoryPhase/ParallelWriters"] as ParallelResult)?.Value;
        var draft1 = (results?["Writer1"] as LlmStringStepResult)?.Content ?? "No draft 1";
        var draft2 = (results?["Writer2"] as LlmStringStepResult)?.Content ?? "No draft 2";

        return Task.FromResult($@"Analyze the following two drafts:

Draft 1:
{draft1}

Draft 2:
{draft2}");
    }
);
```

### After (Template-Based)
```csharp
var templateEngine = new JsonTemplateEngine();

var criticStep = new StatelessLlmStep<ParallelResult, CriticResult>(
    llmService,
    name: "Critic",
    profile: nVideaProfile,
    promptBuilder: (input, context) =>
    {
        var template = @"Analyze the following two drafts:

Draft 1:
{{Writer1.Content ?? No draft 1}}

Draft 2:
{{Writer2.Content ?? No draft 2}}";

        return Task.FromResult(templateEngine.Render(template, input.Value));
    }
);
```

### With Database Results (CSV for Token Efficiency)
```csharp
var analysisStep = new StatelessLlmStep<DbQueryResult, AnalysisResult>(
    llmService,
    name: "DataAnalyzer",
    profile: fastProfile,
    promptBuilder: (input, context) =>
    {
        var template = @"Analyze the following customer data and identify trends:

{{Customers:csv}}

Focus on age distribution and geographic patterns.";

        return Task.FromResult(templateEngine.Render(template, input.Value));
    }
);
```

---

## Performance Characteristics

| Formatter | Use Case | Token Efficiency | Readability |
|-----------|----------|------------------|-------------|
| `{{Prop}}` | Simple values | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| `{{Array:csv}}` | Tabular data | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| `{{Obj:json}}` | Structured data | ⭐⭐⭐ | ⭐⭐⭐ |
| `{{Obj:json:indent}}` | Complex structures | ⭐⭐ | ⭐⭐⭐⭐⭐ |

---

## Best Practices

1. **Use CSV for tabular data** - Saves ~40% tokens vs JSON
2. **Use compact JSON** for nested structures that need hierarchy
3. **Use indented JSON** only when LLM needs to understand complex structure
4. **Provide defaults** for optional fields: `{{Field ?? N/A}}`
5. **Format dates consistently**: `{{Date:yyyy-MM-dd}}` for international format
