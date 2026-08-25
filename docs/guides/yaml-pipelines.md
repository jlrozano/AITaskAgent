# Declarative pipelines in YAML

A pipeline can be written in YAML instead of C#. The engine reads the files, compiles the JSON
Schemas into **real C# types** with Roslyn at startup, wires the steps into a graph, and validates
the whole thing before anything runs.

The compiled-types part is the design decision that matters. The obvious way to build a dynamic
pipeline is to pass `JObject` around and inspect it at runtime; this engine deliberately does not,
so the execution machinery, the telemetry and the observability tooling see the same strongly typed
results whether a step was written in C# or declared in YAML.

## A pipeline file

```yaml
name: DocumentProcessingPipeline
steps:
  - !LlmStep
    stepId: step_1_classify
    inputSchema: DocumentInput
    outputSchema: ClassificationOutput
    profileName: default
    useConversationHistory: false
    systemPrompt: "@classifier"
    userPrompt: |
      Document content: {{inputData.content}}
      Classify the document and return the original content in the `content` field.
    dependsOn: []

  - !LlmStep
    stepId: step_2_extract
    inputSchema: ClassificationOutput
    outputSchema: ExtractionOutput
    profileName: default
    systemPrompt: "@extractor"
    userPrompt: |
      Document type: {{inputData.type}}
      Extract the relevant key fields.
    dependsOn: [step_1_classify]
```

`!LlmStep` is a YAML **tag**, not a `type:` property. Tags are registered by reflection over types
carrying `[YamlStepTag]` and implementing `IYamlStep`, so adding a new step kind means writing the
class and registering it in DI — no change to the parser.

| Key | Meaning |
|---|---|
| `stepId` | Unique name; other steps address it in `dependsOn` |
| `inputSchema` / `outputSchema` | Names of JSON Schema files, compiled to real types |
| `profileName` | Which LLM role to run with; omit to use the environment's `DefaultRole` |
| `systemPrompt` / `userPrompt` | Inline text, or `@templateName` to load from the template provider |
| `useConversationHistory` | `false` starts the step with a clean conversation |
| `dependsOn` | Graph edges |

Both `inputSchema` and `outputSchema` are required. A step missing either is rejected at build time.

## Schemas

One JSON Schema file per type, with `title` matching the name you reference:

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "title": "ClassificationOutput",
  "properties": {
    "content":    { "type": "string" },
    "type":       { "type": "string" },
    "confidence": { "type": "number" }
  },
  "required": ["content", "type", "confidence"]
}
```

`SchemaCompiler` generates a POCO named `{Title}Data` and a wrapper
`{Title} : LlmStepResult<{Title}Data>`, compiles them in memory with Roslyn and loads the assembly.
That wrapper is what flows through the pipeline as an ordinary `IStepResult`.

### JavaScript validation

A schema can carry a `validate(self, context)` function, executed with ClearScript V8 after the LLM
responds. It receives the whole `PipelineContext`, so a field can be validated against what an
earlier step produced — the kind of cross-step rule that cannot be expressed in JSON Schema alone.
A failed validation is fed back to the model as a retry, so it can correct itself.

## Building and running

```csharp
var factory = provider.GetRequiredService<YamlPipelineFactory>();
var yamlPipeline = await factory.CreateFromFolderAsync("pipelines");

var result = await yamlPipeline.ExecuteAsync(input);
```

All `*.yaml` files under the folder tree are merged, so a large pipeline can be split across files
and subfolders.

## Failing before it runs

The factory refuses to build a pipeline it knows is wrong, rather than discovering it three LLM
calls in:

- **Unknown dependency** — a `dependsOn` naming a step that does not exist.
- **Cycles** — detected by topological sort.
- **Schema mismatch** — a step's `inputSchema` must equal the `outputSchema` of the step it depends
  on. Two steps that disagree about the shape of their data never start.
- **Unknown role** — `profileName` must name a role in the configured `LlmEnvironment`; the error
  lists the roles that do exist.
- **Missing schema** — a step without `inputSchema` or `outputSchema`.

## No secrets in YAML

A step names a role, never a key or an endpoint. Credentials live in configuration and are resolved
through `LlmEnvironment`, so a pipeline file is safe to commit and to share between environments.
See [Configuration](configuration.md).

## Prompt templates

`@name` loads a template through the `ITemplateProvider`; anything else is treated as inline text.
Either way the text is rendered against the step input and the pipeline context, so
`{{inputData.content}}` and `{{ context.somekey }}` resolve the same as in a C# step — the template
surface is shared between both paths.

## Current limits

- Generated assemblies are recompiled on every start; there is no on-disk cache keyed by schema
  hash.
- Each JavaScript validation creates its own V8 engine, with no pooling.
- `YamlPipeline.ExecuteAsync` accepts a `CancellationToken` but does not pass it on.

## See also

- [Configuration](configuration.md) — providers, roles and fallbacks
- [LLM Steps](llm-steps.md) — the code-first equivalent
- `Samples/YamlPipelineDemo` — a runnable three-step pipeline
