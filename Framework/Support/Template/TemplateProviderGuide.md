# Template Provider System - Usage Guide

## Overview
The template provider system allows you to externalize LLM prompts into markdown files, making them easier to maintain, version, and test.

## Components

### ITemplateProvider
Interface for loading templates by name: `GetTemplate(name)` returns the raw text (or null) and `Render(name, model)` renders it.

### FileTemplateProvider
File-based implementation with intelligent caching:
- **LRU (Least Recently Used)**: Evicts oldest accessed items
- **TTL (Time To Live)**: Items expire after 5 minutes (default)
- **Size-based**: Cache limited to 1MB (default) with 10% margin
- **File format**: `.md` (markdown); the provider appends `.md` to the name, so names are given **without** extension
- **Missing template**: `Render` returns null, or throws `FileNotFoundException` when `validateTemplates: true`
- **Engine**: the optional `templateEngine` (`ITemplateRendered`); when null, `TinyTemplateRendered` is used with the template folder as base directory for includes

### Template-based LLM Steps

#### TemplateLlmStep<TIn, TOut> (Stateful)
Uses `context.Conversation` for stateful conversation history.

```csharp
var templateProvider = new FileTemplateProvider(
    folderPath: "templates",
    templateEngine: new TinyTemplateRendered(), // optional
    enableCache: true,
    ttl: TimeSpan.FromMinutes(5),
    maxCacheSizeBytes: 1_048_576 // 1MB
);

// Constructor: (name, role, templateProvider, tools, promtTemplateString, systemTemplateString)
var criticStep = new TemplateLlmStep<ParallelResult, CriticResult>(
    "Critic",
    criticRole,                 // LlmRole
    templateProvider,
    null,                       // tool names
    "critic_prompt",
    "critic_system"
);
```

With a provider, the prompt and system strings are template **names**. If the prompt name is omitted it defaults to `"{name}_prompt.md"`, and the system one to the role's `SystemPrompt` or `"{name}_system.md"`; since `FileTemplateProvider` appends `.md` itself, pass the names explicitly.

#### StatelessTemplateLlmStep<TIn, TOut> (Stateless)
Creates clean conversation context for each execution.

```csharp
var writerStep = new StatelessTemplateLlmStep<UserIntentionResponse, LlmStringStepResult>(
    "Writer",
    writersRole,
    templateProvider,
    null,
    "writer_prompt",
    "writer_system"
);
```

---

## Template Syntax

Templates use the `TinyTemplateRendered` syntax (see `TemplateEngineExamples.md`). Inside template-based steps the model is `{ Input, Context }`: `Input` is the step input's `Value`, `Context` the pipeline metadata plus step variables. Property names are matched case-insensitively.

### Basic Interpolation
```markdown
Hello {{Input.User.Name}}, your score is {{Input.Score}}. Session: {{Context.sessionid}}
```

### Nested Properties
```markdown
{{Input.User.Address.City}}
```

### Arrays
```markdown
First item: {{Input.Items[0].Name}}
```

### Default Values
```markdown
{{Input.MissingProp ?? N/A}}
```

### Pipe Filters

#### CSV (Token-Efficient)
```markdown
Here's the data:
{{ Input.DatabaseResults | csv }}
```

#### JSON
```markdown
Config: {{ Input.Settings | json }}
```

#### JSON Indented
````markdown
```json
{{ Input.Settings | json "indent" }}
```
````

#### Date Formatting
```markdown
Date: {{ Input.Timestamp | date "yyyy-MM-dd" }}
```

There is no number-formatting filter.

---

## File Organization

```
templates/
├── critic_prompt.md
├── critic_system.md
├── writer_prompt.md
├── writer_system.md
├── outliner_prompt.md
└── rewriter_prompt.md
```

---

## Cache Behavior

### Production (Cache Enabled)
```csharp
var provider = new FileTemplateProvider(
    "templates",
    enableCache: true,
    ttl: TimeSpan.FromMinutes(5),
    maxCacheSizeBytes: 1_048_576
);
```

- Templates loaded once, cached for 5 minutes
- Cache evicts LRU items when size exceeds 1.1MB (until it is back under 1MB)
- Minimal file I/O

### Development (Cache Disabled)
```csharp
var provider = new FileTemplateProvider(
    "templates",
    enableCache: false
);
```

- Templates reloaded on every call
- Changes reflected immediately
- Useful for testing/development

---

## Migration Example

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
var systemPrompt = "You are a harsh literary critic...";
```

### After (Template-Based)
```csharp
var criticStep = new StatelessTemplateLlmStep<ParallelResult, CriticResult>(
    "Critic",
    criticRole,
    templateProvider,
    null,
    "critic_prompt",
    "critic_system"
);
```

**critic_prompt.md**:
```markdown
Analyze the following two drafts of the same story.

## Draft 1
{{Input.Writer1.Value ?? No draft 1}}

## Draft 2
{{Input.Writer2.Value ?? No draft 2}}
```

**critic_system.md**:
```markdown
You are a harsh literary critic. Be constructive but demanding.
```

---

## Benefits

1. **Separation of Concerns**: Prompts separate from code
2. **Version Control**: Track prompt changes in git
3. **A/B Testing**: Easy to swap templates
4. **Collaboration**: Non-developers can edit prompts
5. **Token Optimization**: Use the `| csv` filter for tabular data
6. **Maintainability**: Centralized prompt management
