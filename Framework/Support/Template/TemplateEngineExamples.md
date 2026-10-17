# TinyTemplateRendered - Usage Examples

## Overview
`TinyTemplateRendered` (namespace `AITaskAgent.Support.Template`) is the template engine used to interpolate typed step results into LLM prompts with token-efficient formatting. It is registered as the default `ITemplateRendered` by `AddAITaskAgent()` and can also be called statically:

```csharp
// Static call (optional: strictMode, baseDirectory for includes, logger for unresolved placeholders)
string result = TinyTemplateRendered.Render(template, data);

// Through the interface
ITemplateRendered engine = new TinyTemplateRendered();
string result2 = engine.Render(template, data);
```

The model is serialized to JSON with C# property names **lowercased** (`Name` → `name`); dictionary keys keep their literal spelling. Lookups fall back to a case-insensitive match, so `{{Name}}` and `{{name}}` both resolve, but anything the engine emits from the model itself (CSV headers, JSON keys) uses the lowercased names.

## Basic Syntax

### Simple Property Access
```csharp
var data = new { Name = "Juan", Age = 30 };
var result = TinyTemplateRendered.Render("Hello {{Name}}, you are {{Age}} years old", data);
// Output: "Hello Juan, you are 30 years old"
```

### Nested Properties (JPath)
```csharp
var data = new 
{ 
    User = new { Name = "Juan", Address = new { City = "Madrid" } }
};
var result = TinyTemplateRendered.Render("{{User.Name}} lives in {{User.Address.City}}", data);
// Output: "Juan lives in Madrid"
```

### Array Indexing
```csharp
var data = new { Items = new[] { "Apple", "Banana", "Cherry" } };
var result = TinyTemplateRendered.Render("First: {{Items[0]}}, Last: {{Items[2]}}", data);
// Output: "First: Apple, Last: Cherry"
```

### Dictionary Keys
```csharp
var data = new { Data = new Dictionary<string, string> { ["OriginalKey"] = "value" } };
var result = TinyTemplateRendered.Render("{{ Data['OriginalKey'] }}", data);
// Output: "value"
```

### Default Values
```csharp
var data = new { Name = "Juan" };
var result = TinyTemplateRendered.Render("{{Name}} - {{MissingProp ?? N/A}}", data);
// Output: "Juan - N/A"
```

A placeholder that resolves to nothing and has no `??` default renders as an empty string (with a warning when a logger is passed).

---

## Pipe Filters for Token Optimization

Filters use the syntax `{{ value | filter }}` or `{{ value | filter "arg" }}`.

### `| csv` - Array to CSV (Token-Efficient)
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

var prompt = TinyTemplateRendered.Render(@"
Analyze the following users:
{{ Users | csv }}
", dbResults);

/* Output:
Analyze the following users:
name,age,city
John,30,NYC
Jane,25,LA
Bob,35,Chicago
*/
```

**Token savings**: CSV format uses ~40% fewer tokens than JSON for tabular data.

### `| json` - Compact JSON
**Use case**: Structured data that needs to preserve hierarchy

```csharp
var data = new 
{
    Config = new { MaxRetries = 3, Timeout = 30, Endpoints = new[] { "api1", "api2" } }
};

var prompt = TinyTemplateRendered.Render("Use this config: {{ Config | json }}", data);
// Output: Use this config: {"maxretries":3,"timeout":30,"endpoints":["api1","api2"]}
```

### `| json "indent"` - Pretty JSON
**Use case**: Complex structures where readability matters for LLM

```csharp
var prompt = TinyTemplateRendered.Render("Review this config:\n{{ Config | json \"indent\" }}", data);
/* Output:
Review this config:
{
  "maxretries": 3,
  "timeout": 30,
  "endpoints": [
    "api1",
    "api2"
  ]
}
*/
```

### Other filters
`yaml`, `upcase`, `downcase`, `capitalize`, `strip`, `size` (items of an array/object or length of a string) and `default "value"`. An unknown filter leaves the raw value.

---

## Date Formatting

### `| date "format"`
```csharp
var data = new { Timestamp = DateTime.Now };
var result = TinyTemplateRendered.Render("Date: {{ Timestamp | date \"yyyy-MM-dd HH:mm\" }}", data);
// Output: "Date: 2026-01-06 12:00"
```

There is no number-formatting filter (`F2`, `N0`...): format numbers in the model before rendering.

---

## Includes and Comments

- `{{ include 'path/file.md' }}` and the legacy `@[path/file.md]` insert another file, rendered with the same model. Relative paths resolve against `baseDirectory` (or the working directory when it is null); with `baseDirectory` set, a path outside it is rejected.
- `{{# comment #}}` is removed from the output.

---

## Real-World Example: LLM Pipeline

Inside `TemplateLlmStep` / `StatelessTemplateLlmStep` the model is `{ Input, Context }`: `Input` is the step input's `Value` and `Context` is the pipeline metadata merged with the step variables (`SetVariable`).

### Before (Manual String Concatenation)
```csharp
var results = parallelResult.Value; // IReadOnlyDictionary<string, IStepResult>
var draft1 = (results.GetValueOrDefault("Writer1") as LlmStringStepResult)?.Value ?? "No draft 1";
var draft2 = (results.GetValueOrDefault("Writer2") as LlmStringStepResult)?.Value ?? "No draft 2";

var prompt = $@"Analyze the following two drafts:

Draft 1:
{draft1}

Draft 2:
{draft2}";
```

### After (Template-Based)
```csharp
var criticStep = new StatelessTemplateLlmStep<ParallelResult, CriticResult>(
    name: "Critic",
    role: criticRole,
    promtTemplateString: @"Analyze the following two drafts:

Draft 1:
{{Input.Writer1.Value ?? No draft 1}}

Draft 2:
{{Input.Writer2.Value ?? No draft 2}}");
```

Without a template provider the prompt string is rendered directly; with one, it is taken as a template name (see `TemplateProviderGuide.md`).

### With Database Results (CSV for Token Efficiency)
```csharp
var analysisStep = new StatelessTemplateLlmStep<DbQueryResult, AnalysisResult>(
    name: "DataAnalyzer",
    role: fastRole,
    promtTemplateString: @"Analyze the following customer data and identify trends:

{{ Input.Customers | csv }}

Focus on age distribution and geographic patterns.");
```

---

## Performance Characteristics

| Formatter | Use Case | Token Efficiency | Readability |
|-----------|----------|------------------|-------------|
| `{{Prop}}` | Simple values | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| `{{ Array \| csv }}` | Tabular data | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| `{{ Obj \| json }}` | Structured data | ⭐⭐⭐ | ⭐⭐⭐ |
| `{{ Obj \| json "indent" }}` | Complex structures | ⭐⭐ | ⭐⭐⭐⭐⭐ |

---

## Best Practices

1. **Use CSV for tabular data** - Saves ~40% tokens vs JSON
2. **Use compact JSON** for nested structures that need hierarchy
3. **Use indented JSON** only when LLM needs to understand complex structure
4. **Provide defaults** for optional fields: `{{Field ?? N/A}}`
5. **Format dates consistently**: `{{ Date | date "yyyy-MM-dd" }}` for international format
