# YAML-Driven Dynamic Pipelines Design

## Overview
This document outlines the architectural design for introducing YAML-based pipeline orchestration to the `AITaskFramework`. The goal is to allow users to define step sequences (Pipelines) and their strongly-typed data contracts (JSON Schemas) dynamically without writing C# code, while maintaining full compatibility with the framework's native execution engine (`Pipeline.cs`), observability features, and dynamic type safety.

## Architecture: Hybrid Factory Pattern

The design employs a **Hybrid Factory Pattern** (inspired by `BRMS.Abstractions`), which separates the compilation of data contracts from the runtime orchestration of the pipeline.

### Phase 1: Dynamic Data Compilation (Runtime/AOT-like)
1. **Input/Output Definitions:** Users define their data contracts using JSON Schemas (e.g., `ClientRequest.json`, `LlmResponse.json`).
2. **Code Generation:** A `SchemaCompiler` component reads these schemas strictly at runtime during application startup (or builder configuration).
3. **Strict C# Compilation:** Using `NJsonSchema` and `Microsoft.CodeAnalysis` (Roslyn), the framework generates pure, strongly-typed C# DTO classes (e.g., `Dynamic_ClientRequest`) directly into memory. It strictly avoids `JObject` or dynamic properties. These generated types natively implement `IStepResult`.
4. **JavaScript-Powered Validation:** The generated types implement the `Task<(bool IsValid, string? Error)> ValidateAsync()` method from `IStepResult` by invoking the V8 JavaScript engine (via ClearScript, leveraging patterns from `BRMS.StdRules.Modules.Scripting`). This allows YAML/JSON developers to inject powerful, Turing-complete validation scripts directly alongside their schemas without writing compiled C#.

### Phase 2: YAML Orchestration (DAG Style) & Reflection Factory
1. **Modular Pipeline Definition & Step Groups:** Users define the execution flow using a flat Graph/DAG structure. Pipelines can be split across multiple YAML files (e.g., organized in subfolders by feature) containing groups of steps. The orchestrator merges these fragments during initialization, allowing files to reference remote steps and entire groups.
2. **Type Resolution via YAML Tags (`!Type`):** `YamlDotNet` deserialization is driven by native YAML tags (e.g., `!LlmStep`, `!CodeStep`). The framework dynamically discovers all types implementing `IYamlStep`. Crucially, `IYamlStep` mandates explicit properties (like `InputSchema` and `OutputSchema`) which the factory reads to map the steps to the strictly generated C# types from Phase 1, avoiding runtime generic metadata resolution parsing strings.
3. **Unique Step Identification:** Every step declared in the YAML must have a unique `stepId` (in place of a generic name tag). This ID serves as the global reference point for routing, dependencies, and context lookups across files.
4. **Property Binding:** The factory deserializes the YAML directly onto the C# class properties. All runtime dependencies (ILogger, ILlmService) are resolved via Dependency Injection, while configuration is set via `required init` properties.

## Core Refactoring: Native YAML Deserialization

To support seamless YAML deserialization directly, the core pipeline step classes (e.g., `BaseLlmStep`) will be heavily refactored from their code-first origins:

### 1. Stateless Constructors & Required Properties
Constructors will strictly enforce Dependency Injection, accepting only required runtime services (e.g., `ILogger`, `ILlmService`, `ITemplateProvider`). All behavioral configuration (prompts, temperature, dynamic model overrides) will be migrated to standard standard C# properties (e.g., using `required init` modifiers where appropriate). This structure aligns perfectly with native deserialization frameworks like `YamlDotNet`.

### 2. Template Resolution Convention (`@` prefix)
To maximize flexibility in the YAML, string-based properties (like `SystemMessage` and `UserMessage`) will support a dynamic resolution convention:
*   **Raw Text:** If the value is standard text (e.g., `prompt: "Resume el siguiente texto..."`), it is injected literally.
*   **Template Reference:** If the value starts with `@` (e.g., `prompt: "@prompts/extractor_user.md"`), the framework's internal execution intercepts it, treating the string as a template path. It calls `ITemplateProvider` to load and render the required file against the runtime `PipelineContext`.

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
