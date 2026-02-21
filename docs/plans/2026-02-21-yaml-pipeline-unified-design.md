# YAML-Driven Dynamic Pipelines Design (Unified)

## Overview
This document outlines the consolidated architectural design for the YAML-based pipeline orchestration engine in `AITaskFramework`. 
The objective is to allow dynamic definition of both the pipeline execution flow (YAML) and the strongly-typed data contracts (JSON Schema) without requiring manual C# boilerplates, while maintaining the robustness of the native C# core engine.

## Core Principles & Agreements

### 1. No JObjects, Strict C# Types
The execution engine requires strongly-typed objects implementing `IStepResult`. We will not pass generic `JObject` or `dynamic` instances between steps.

### 2. Runtime Schema Compilation (AOT-like)
*   **Definition:** Data contracts are defined strictly via standard JSON Schemas (`.json`).
*   **Compilation:** A `SchemaCompiler` component reads these schemas at application startup.
*   **Generation:** Using `NJsonSchema` and `Microsoft.CodeAnalysis.CSharp` (Roslyn), the framework generates pure C# DTO classes directly into a dynamic in-memory assembly. These generated classes natively implement `IStepResult`.
*   **Caching:** To reduce startup overhead, a disk-cache mechanism can optionally store the compiled `.dll` if the source schemas haven't changed.

### 3. JavaScript Validation (V8 ClearScript)
Dynamic data payloads require validation that cannot be hardcoded in C#.
*   The generated C# classes implement `Task<(bool IsValid, string? Error)> ValidateAsync()`.
*   This method leverages the native **V8 JavaScript engine** via ClearScript (reusing patterns from `BRMS.StdRules.Modules.Scripting`).
*   Users can attach JavaScript validation logic directly to their schemas, allowing Turing-complete validation rules evaluated dynamically during the pipeline run.

---

## The YAML Orchestrator

### 1. Modular Execution (DAG Style)
*   **Structure:** The `pipeline.yaml` is written in a flat, Directed Acyclic Graph (DAG) style. Steps are declared in a root array and linked using `dependsOn` or `stepId` references.
*   **Modularity:** Pipelines can be split across multiple YAML files organized in subfolders. The framework merges these "step groups" during initialization, allowing large pipelines to be composed of smaller, reusable fragments.
*   **Validation:** Before execution, the orchestrator performs a semantic pass to detect circular dependencies (infinite loops) and validate that `OutputSchema` matches the next step's `InputSchema`.

### 2. Native Deserialization (`YamlDotNet` & Tags)
The framework natively deserializes the YAML directly into C# classes without creating custom "Proxy" deserializers.
*   **Reflection Discovery:** The `YamlPipelineFactory` scans the AppDomain for classes implementing the `IYamlStep` interface and decorated with `[YamlStepTag("TagName")]`.
*   **Tag Mapping:** It automatically calls `DeserializerBuilder.WithTagMapping("!TagName", typeof(TheClass))` for each discovered step type.
*   **YAML Syntax:** In the YAML file, users specify the type natively using tags, for example: `!LlmStep`.

### 3. `IYamlStep` Interface
To ensure the deserializer can map the DAG, all YAML-exposed steps must implement `IYamlStep`, which strictly requires:
```csharp
public interface IYamlStep
{
    string StepId { get; init; } // Replaces generic names, acts as the global DAG node ID
    string InputSchema { get; init; } // The name of the JSON Schema to use as TIn
    string OutputSchema { get; init; } // The name of the JSON Schema to use as TOut
}
```

---

## Core Refactoring: Non-Generic `BaseLlmStep`

To support both the Code-First (C#) and YAML-First approaches cleanly, the native `BaseLlmStep` will be refactored to eliminate strict generic parameters at the base level.

### 1. Types as constructor parameters
Instead of `BaseLlmStep<TIn, TOut>`, the class becomes `BaseLlmStep` and receives `Type inputType` and `Type outputType` via its constructor.
*   It operates entirely on `IStepResult` internally (handling retries, bookmarks, LLM tool logic, and validation).

### 2. Dual Inheritance Approach
*   **`TypedLlmStep<TIn, TOut> : BaseLlmStep`**: For C# developers. It receives strongly-typed delegates (`Func<TIn, string>`) and passes `typeof(TIn)` and `typeof(TOut)` to the base class.
*   **`YamlLlmStep : BaseLlmStep, IYamlStep`**: For YAML integration. It exposes public string properties (`SystemPrompt`, `UserPrompt`, `InputSchema`, `OutputSchema`) compatible with `YamlDotNet`. 
    *   Once deserialized, the factory reads the string properties (like `InputSchema`), resolves the compiled dynamic C# `Type` from the `SchemaCompiler`, and injects both the Types and the DI services (`ILogger`, `ILlmService`) into the `BaseLlmStep` constructor.

### 3. Template Resolution (`@` Prefix)
All string properties in steps (like LLM Prompts) support a dynamic fallback convention:
*   **Raw Text:** `prompt: "Resume esto..."` is processed literally.
*   **Template Reference:** `prompt: "@prompts/extractor.md"` is intercepted. The framework uses the configured `ITemplateProvider` to load the file and render `{{ variables }}` based on the current `PipelineContext`.

### 4. Security & Environment Configuration
Environment-specific variables (like DB connections, API Keys, Base URLs) are **not** handled via YAML interpolation.
*   The YAML strictly references a descriptive "Profile" name (`provider: "OpenAI_Prod"`).
*   The framework resolves all secure credentials natively using the `ILlmProviderResolver.GetProvider(profileName)`, which expands environment variables securely outside of the YAML scope.
