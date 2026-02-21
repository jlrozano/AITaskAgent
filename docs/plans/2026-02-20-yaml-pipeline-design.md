# YAML-Driven Dynamic Pipelines Design

## Overview
This document outlines the architectural design for introducing YAML-based pipeline orchestration to the `AITaskFramework`. The goal is to allow users to define step sequences (Pipelines) and their strongly-typed data contracts (JSON Schemas) dynamically without writing C# code, while maintaining full compatibility with the framework's native execution engine (`Pipeline.cs`), observability features, and dynamic type safety.

## Architecture: Hybrid Factory Pattern

The design employs a **Hybrid Factory Pattern** (inspired by `BRMS.Abstractions`), which separates the compilation of data contracts from the runtime orchestration of the pipeline.

### Phase 1: Dynamic Data Compilation (Runtime/AOT-like)
1. **Input/Output Definitions:** Users define their data contracts using JSON Schemas (e.g., `ClientRequest.json`, `LlmResponse.json`).
2. **Code Generation:** A `SchemaCompiler` component reads these schemas strictly at runtime during application startup (or builder configuration).
3. **In-Memory Assembly:** Using `NJsonSchema` and `Microsoft.CodeAnalysis` (Roslyn), the framework generates pure C# DTO classes (e.g., `Dynamic_ClientRequest`) and compiles them directly into a dynamic assembly in memory (e.g., `AITaskFramework.DynamicTypes.dll`). This avoids the need for external build steps and provides maximum agility. A disk-caching mechanism may be optionally introduced to skip recompilation of unchanged schemas on subsequent runs.
4. **Result:** Strongly-typed classes exist at runtime, allowing the native pipeline engine to pass `IStepResult` objects securely between steps.

### Phase 2: YAML Orchestration (DAG Style) & Reflection Factory
1. **Pipeline Definition:** Users define the execution flow in `pipeline.yaml` using a flat Graph/DAG (Directed Acyclic Graph) structure. Steps are grouped at the root level and utilize properties like `dependsOn` or `inputFrom` to define execution order and dependencies, avoiding deep nesting and accommodating complex convergences.
2. **Type Resolution:** A `YamlPipelineFactory` reads the YAML. It translates the aliases (e.g., `type: "LlmStep"`) into generic base types (e.g., `typeof(YamlLlmStep<,>)`) and uses reflection (`MakeGenericType`) to create a closed type using the dynamically generated schema classes.
3. **Instantiation:** The factory uses `ActivatorUtilities` (Dependency Injection) to instantiate the closed type, automatically resolving core services (Logger, LLM Service, Template Provider).
4. **Property Binding:** The factory uses `YamlDotNet` (or JSON deserialization) to map the `properties` block from the YAML directly onto the public properties of the newly instantiated step.

## Factory-Friendly Base Classes

To bridge the gap between declarative YAML and the functional delegates required by the native framework (e.g., `BaseLlmStep`), new "Factory-Friendly" base classes will be introduced.

### `YamlLlmStep<TIn, TOut>`
This class inherits from the native `BaseLlmStep<TIn, TOut>` but replaces hardcoded delegates with DI-injected services and string-based properties.

* **Properties:** Exposes simple properties like `SystemPromptTemplateName` and `UserPromptTemplateName` which the YAML can configure.
* **Templating:** Injects `ITemplateProvider` via its constructor.
* **Delegate Generation:** Internally, it fulfills the `messageBuilder` delegate requirement of its parent by calling `templateProvider.Render(TemplateName, new { Model = input, Context = pipelineContext })`. This dynamically fuses the generated `TIn` model with markdown templates at runtime.

## Data Routing and Context Access

The YAML structure utilizes a DAG-style model, which the factory will translate back into the tree-like execution model of `Pipeline.cs` (`NextSteps`) under the hood, or evaluate continuously.

### DAG Data Flow (Step Dependencies)
Data flows based on explicit dependency declarations (e.g., `dependsOn`). The framework enforces that if Step B depends on Step A, Step B's `inputSchema` must be assignable from Step A's `outputSchema`. The native engine orchestrates the passing of `IStepResult` accordingly.

### Explicit Context Access (Cross-Branch)
When a step needs data from a step that is not direct, it leverages the native `PipelineContext.StepResults`.
* **Templating Power:** Within the markdown prompt templates (e.g., `user_resumidor.md`), the `JsonTemplateEngine` can evaluate expressions against the shared context.
* **Syntax Example:** `{{ Context.StepResults['PasoExtraccion'].Input.TextoOriginal }}` allows any step to pull historical data safely.

## Extensibility (NuGet Support)
Third-party NuGet packages can expose new Factory-Friendly step types (e.g., `YamlSearchStep<,>`). They register their open generic types with the `YamlPipelineFactory` during DI setup (e.g., `services.AddCustomYamlSteps()`), making the new `type` alias immediately available in the YAML schema.
