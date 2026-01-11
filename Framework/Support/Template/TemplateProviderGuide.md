# Template Provider System - Usage Guide

## Overview
The template provider system allows you to externalize LLM prompts into markdown files, making them easier to maintain, version, and test.

## Components

### ITemplateProvider
Interface for loading templates by name.

### FileTemplateProvider
File-based implementation with intelligent caching:
- **LRU (Least Recently Used)**: Evicts oldest accessed items
- **TTL (Time To Live)**: Items expire after 5 minutes (default)
- **Size-based**: Cache limited to 1MB (default) with 10% margin
- **File format**: `.md` (markdown)

### Template-based LLM Steps

#### TemplateLlmStep<TIn, TOut> (Stateful)
Uses `context.Conversation` for stateful conversation history.

```csharp
var templateEngine = new JsonTemplateEngine();
var templateProvider = new FileTemplateProvider(
    folderPath: "templates",
    enableCache: true,
    ttl: TimeSpan.FromMinutes(5),
    maxCacheSizeBytes: 1_048_576 // 1MB
);

var criticStep = new TemplateLlmStep<ParallelResult, CriticResult>(
    llmService,
    templateEngine,
    templateProvider,
    name: "Critic",
    profile: nVideaProfile,
    promptTemplateName: "critic_prompt",
    systemPromptTemplateName: "critic_system"
);
```

#### StatelessTemplateLlmStep<TIn, TOut> (Stateless)
Creates clean conversation context for each execution.

```csharp
var writerStep = new StatelessTemplateLlmStep<UserIntentionResponse, LlmStringStepResult>(
    llmService,
    templateEngine,
    templateProvider,
    name: "Writer",
    profile: nVideaProfile,
    promptTemplateName: "writer_prompt",
    systemPromptTemplateName: "writer_system"
);
```

---

## Template Syntax

Templates use the `JsonTemplateEngine` syntax:

### Basic Interpolation
```markdown
Hello {{User.Name}}, your score is {{Score}}.
```

### Nested Properties
```markdown
{{User.Address.City}}
```

### Arrays
```markdown
First item: {{Items[0].Name}}
```

### Default Values
```markdown
{{MissingProp ?? N/A}}
```

### Custom Formatters

#### CSV (Token-Efficient)
```markdown
Here's the data:
{{DatabaseResults:csv}}
```

#### JSON
```markdown
Config: {{Settings:json}}
```

#### JSON Indented
```markdown
```json
{{Settings:json:indent}}
\```
```

#### Date/Number Formatting
```markdown
Date: {{Timestamp:yyyy-MM-dd}}
Score: {{Value:F2}}%
```

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
- Cache evicts LRU items when size exceeds 1.1MB
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
    },
    systemPrompt: "You are a harsh literary critic..."
);
```

### After (Template-Based)
```csharp
var criticStep = new StatelessTemplateLlmStep<ParallelResult, CriticResult>(
    llmService,
    templateEngine,
    templateProvider,
    name: "Critic",
    profile: nVideaProfile,
    promptTemplateName: "critic_prompt",
    systemPromptTemplateName: "critic_system"
);
```

**critic_prompt.md**:
```markdown
Analyze the following two drafts of the same story.

## Draft 1
{{Writer1.Content ?? No draft 1}}

## Draft 2
{{Writer2.Content ?? No draft 2}}
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
5. **Token Optimization**: Use `:csv` formatter for tabular data
6. **Maintainability**: Centralized prompt management
