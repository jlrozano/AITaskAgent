# YAML-Driven Dynamic Pipelines Design (Comprehensive Unified Architecture)

## 1. Executive Summary
This document forms the technical specification and exhaustive design parameters for the YAML Pipeline Engine within `AITaskFramework`. Its primary objective is to empower users to define deeply complex LLM step orchestrations—and their corresponding strongly-typed data payloads—entirely via declarative configuration (YAML and JSON Schema).

This design fundamentally preserves the core execution robustness of `Pipeline.cs`, retaining strict statically-typed semantics, logging boundaries, retries, and validations while introducing a fully dynamic compilation and orchestration facade.

---

## 2. Core Architectural Principles
- **No `JObject` / No `dynamic` bypasses:** The underlying pipeline engine strictly requires all payloads to be C# objects that implement `IStepResult`. This rule enforces type-safety down to the core engine and ensures observability tools can serialize results reliably.
- **AOT-like Dynamic Compilation at Boot:** JSON Schemas are translated to real C# IL (Intermediate Language) assemblies *strictly before* the pipeline runs first.
- **Extensible Configuration via Composition & Inheritance:** YAML objects map to real C# classes (via `YamlDotNet` native tags `@!` and reflection scanning) interacting natively with the DI container.

---

## 3. Phase 1: Dynamic Data Compilation (The Schema Compiler)

To satisfy the "No `JObject`" rule, the configuration engine must generate physical C# types at runtime that mirror the user's JSON Schemas.

### 3.1. Compilation Mechanics
1. **Source Definitions:** Users define contracts using strict JSON Schema standard files (e.g., `ClientFactura.json`).
2. **Generation:** During application startup (or DI builder phase), a new `SchemaCompiler` component discovers these schemas.
3. **Emit to Memory:** Using `NJsonSchema.CodeGeneration.CSharp` for generating the C# syntax trees and `Microsoft.CodeAnalysis` (Roslyn) for compilation, the code is emitted directly to an in-memory assembly (e.g., `AITaskAgent.DynamicTypes.dll`).
4. **Caching Strategy:** To prevent high startup latency on massive volumes, a hash of the JSON Schemas can be checked against a cached `.dll` on disk. If the hash matches, the file is loaded directly via `AssemblyLoadContext` without invoking Roslyn.

### 3.2. JavaScript Validation via V8 (ClearScript)
Because the types are dynamically generated, developers cannot easily write standard `IStepResult.ValidateAsync()` methods in C# for them.

**The Solution:**
*   The generated C# schema classes automatically implement `Task<(bool IsValid, string? Error)> ValidateAsync()`.
*   Inside this method, the C# code invokes the **V8 JavaScript engine** via ClearScript (re-using patterns established in `BRMS.StdRules.Modules.Scripting`).
*   **The Validation Context:** The JavaScript function does *not* just receive the current object. It receives the **entire `PipelineContext`**. This is critical because validation often depends on cross-step data (e.g., "Field X is only valid if Step 2 returned Y").
*   The developer writes this JS directly into the JSON Schema configuration (or an accompanying JS file) and the compiler injects the script text into the generated C#.

---

## 4. Phase 2: YAML Orchestration Engine

### 4.1. DAG Modularity and Step Groups
*   **Structure:** `pipeline.yaml` describes the flow as a Directed Acyclic Graph (DAG) instead of deep nesting.
*   **Unique Step IDs:** Every single step declared has a robust `stepId`. This guarantees uniquely addressable nodes for dependencies (`dependsOn` arrays), and context lookups.
*   **Inclusion/Fragments:** A pipeline can be split across multiple YAML files, allowing logical groupings inside subfolders. The YAML loader merges these fragments sequentially in-memory before passing the resolved structured string to `YamlDotNet`.

### 4.2. Native Type Deserialization (The Interface Mapping)
We avoid fragile generic parsing in YAML. We embrace `YamlDotNet` standard tag parsing:
1.  ** `IYamlStep` Marker:** All steps expose `stepId`, `inputSchema`, and `outputSchema` explicitly as properties defined by `IYamlStep`.
2.  **Reflection Auto-Discovery:** On boot, the factory grabs `AppDomain.CurrentDomain.GetAssemblies()` and finds all classes implementing `IYamlStep` annotated with `[YamlStepTag("LlmStep")]`.
3.  **Deserializer Registration:** It automatically registers `DeserializerBuilder().WithTagMapping("!LlmStep", type)` without hardcoded switches.
4.  **Property Binding:** Once instantiated by `YamlDotNet`, the framework has all properties matched cleanly using required init parameters.

---

## 5. Core Refactoring: The `BaseLlmStep` Evolution

To allow these features to exist, the core architecture undergoes a critical shift to detach generic `<TIn, TOut>` compile-time enforcement from the base LLM operational logic.

### 5.1. Moving to Constructor-Level Types
The signature `BaseLlmStep<TIn, TOut>` is retired in favor of a standard `BaseLlmStep(Type inputType, Type outputType)`.
*   The base class manages LLM communication, retry loops, streaming, metric event logic, and tool tracking by relying exclusively on downcasted `IStepResult`.

### 5.2. The Dual-Headed Hierarchy
From `BaseLlmStep`, two distinct families branch out:

1.  **Code-First Users (`TypedLlmStep<TIn, TOut>`)**:
    *   Designed for pure C# usage.
    *   Constructors retain `Func<TIn, string>` strong delegates.
    *   Injects `typeof(TIn)` and `typeof(TOut)` to the parent.
2.  **YAML-First Users (`YamlLlmStep`)**:
    *   Implements `IYamlStep`.
    *   Configures everything via primitive string properties (`InputSchema`, `SystemPrompt`).
    *   Uses a factory interception step post-deserialization to read the raw `InputSchema` string ("Factura"), fetch the matching dynamic `Type` from `SchemaCompiler`, and assign it to the Parent's `inputType`.

---

## 6. Dynamic Evaluation Capabilities

### 6.1. Template Resolution (`@` Prefix Syntax)
The framework supports late-binding interpolation syntax for prompt strings **and data payload mapping** using the `{}` handlebars syntax managed by `ITemplateProvider` + `JsonTemplateEngine`.

*   **Syntax Format:** References are defined explicitly by a leading `@` followed strictly by the **template *name*** (e.g., `@extractor`, `@resumidor`), *not paths or extensions*.
*   **Resolution:** The `ITemplateProvider.GetTemplate("extractor")` implementation handles searching standard directories, resolving the physical file or database record, and returning the base markdown.
*   **Scope:** The engine renders against `PipelineContext` to inject parameters globally.
    *   *Usage Example:* `prompt: "@extractor"` -> `ITemplateProvider` finds "extractor" -> engine resolves `{{ Context.StepResults['PasosPrevios'].Dato }}`.

### 6.2. Security and Profiles (No Secrets in YAML)
No tokens, URLs, or API keys will ever reside in the Pipeline YAML.
*   **YAML Spec:** The YAML step only references profile names (e.g., `profile: "OpenAI_Advanced_Model"`).
*   **Resolver Mechanics:** `ILlmProviderResolver.GetProvider(profileName)` is invoked by the Engine during the execution phase. This service extracts secrets from secure storage or standard environment variables safely before passing configs down to the `LLMService`.

---

## 7. Execution Guardrails & Pre-Validation

The orchestration factory will implement a final fail-fast **Semantic Pass** over the assembled DAG before emitting the runtime Pipeline.
1.  **Schema Mismatch Detection:** If Step 1 `outputSchema="A"` but its dependent Step 2 specifies `inputSchema="B"`, the system throws an instant ConfigurationException on load to prevent cryptic runtime casting errors down the line.
2.  **Cycle Detection:** Circular `dependsOn` arrays are detected using a topological sort on boot.
