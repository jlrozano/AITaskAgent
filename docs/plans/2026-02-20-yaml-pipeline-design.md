# YAML-Driven Dynamic Pipelines Design

## Overview
This document outlines the architectural design for introducing YAML-based pipeline orchestration to the `AITaskFramework`. The goal is to allow users to define step sequences (Pipelines) and their strongly-typed data contracts (JSON Schemas) dynamically without writing C# code, while maintaining full compatibility with the framework's native execution engine (`Pipeline.cs`), observability features, and dynamic type safety.

## Architecture: Hybrid Factory Pattern

The design employs a **Hybrid Factory Pattern** (inspired by `BRMS.Abstractions`), which separates the compilation of data contracts from the runtime orchestration of the pipeline.

### Phase 1: Dynamic Data Compilation (AOT-like)
1. **Input/Output Definitions:** Users define their data contracts using JSON Schemas (e.g., `ClientRequest.json`, `LlmResponse.json`).
2. **Code Generation:** A `SchemaCompiler` component reads these schemas at application startup or via a builder command.
3. **In-Memory Assembly:** Using `NJsonSchema` and `Microsoft.CodeAnalysis` (Roslyn), the framework generates pure C# DTO classes (e.g., `Dynamic_ClientRequest`) and compiles them into a dynamic assembly (e.g., `AITaskFramework.DynamicTypes.dll`). 
4. **Result:** Strongly-typed classes exist at runtime, allowing the native pipeline engine to pass `IStepResult` objects securely between steps.

### Phase 2: YAML Orchestration & Reflection Factory
1. **Pipeline Definition:** Users define the execution flow in `pipeline.yaml` using aliases for existing "Factory-Friendly" step types (e.g., `type: "LlmStep"`).
2. **Type Resolution:** A `YamlPipelineFactory` reads the YAML. It resolves the generic base type (e.g., `typeof(YamlLlmStep<,>)`) and uses reflection (`MakeGenericType`) to create a closed type using the dynamically generated schema classes (e.g., `typeof(YamlLlmStep<Dynamic_ClientRequest, Dynamic_LlmResponse>)`).
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

The YAML structure mirrors the tree-like execution model of `Pipeline.cs`, specifically the `NextSteps` paradigm.

### Implicit Data Flow (Parent to Child)
Data flows naturally down the execution tree. The framework enforces that if Step B is in the `nextSteps` of Step A, Step B's `inputSchema` must be assignable from Step A's `outputSchema`. The native engine passes the `IStepResult` directly.

### Explicit Context Access (Cross-Branch)
When a step needs data from a step that is not its direct parent (or needs data from the initial pipeline input), it leverages the native `PipelineContext.StepResults`.
* **Templating Power:** Within the markdown prompt templates (e.g., `user_resumidor.md`), the `JsonTemplateEngine` can evaluate expressions against the shared context.
* **Syntax Example:** `{{ Context.StepResults['PasoExtraccion'].Input.TextoOriginal }}` allows any step to pull historical data safely.

## Extensibility (NuGet Support)
Third-party NuGet packages can expose new Factory-Friendly step types (e.g., `YamlSearchStep<,>`). They register their open generic types with the `YamlPipelineFactory` during DI setup (e.g., `services.AddCustomYamlSteps()`), making the new `type` alias immediately available in the YAML schema.
