# AITaskAgent Framework

Extensible framework for creating AI agents that execute complex tasks through pipelines of steps (Steps).

> **Structure in v2.** This README lives in `AITaskAgent.Core`, the engine, but describes the complete framework, which is now split across
> `AITaskAgent.Abstractions` (contracts, base `PipelineContext`, base events), `AITaskAgent.Core` (engine), `AITaskAgent.Extensions.Llm`
> (LLM steps, conversation, tools, `LlmPipelineContext`), `AITaskAgent.Yaml` and `AITaskAgent.Extensions.Script`. The namespaces have not changed.
> See [docs/ARCHITECTURE-v2.md](../docs/ARCHITECTURE-v2.md).

## Table of Contents

- [Architectural Decisions](#architectural-decisions)
- [General Architecture](#general-architecture)
- [Class Hierarchy](#class-hierarchy)
- [API Reference](#api-reference)
- [Creating a Custom Step](#creating-a-custom-step)
- [Creating an LLM Step](#creating-an-llm-step)
- [Creating a Tool for LLM](#creating-a-tool-for-llm)
- [Observability Guide](#observability-guide)
- [System Events](#system-events)
- [Metrics](#metrics)
- [Pipeline Configuration](#pipeline-configuration)

---

## Architectural Decisions

This framework is built on fundamental architectural decisions that guarantee robustness, observability and maintainability in production environments.

### Observability: OpenTelemetry

**Decision:** Use of **OpenTelemetry** as the standard for distributed tracing.

**Rationale:**
- Industry standard (CNCF)
- Native support in .NET with `System.Diagnostics.Activity`
- Integration with Jaeger, Zipkin, Azure Application Insights, AWS X-Ray
- Clear separation between Traces, Metrics and Logs

**Implementation:**
- `Telemetry.Source` (`AITaskAgent.Core.Base`) is the centralized `ActivitySource` (name `AITaskAgent`)
- Pipeline, Steps (via `ObservabilityMiddleware`), LLM invocations and Tools create spans automatically
- Standardized tags in `AITaskAgentConstants.TelemetryTags`
- Centralized constants in `AITaskAgentConstants`

**Usage:**
```csharp
using Activity? activity = Telemetry.Source.StartActivity("Operation");
activity?.SetTag(AITaskAgentConstants.TelemetryTags.StepName, "MyStep");
```

### JSON: Newtonsoft.Json + NJsonSchema

**Decision:** Use of **Newtonsoft.Json** and **NJsonSchema** for all LLM response deserialization and schema validation.

**Rationale:**
- **Robustness:** `System.Text.Json` is too strict for the syntactic "creativity" of LLMs (extra commas, comments)
- **Fault tolerance:** Newtonsoft prioritizes tolerance over CPU micro-optimizations
- **I/O-bound context:** The overhead of JSON parsing is negligible compared to LLM latency (1-5 seconds)
- **Validation:** NJsonSchema provides robust JSON schema validation

**Rejected alternatives:**
- `System.Text.Json`: Too strict, fails with "creative" LLM JSON
- Manual validation: Error-prone and hard to maintain

### Mandatory Async

**Decision:** The entire public API is asynchronous. Blocking threads with `.Result`, `.Wait()` or excessive locks is prohibited.

**Rationale:**
- LLM operations are inherently asynchronous (HTTP, streaming)
- Scalability: Thousands of concurrent requests without exhausting threads
- Natural integration with ASP.NET Core, Blazor, etc.

**Requirements:**
- Use `Task.WhenAll` or `Parallel.ForEachAsync` for concurrency
- `CancellationToken` propagated in all operations
- Avoid `ConfigureAwait(false)` (unnecessary in .NET Core+)

### Hybrid Validation (Structural vs Semantic)

**Decision:** Clear distinction between DTO validation (`IStepResult.ValidateAsync`) and business validation (`IStepValidator` validators of the Step).

**Separation of responsibilities:**

| Type | Responsible | When it runs | Examples |
|------|-------------|--------------|----------|
| **Structural** | `IStepResult.ValidateAsync(PipelineContext)` | After each attempt that does not return an error | Nulls, types, basic formats |
| **Semantic** | `IStep.ValidateAsync()` (in `StepBase`, the `IStepValidator` added with `AddValidator`) | After passing structural validation | Compilation, DB queries, complex logic |

Both are executed by `RetryMiddleware`; if one fails, it emits a `StepValidationEvent` and retries the Step.

**Constants:**
- `AITaskAgentConstants.ValidationTypes.Structural`
- `AITaskAgentConstants.ValidationTypes.Semantic`

### Internal Validation with Feedback Loop

**Decision:** The correction of semantic errors (e.g. code that does not compile) happens **inside** the Step's execution through the retry loop of `RetryMiddleware`, which calls the Step again passing the failed result of the previous attempt (`lastStepResult`) as feedback. The main Pipeline does not manage backtracking (*forward-only*).

**Rationale:**
- **Observability:** The pipeline sees only "Step completed successfully or failed"
- **Encapsulation:** The correction logic is contained within the Step
- **Performance:** Retries do not traverse the whole observability chain

### Separation of Contexts (Technical vs Business)

**Decision:** `PipelineContext` (technical, without LLM state) is separated from `ConversationContext` (business, mutable but cloneable). The conversation is not in the base context: it is provided by `LlmPipelineContext : PipelineContext` (`AITaskAgent.Extensions.Llm`), which LLM steps require through `RequiredContext`.

**Rationale:**
- **Thread-safety:** The technical context is a C# record, but not deeply immutable: `Metadata` is a mutable `ConcurrentDictionary` shared with the branches (like `StepResults` and the `EventChannel`, all thread-safe), and `CloneForBranch()` gives each parallel branch its own copy of the path state
- **Context Scoping:** When cloning, we allow filtering the history. Sub-agents do not need the whole previous conversation
- **Isolation:** Each parallel branch owns its own instance (`CloneForBranch()` is virtual and keeps the derived type, with the conversation cloned), avoiding corruption

### Use of Reflection

**Decision:** Use `System.Reflection` for extracting template parameters (`StepResult.GetParameters`, not cached) and for creating results (`StepResultFactory`, which does cache the compiled constructors per type).

**Rationale:**
- **Ergonomics:** Users define normal properties, the framework reads them automatically
- **Performance reality:** In an I/O-bound system, optimizing CPU is premature optimization
- **Empirical measurements:** Reflection is ~0.0015% of total time (15µs vs 1000ms of the LLM)

### Centralized Constants

**Decision:** All magic strings are centralized in `static partial` classes to ease maintenance and extensibility.

**Structure:**
```
AITaskAgent.Abstractions/Constants/
├── AITaskAgentConstants.EventTypes.cs      ← Tipos de eventos
├── AITaskAgentConstants.TelemetryTags.cs   ← Tags de OpenTelemetry, ActivitySource y Meter
└── AITaskAgentConstants.ValidationTypes.cs ← Tipos de validación

AITaskAgent.Extensions.Llm/Constants/
├── LlmConstants.cs                         ← Roles de mensajes
└── FinishReason.cs                         ← Enum de finish reasons
```

**Advantages:**
- Zero scattered magic strings
- Centralized IntelliSense
- Extensible with `partial` from LLM
- Safe refactoring



## General Architecture

The framework follows a **Pipeline** architecture where each task is decomposed into **Steps** that run sequentially. Each Step receives a result from the previous step and produces a new result.

```
┌─────────────┐    ┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│   Input     │───▶│   Step 1    │───▶│   Step 2    │───▶│   Step N    │───▶ Output
│ (StepResult)│    │             │    │             │    │             │
└─────────────┘    └─────────────┘    └─────────────┘    └─────────────┘
                         │                  │                  │
                         ▼                  ▼                  ▼
                   ┌──────────────────────────────────────────────┐
                   │         PipelineContext                      │
                   │  - CorrelationId (distributed tracing)       │
                   │  - EventChannel (real-time events)           │
                   │  - ServiceProvider (DI)                      │
                   │  - (LlmPipelineContext) Conversation         │
                   │  - StepResults / Metadata (shared storage)   │
                   └──────────────────────────────────────────────┘
```

### Design Principles

1. **Template Method Pattern**: The base classes control the observability flow; the derived classes implement the specific logic.
2. **Virtual Hooks**: Methods that derived classes can override to enrich metrics, events and traces.
3. **Separation of Responsibilities**: Logs, Traces, Metrics and Events are independent pillars.
4. **Extensibility**: Designed as an extensible library (by project reference) - all base classes can be derived from.

---

## Class Hierarchy

### Steps (Pipeline Steps)

```
IStep (interface)
├── StepBase (abstract class)
│   │   ├── ExecuteAsync()                   [protected abstract] - IMPLEMENTAR: lógica del paso
│   │   │       (input, context, attempt, lastStepResult, cancellationToken)
│   │   │
│   │   │   Reintentos, timeout y observabilidad NO están en StepBase: los aplica la
│   │   │   cadena de middlewares del Pipeline (ObservabilityMiddleware → TimeoutMiddleware
│   │   │   → RetryMiddleware), que llama a los hooks a través de IEnrichable.
│   │   │
│   │   │   ══════════════════════════════════════════════════════════════
│   │   │   HOOKS DE OBSERVABILIDAD (Patrón uniforme)
│   │   │   Pre-exec: (input, context) | Post-exec: (result, context)
│   │   │   ══════════════════════════════════════════════════════════════
│   │   ├── EnrichActivityBefore()           [protected internal virtual] - Tags pre-ejecución
│   │   ├── EnrichStartedEvent()             [protected virtual] - Evento inicio
│   │   ├── EnrichActivityAfter()            [protected internal virtual] - Tags post-ejecución
│   │   ├── EnrichCompletedEvent()           [protected virtual] - Evento fin
│   │   ├── Validate()                       [protected virtual] - Validación semántica (IStepValidator)
│   │   ├── FinalizeAsync()                  [protected virtual] - Hook tras el último intento
│   │   ├── SaveStateAsync() / RestoreStateAsync() [protected virtual] - Suspensión y reanudación
│   │   ├── SwitchToNextFallback()           [protected virtual] - Fallback (IFallbackable)
│   │   ├── CreateResult()                   [protected] - Factory de resultado
│   │   └── CreateErrorResult()              [protected] - Factory de error
│   │
│   ├── TypedStep<TIn, TOut> (generic abstract class)
│   │   │   ├── ExecuteAsync(TIn, ...)       [protected abstract] - IMPLEMENTAR: lógica tipada
│   │   │   ├── CreateResult()               [protected new] - Factory tipada
│   │   │   └── CreateErrorResult()          [protected new] - Factory error tipada
│   │   │
│   │   ├── DelegatedStep<TIn, TOut>
│   │   │   └── Step simple basado en función lambda
│   │   └── ActionStep<TIn>
│   │       └── Acción que devuelve ActionResult (opcionalmente fire-and-forget)
│   │
│   ├── GroupStep / LoopStep / ParallelStep / BreakStep / DeclarativeSwitchStep
│   │   └── Control de flujo (declarables en YAML por su [StepTag]); ParallelStep.FailFast (true por defecto)
│   │
│   ├── ExecStep
│   │   └── Ejecuta un .exe/.bat/.cmd sin shell y devuelve su stdout; IExecStepPolicy opcional por DI
│   │
│   └── BaseLlmStep (abstract, no genérico)
│       │   ├── BuildUserMessageAsync()      [protected virtual] - Mensaje del usuario
│       │   ├── BuildSystemMessageAsync()    [protected virtual] - Prompt del sistema
│       │   ├── BuildLlmRequestAsync()       [protected virtual] - Personalizar request
│       │   ├── ConfigureLlmRequest()        [protected virtual] - Ajustes menores del request
│       │   └── ParseLlmResponseAsync()      [protected virtual] - Parsear respuesta
│       │
│       ├── YamlLlmStep
│       │   └── Paso LLM declarado en YAML (tag LlmStep, JSON Schema opcional)
│       └── BaseLlmStep<TIn, TOut>
│           └── TemplateLlmStep<TIn, TOut>
│               ├── StatelessTemplateLlmStep<TIn, TOut>
│               └── IntentionAnalyzerStep<TIn>
│                   └── Análisis de intención sobre una lista de IntentionOption
│
└── SwitchStep<TIn> (implementa IStep directamente)
    └── RouterStep
        └── Enrutado por la opción elegida (IRouterResult)
```

### Results (Step Results)

**IMPORTANT:** `Value` is the main property where the result content is stored. Additional properties in derived classes are step **metadata** (e.g. TokensUsed in LLM), NOT the main content.

- Use `StepResult<T>` or `LlmStepResult<T>` where `T` is the content type.
- Additional properties in derived classes are for observability metadata.

```
IStepResult (interface)
└── StepResult (abstract class)
    │   ├── Value                    [public, set internal protected] - CONTENIDO PRINCIPAL del resultado
    │   ├── HasError                 [public] - Indica si hay error
    │   ├── Error                    [public] - Información del error
    │   ├── StepName / StepType      [public] - Paso que produjo el resultado
    │   ├── NextSteps                [public virtual] - Steps a ejecutar a continuación (control de flujo)
    │   ├── ValidateAsync(context)   [public virtual] - Validación estructural
    │   └── GetParameters()          [public virtual] - Parámetros para templates
    │
    ├── StepResult<T>                [Resultado tipado genérico]
    │   └── Value                    [public T?] - Contenido tipado
    │
    └── LlmStepResult (class)        [Metadatos LLM adicionales]
        │   ├── TokensUsed           [public] - Tokens consumidos (metadata)
        │   ├── CostUsd              [public] - Costo estimado (metadata)
        │   ├── Model                [public] - Modelo usado (metadata)
        │   ├── FinishReason         [public] - Razón de finalización (metadata)
        │   ├── Reasoning            [public] - Razonamiento del modelo, si lo hay (metadata)
        │   └── AssistantMessage     [public] - Mensaje raw del asistente
        │
        └── LlmStepResult<T>         [USAR ESTE para respuestas JSON estructuradas]
            └── Value                [public T?] - Contenido deserializado del LLM
```

### Tools (Tools for LLM)

```
ITool (interface)
└── LlmTool (abstract class)
    │   ══════════════════════════════════════════════════════════════
    │   PROPIEDADES
    │   ══════════════════════════════════════════════════════════════
    ├── Name                     [public abstract] - IMPLEMENTAR: nombre del tool
    ├── Description              [public abstract] - IMPLEMENTAR: descripción
    ├── UsageGuidelines          [public virtual] - Pautas para el system prompt (si es null, Description)
    ├── IsReadOnly               [public virtual] - Solo lectura: el detector de bucles tolera más repeticiones
    ├── ParametersSchema         [protected abstract] - IMPLEMENTAR: JSON Schema
    ├── GetDefinition()          [public virtual] - Genera ToolDefinition
    │
    │   ══════════════════════════════════════════════════════════════
    │   EJECUCIÓN (Template Method)
    │   ══════════════════════════════════════════════════════════════
    ├── ExecuteAsync()           [public] - Orquestador: controla observabilidad
    ├── InternalExecuteAsync()   [protected abstract] - IMPLEMENTAR: tu lógica
    │
    │   ══════════════════════════════════════════════════════════════
    │   HOOKS DE OBSERVABILIDAD (Patrón uniforme con StepBase)
    │   Pre-exec: (argumentsJson, context) | Post-exec: (result, context)
    │   ══════════════════════════════════════════════════════════════
    ├── EnrichActivityBefore()     [protected virtual] - Tags pre-ejecución
    ├── EnrichStartedEvent()       [protected virtual] - Evento inicio
    ├── EnrichActivityAfter()      [protected virtual] - Tags post-ejecución
    └── EnrichCompletedEvent()     [protected virtual] - Evento fin
```

---

## API Reference

### Access Modifiers

| Modifier | Meaning |
|----------|---------|
| `public` | Accessible from any code |
| `protected` | Only from derived class |
| `protected internal` | From derived class or same assembly |
| `abstract` | **MUST** be implemented in derived class |
| `virtual` | **CAN** be overridden in derived class |

### StepBase - Observability Hooks (Uniform Pattern)

All of them receive the `PipelineContext`; the pre-execution ones also receive the input and the post-execution ones, the result. They are invoked by `ObservabilityMiddleware` through `IEnrichable`:

| Hook | Parameters | When |
|------|------------|------|
| `EnrichActivityBefore()` | `(Activity?, IStepResult input, PipelineContext context)` | Before executing |
| `EnrichStartedEvent()` | `(StepStartedEvent, IStepResult input, PipelineContext context)` | Before sending event |
| `EnrichActivityAfter()` | `(Activity?, IStepResult result, PipelineContext context)` | After executing |
| `EnrichCompletedEvent()` | `(IStepCompletedEvent, IStepResult result, PipelineContext context)` | Before sending event |

### LlmTool - Observability Hooks (Same Pattern)

Pre-execution receive `(argumentsJson, context)`, post-execution receive `(result, context)`:

| Hook | Parameters | When |
|------|------------|------|
| `EnrichActivityBefore()` | `(Activity?, string argumentsJson, PipelineContext context)` | Before executing |
| `EnrichStartedEvent()` | `(ToolStartedEvent, string argumentsJson, PipelineContext context)` | Before sending event |
| `EnrichActivityAfter()` | `(Activity?, string result, PipelineContext context)` | After executing |
| `EnrichCompletedEvent()` | `(ToolCompletedEvent, string result, PipelineContext context)` | Before sending event |

### BaseLlmStep - Additional LLM Hooks

`BaseLlmStep` is **non-generic**: it receives `Type inputType` and `Type outputType` through its constructor, so that the same step serves both the code-first path and the declarative YAML path, where the response schema (`JsonSchema`) is known when the files are loaded and there are no generated C# types. `BaseLlmStep<TIn, TOut>` still exists as the typed heir for those who write steps in C#.

The constructor takes `(string name, LlmRole role, Type inputType, Type outputType, string prompt,
string? systemPrompt = null)`. It does not receive `ILlmService`: the service comes from `role.Provider.Service`,
and changes only when advancing through the *fallback* chain.

| Method | Access | Type | Description |
|--------|--------|------|-------------|
| `BuildUserMessageAsync()` | protected | virtual | Builds the user message (by default, `Prompt`) |
| `BuildSystemMessageAsync()` | protected | virtual | Builds the system message (by default, `SystemPrompt`) |
| `GetConversationContext()` | protected | virtual | Chooses the step's conversation (by default, the pipeline's) |
| `BuildLlmRequestAsync()` | protected | virtual | Fully customizes the request |
| `ConfigureLlmRequest()` | protected | virtual | Minor adjustments to the already built request |
| `ParseLlmResponseAsync()` | protected | virtual | Parses the LLM response |
| `SwitchToNextFallback()` | protected | override | Advances to the next provider in the chain (`IFallbackable`) |

The tool loop (`MaxToolIterations`, 5 by default) and its execution are private to `BaseLlmStep`;
retries are decided by `RetryMiddleware`.

---

## Creating a Custom Step

### Step 1: Define the Result

Each Step needs a result type that extends `StepResult` (normally `StepResult<T>`, with the content in `Value`):

```csharp
namespace MiProyecto.Results;

using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.StepResults;

/// <summary>Contenido del resultado de validación.</summary>
public sealed record DataValidation(bool IsValid, IReadOnlyList<string> ValidationErrors);

/// <summary>
/// Resultado del paso de validación de datos. Value contiene DataValidation.
/// </summary>
public class DataValidationResult(IStep step) : StepResult<DataValidation>(step)
{
}
```

### Step 2: Create the Step

```csharp
namespace MiProyecto.Steps;

using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using Microsoft.Extensions.Logging;
using MiProyecto.Results;

/// <summary>
/// Step que valida datos de entrada.
/// Los reintentos (MaxRetries, 3 por defecto) los aplica RetryMiddleware.
/// </summary>
public class DataValidationStep(string name = "DataValidation")
    : TypedStep<StepResult<object>, DataValidationResult>(name)
{
    /// <summary>
    /// Lógica principal del paso (protected abstract - DEBE implementarse).
    /// </summary>
    protected override Task<DataValidationResult> ExecuteAsync(
        StepResult<object> input,                 // Resultado del paso anterior
        PipelineContext context,                  // Contexto del pipeline
        int attempt,                              // Número de intento actual (1-based)
        DataValidationResult? lastStepResult,     // Resultado fallido del intento anterior
        CancellationToken cancellationToken)
    {
        Logger.LogDebug("Validando datos, intento {Attempt}", attempt);

        // Lógica de validación...
        List<string> errors = [];

        if (input.Value == null)
        {
            errors.Add("Los datos de entrada son nulos");
        }

        // Crear resultado usando el factory protegido (Value tiene setter internal protected)
        return Task.FromResult(CreateResult(new DataValidation(errors.Count == 0, errors)));
    }
}
```

### Step 3: Enrich Observability (Optional)

If your Step has specific data, override the hooks:

```csharp
public class DataValidationStep : TypedStep<StepResult<object>, DataValidationResult>
{
    private int _recordsProcessed;
    private int _recordsFailed;

    /// <summary>
    /// HOOK: Añade tags OpenTelemetry ANTES de la ejecución.
    /// Llamado por ObservabilityMiddleware automáticamente.
    /// </summary>
    protected override void EnrichActivityBefore(
        Activity? activity,
        IStepResult input,
        PipelineContext context)
    {
        activity?.SetTag("validation.input_type", input.Value?.GetType().Name);
    }

    /// <summary>
    /// HOOK: Añade tags OpenTelemetry DESPUÉS de la ejecución.
    /// Llamado por ObservabilityMiddleware automáticamente.
    /// </summary>
    protected override void EnrichActivityAfter(
        Activity? activity,
        IStepResult result,
        PipelineContext context)
    {
        activity?.SetTag("validation.records_processed", _recordsProcessed);
        activity?.SetTag("validation.records_failed", _recordsFailed);
    }

    /// <summary>
    /// HOOK: Enriquece el evento de completado.
    /// Llamado por ObservabilityMiddleware antes de enviarlo al EventChannel.
    /// </summary>
    protected override IStepCompletedEvent EnrichCompletedEvent(
        IStepCompletedEvent baseEvent,
        IStepResult result,
        PipelineContext context)
    {
        return baseEvent is StepCompletedEvent completed
            ? completed with
            {
                AdditionalData = new Dictionary<string, object?>
                {
                    ["recordsProcessed"] = _recordsProcessed,
                    ["recordsFailed"] = _recordsFailed
                }
            }
            : baseEvent;
    }

    protected override async Task<DataValidationResult> ExecuteAsync(...)
    {
        // Tu lógica aquí...
        _recordsProcessed = 100;
        _recordsFailed = 5;
        // ...
    }
}
```

There is no metrics hook: metrics are native `System.Diagnostics.Metrics` instruments that the framework records (see [Metrics](#metrics)).

---

## Creating an LLM Step

For Steps that interact with LLMs, extend `BaseLlmStep`:

### Step 1: Define the DTO and the LLM Result

**IMPORTANT:** The LLM content goes in `Value`. Create a DTO for the JSON data and use `LlmStepResult<T>`.

```csharp
namespace MiProyecto.Results;

using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Results;
using Newtonsoft.Json;

/// <summary>
/// DTO con los datos JSON que devuelve el LLM.
/// Este será Value en el resultado.
/// </summary>
public sealed record SentimentData
{
    /// <summary>Sentimiento detectado.</summary>
    [JsonProperty("sentiment")]
    public string Sentiment { get; init; } = "neutral";

    /// <summary>Confianza del análisis (0-1).</summary>
    [JsonProperty("confidence")]
    public double Confidence { get; init; }

    /// <summary>Explicación del análisis.</summary>
    [JsonProperty("reasoning")]
    public string Reasoning { get; init; } = string.Empty;
}

/// <summary>
/// Resultado del análisis de sentimiento.
/// Value contiene SentimentData (el contenido del LLM).
/// Las propiedades heredadas (TokensUsed, CostUsd, etc.) son metadatos.
/// </summary>
public sealed class SentimentAnalysisResult(IStep step) : LlmStepResult<SentimentData>(step)
{
    /// <summary>Validación estructural del resultado.</summary>
    public override Task<(bool IsValid, string? Error)> ValidateAsync(PipelineContext context)
    {
        if (Value == null)
            return Task.FromResult((false, (string?)"Sentiment data is null"));

        if (Value.Confidence < 0 || Value.Confidence > 1)
            return Task.FromResult((false, (string?)"Confidence must be between 0 and 1"));

        return Task.FromResult((true, (string?)null));
    }
}
```

**Accessing the data:**
```csharp
// CORRECTO: Acceder a través de Value
string sentiment = result.Value?.Sentiment ?? "unknown";
double confidence = result.Value?.Confidence ?? 0;

// CORRECTO: Metadatos del paso
int? tokens = result.TokensUsed;
decimal? cost = result.CostUsd;
```

### Step 2: Create the LLM Step

The LLM service is not injected into the Step: it comes from the `LlmRole` (`role.Provider.Service`), which also provides the model, inference settings, *fallbacks* and tools.

```csharp
namespace MiProyecto.Steps;

using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Steps;
using MiProyecto.Results;

/// <summary>
/// Step que analiza el sentimiento de un texto usando LLM.
/// </summary>
public class SentimentAnalysisStep(LlmRole role, string name = "SentimentAnalysis")
    : BaseLlmStep<StepResult<string>, SentimentAnalysisResult>(
        name,
        role,
        prompt: string.Empty,
        // Prompt del sistema; si es null se usa role.SystemPrompt
        systemPrompt: """
            Eres un experto analizador de sentimientos.
            Siempre respondes en formato JSON con las propiedades:
            - sentiment: "positive", "negative", o "neutral"
            - confidence: número entre 0 y 1
            - reasoning: explicación breve
            """)
{
    /// <summary>
    /// HOOK: Construye el mensaje para el LLM (protected virtual; por defecto, Prompt).
    /// BaseLlmStep<TIn, TOut> ya ha convertido el input a TIn.
    /// </summary>
    protected override Task<string?> BuildUserMessageAsync(
        IStepResult input,
        PipelineContext context)
    {
        string? text = ((StepResult<string>)input).Value;

        return Task.FromResult<string?>($"""
            Analiza el sentimiento del siguiente texto y responde en JSON:

            TEXTO:
            {text}

            Responde SOLO con JSON válido.
            """);
    }
}
```

---

## Creating a Tool for LLM

Tools are functions that the LLM can invoke during its execution:

```csharp
namespace MiProyecto.Tools;

using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Tools.Base;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

/// <summary>
/// Tool que busca información del clima.
/// </summary>
public class WeatherTool : LlmTool
{
    /// <summary>Nombre único del tool (public abstract - IMPLEMENTAR).</summary>
    public override string Name => "get_weather";

    /// <summary>Descripción para el LLM (public abstract - IMPLEMENTAR).</summary>
    public override string Description =>
        "Obtiene el clima actual para una ciudad específica.";

    /// <summary>
    /// JSON Schema de los parámetros (protected abstract - IMPLEMENTAR).
    /// </summary>
    protected override BinaryData ParametersSchema => BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "city": {
                    "type": "string",
                    "description": "Nombre de la ciudad"
                },
                "units": {
                    "type": "string",
                    "enum": ["celsius", "fahrenheit"],
                    "description": "Unidades de temperatura"
                }
            },
            "required": ["city"]
        }
        """);

    /// <summary>
    /// Lógica interna del tool (protected abstract - IMPLEMENTAR).
    /// ExecuteAsync (público) maneja la observabilidad automáticamente.
    /// </summary>
    protected override async Task<string> InternalExecuteAsync(
        string argumentsJson,
        PipelineContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Deserializar argumentos
        var args = JsonConvert.DeserializeAnonymousType(
            argumentsJson,
            new { city = "", units = "celsius" });

        // Lógica del tool...
        await Task.Delay(100, cancellationToken);

        return JsonConvert.SerializeObject(new
        {
            args!.city,
            temperature = 22,
            args.units,
            condition = "sunny"
        });
    }
}
```

### Registering Tools in a Step

By default the Step takes `role.Tools`; they can also be assigned on the Step itself:

```csharp
var sentimentStep = new SentimentAnalysisStep(role)
{
    Tools = [new WeatherTool(), new SearchTool()]
};
```

---

## Observability Guide

The framework implements **4 observability pillars**, each with its own APIs and destinations:

| Pillar | API | Purpose | Common Destinations |
|--------|-----|---------|---------------------|
| **Logs** | `ILogger` | Debugging, errors | Console, Files, Seq |
| **Traces** | `Activity` (OpenTelemetry) | Distributed tracing | Jaeger, Zipkin, App Insights |
| **Metrics** | `System.Diagnostics.Metrics` (Meter `AITaskAgent.Pipeline`) | Aggregated telemetry | Prometheus, Grafana |
| **Events** | `IEventChannel` | Real-time UI updates | SSE, WebSocket, SignalR |

### Observability Flow of a Step

`Pipeline` executes each Step through a chain of middlewares: first those registered in
`PipelineMiddlewareRegistry` and then the internal `ObservabilityMiddleware` → `TimeoutMiddleware`
→ `RetryMiddleware`:

```
ObservabilityMiddleware.InvokeAsync()
│
├─1. TRACES: StartActivity("Step.{Name}")
│   ├─ SetTag("step.name", Name)
│   ├─ SetTag("step.type", GetType().Name)
│   └─ SetTag("correlation.id", context.CorrelationId)
│
├─2. TRACES HOOK: EnrichActivityBefore(activity, input, context)
│   └─ [Tu código] - Añade tags personalizados pre-ejecución
│
├─3. EVENTS: StepStartedEvent (AttemptNumber = 1)
│   ├─ HOOK: EnrichStartedEvent(baseEvent, input, context)
│   └─ context.SendEventAsync() → context.EventChannel
│
├─4. TimeoutMiddleware (step.Timeout o DefaultStepTimeout)
│   └─ RetryMiddleware [RETRY LOOP hasta MaxRetries]
│       ├─ Intento > 1: EVENTS: StepStartedEvent { AttemptNumber }
│       │
│       ├─ step.ExecuteAsync(input, context, attempt, lastStepResult) ← TU LÓGICA
│       │
│       ├─ Excepción o error con OriginalException: no se reintenta
│       │   (salvo SwitchToNextFallback); FinalizeAsync y fin
│       ├─ Error sin excepción: reintento con feedback
│       │
│       ├─ VALIDATION - Structural:
│       │   ├─ result.ValidateAsync(context)
│       │   └─ Si falla: EVENTS: StepValidationEvent { ValidationType: "structural" } y reintento
│       │
│       ├─ VALIDATION - Semantic (IStep.ValidateAsync → IStepValidator):
│       │   └─ Si falla: EVENTS: StepValidationEvent { ValidationType: "semantic" } y reintento
│       │
│       ├─ METRICS: aitaskagent.step.retries en cada reintento
│       └─ Éxito: FinalizeAsync(result)
│
├─5. TRACES: SetTag("step.duration_ms"), SetTag("step.success"), SetStatus(OK/Error)
│
├─6. TRACES HOOK: EnrichActivityAfter(activity, result, context)
│   └─ [Tu código] - Añade tags personalizados post-ejecución
│
├─7. METRICS: aitaskagent.step.executions, aitaskagent.step.duration
│   └─ aitaskagent.step.errors si hay error
│
└─8. EVENTS: StepCompletedEvent (incluye StepResult)
    ├─ HOOK: EnrichCompletedEvent(baseEvent, result, context)
    └─ context.SendEventAsync() → context.EventChannel
```

### Observability Flow in BaseLlmStep (Additional)

`BaseLlmStep` adds LLM-specific observability **on top of** that of the Step:

```
BaseLlmStep.ExecuteAsync()
│
├─ [Por cada iteración LLM, hasta MaxToolIterations]
│   ├─ TRACES: StartActivity("LLM.Invoke", ActivityKind.Client)
│   │   └─ SetTag("llm.model", model)
│   │   └─ SetTag("llm.provider", servicio LLM)
│   │   └─ SetTag("llm.iteration", n) / SetTag("llm.has_tools", ...)
│   │
│   ├─ LOGS: LogDebug("LLM invoke starting (iteration ...)")
│   │
│   ├─ [Llamada al LLM] → EVENTS: LlmResponseEvent (por chunk en streaming y respuesta final)
│   │
│   ├─ TRACES: SetTag("llm.tokens_used"), SetTag("llm.finish_reason")
│   ├─ METRICS: aitaskagent.llm.duration, aitaskagent.llm.requests,
│   │           aitaskagent.llm.tokens, aitaskagent.llm.cost
│   │
│   └─ Si hay tool calls:
│       └─ [Ver flujo de Tools abajo]
│
└─ Tokens y coste en el resultado (TokensUsed, CostUsd) y en context.Metadata
```

### Observability Flow in Tools

Controlled by `LlmTool.ExecuteAsync()`, invoked by `BaseLlmStep` for each tool call:

```
LlmTool.ExecuteAsync(argumentsJson, context, stepName, logger, cancellationToken)
│
├─ TRACES: StartActivity("Tool.{Name}")
│   └─ SetTag("tool.name", Name)
│   └─ SetTag("step.name", stepName)
│   └─ SetTag("correlation.id", context.CorrelationId)
│
├─ TRACES HOOK: EnrichActivityBefore(activity, argumentsJson, context)
│
├─ EVENTS: ToolStartedEvent (AdditionalData["parameters"])
│   └─ HOOK: EnrichStartedEvent → context.SendEventAsync()
│
├─ LOGS: LogDebug("Tool {ToolName} executing ...")
│
├─ InternalExecuteAsync(argumentsJson, context, logger, ct) ← TU LÓGICA
│
├─ LOGS: LogDebug("Tool {ToolName} completed ...") o LogError + rethrow
│
├─ TRACES: Activity.SetStatus(OK/Error), HOOK: EnrichActivityAfter,
│          SetTag("tool.duration_ms"), SetTag("tool.success")
│
├─ METRICS: aitaskagent.tool.executions, aitaskagent.tool.duration
│
└─ EVENTS: ToolCompletedEvent
    └─ HOOK: EnrichCompletedEvent → context.SendEventAsync()
```

### Observability Flow in Pipeline

The Pipeline handles observability at the level of the full execution:

```
Pipeline.ExecuteAsync(name, steps, input, context, ...)
│
├─ TRACES: StartActivity("Pipeline.{name}")
│   └─ SetTag("pipeline.name", name)
│   └─ SetTag("correlation.id", context.CorrelationId)
│   └─ SetTag("pipeline.step_count", steps.Count)
│   └─ SetTag("pipeline.timeout_ms", timeout)
│
├─ EVENTS: SendEventAsync(PipelineStartedEvent)
├─ METRICS: aitaskagent.pipeline.executions
│
├─ [Por cada Step]
│   └─ cadena de middlewares ← [Ver flujo del Step arriba]
│
├─ TRACES: Activity.SetStatus(OK/Error)
├─ METRICS: aitaskagent.pipeline.duration
│
└─ EVENTS: SendEventAsync(PipelineCompletedEvent)
```

---

## System Events

### Event Table and Origin

| Event | Sent from | When | Properties |
|-------|-----------|------|------------|
| `PipelineStartedEvent` | `Pipeline.ExecuteAsync()` | Pipeline start | `PipelineName`, `TotalSteps`, `CorrelationId` |
| `PipelineCompletedEvent` | `Pipeline.ExecuteAsync()` | Pipeline end | `Success`, `Duration`, `ErrorMessage` |
| `StepStartedEvent` | `ObservabilityMiddleware` (attempt 1) and `RetryMiddleware` (retries) | Start of each attempt | `StepName`, `StepPath`, `AttemptNumber`, `AdditionalData`, `Display` |
| `StepValidationEvent` | `RetryMiddleware` | When a validation fails | `IsValid`, `ValidationError`, `ValidationType`, `AttemptNumber` |
| `StepCompletedEvent` | `ObservabilityMiddleware` | End of each step | `Success`, `Duration`, `ErrorMessage`, `AdditionalData`, `StepResult`, `Display` |
| `StepProgressEvent` | Manual (your code) | During execution | `Message`, `CurrentProgress`, `TotalProgress` |
| `LlmResponseEvent` | `BaseLlmStep` | Each streaming chunk and the final response | `Content`, `IsThinking`, `FinishReason`, `TokensUsed`, `CostUsd`, `ToolCalls` |
| `ToolStartedEvent` | `LlmTool.ExecuteAsync()` | Start of tool | `ToolName`, `StepName`, `AdditionalData` (`parameters`: the JSON arguments of the call), `Display` |
| `ToolCompletedEvent` | `LlmTool.ExecuteAsync()` | End of tool | `Success`, `Duration`, `ErrorMessage`, `Display`, `AdditionalData` (`parameters` and, if it goes well, `result`: the returned text, untruncated; the same keys as `McpToolWrapper`) |

`Display` is the optional text a step or tool wants shown for that moment (see [Display text](../docs/guides/event-system.md#display-text)); it is null unless a template was declared.

There are more events in `Observability/Events/` (`StepRoutingEvent`, `TagStartedEvent`/`TagCompletedEvent`, `PipelineSuspendedEvent`/`PipelineResumingEvent`/`PipelineResumedEvent`, `BranchStartedEvent`/`BranchCompletedEvent`, `LoopExhaustedEvent`, `StepCachedHitEvent`, `StepTimeoutEvent`).

### Logging Events

`EventChannel` does not know the concrete events: if `EventLogLevel` is different from `LogLevel.None`, it calls
`evt.WriteLog(logger, level)` for each event, including the `SuppressFromUser` ones. `IProgressEvent.WriteLog` has a
default implementation with the common fields (`EventType`, `StepName`, `CorrelationId`);
`ProgressEventBase` declares it `virtual` and `LlmResponseEvent` overrides it to include `IsThinking`,
`FinishReason`, `Content` and `TokensUsed`. A custom event can override `WriteLog` to log its own fields.

The default level depends on how the channel is created: `EventChannelOptions.EventLogLevel` is `None`, DI registration
(`AddAITaskAgent`) uses `ObservabilityOptions.EventLogLevel` (`Debug` by default) and the
`new EventChannel()` constructor uses `Information`.

### Subscribing to Events (System.Threading.Channels)

`EventChannel` uses `System.Threading.Channels` for asynchronous event delivery:

```csharp
// Obtener el canal (registrado en DI como IEventChannel, o context.EventChannel)
IEventChannel eventChannel = serviceProvider.GetRequiredService<IEventChannel>();

// Opción 1: Suscripción con handler (se procesa en segundo plano; Dispose para desuscribirse)
using IDisposable handlerSubscription = eventChannel.Subscribe((evt, ct) =>
{
    switch (evt)
    {
        case StepStartedEvent started:
            Console.WriteLine($"Step {started.StepName} iniciado");
            break;

        case StepValidationEvent validation:
            if (!validation.IsValid)
                Console.WriteLine($"Validación fallida: {validation.ValidationError}");
            break;

        case StepCompletedEvent completed:
            Console.WriteLine($"Step {completed.StepName} completado en {completed.Duration.TotalMilliseconds}ms");
            break;

        case ToolCompletedEvent tool:
            Console.WriteLine($"Tool {tool.ToolName} ejecutado");
            break;
    }
    return Task.CompletedTask;
});

// Opción 2: Suscripción con IEventSubscription (auto-unsubscribe)
await using IEventSubscription subscription = eventChannel.CreateSubscription(capacity: 100);

await foreach (IProgressEvent evt in subscription.Reader.ReadAllAsync(cancellationToken))
{
    // Procesar eventos...
}
// Al salir del using, se desuscribe automáticamente
```

Both accept an optional `correlationId` to receive only the events of one run.

### SSE (Server-Sent Events) Integration

```csharp
// En un controller ASP.NET Core
[HttpGet("events")]
public async Task StreamEvents(CancellationToken ct)
{
    Response.ContentType = "text/event-stream";
    
    await using IEventSubscription subscription = _eventChannel.CreateSubscription();
    
    await foreach (IProgressEvent evt in subscription.Reader.ReadAllAsync(ct))
    {
        string json = JsonConvert.SerializeObject(evt);
        await Response.WriteAsync($"data: {json}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
```

---

## Metrics

### Instruments

The framework does not expose a collector of its own: it registers native `System.Diagnostics.Metrics` instruments in the
Meter `AITaskAgent.Pipeline` (version `1.0.0`) and the traces in the `ActivitySource` `AITaskAgent`. They are collected with
any `MeterListener` or with OpenTelemetry (`AddMeter("AITaskAgent.Pipeline")`, `AddSource("AITaskAgent")`).

| Instrument | Type | Recorded from | Tags |
|------------|------|---------------|------|
| `aitaskagent.pipeline.executions` | Counter | `Pipeline` | `pipeline.name` |
| `aitaskagent.pipeline.duration` | Histogram (ms) | `Pipeline` | `pipeline.name` |
| `aitaskagent.step.executions` | Counter | `ObservabilityMiddleware` | `step.name`, `step.success` |
| `aitaskagent.step.duration` | Histogram (ms) | `ObservabilityMiddleware` | `step.name` |
| `aitaskagent.step.errors` | Counter | `ObservabilityMiddleware` | `step.name` |
| `aitaskagent.step.retries` | Counter | `RetryMiddleware` | `step.name` |
| `aitaskagent.llm.requests` | Counter | `BaseLlmStep` | `llm.model`, `llm.provider` |
| `aitaskagent.llm.tokens` | Counter | `BaseLlmStep` | `llm.model`, `llm.provider` |
| `aitaskagent.llm.cost` | Counter | `BaseLlmStep` | `llm.model`, `llm.provider` |
| `aitaskagent.llm.duration` | Histogram (ms) | `BaseLlmStep` | `llm.model`, `llm.provider` |
| `aitaskagent.tool.executions` | Counter | `LlmTool` | `tool.name`, `step.success` |
| `aitaskagent.tool.duration` | Histogram (ms) | `LlmTool` | `tool.name` |

---

## Pipeline Configuration

### Creating and Running a Pipeline

```csharp
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Conversation.Context;

// Crear steps (SaveResultsStep: un step tuyo)
IReadOnlyList<IStep> steps = [
    new DataValidationStep(),
    new SentimentAnalysisStep(role),
    new SaveResultsStep()
];

// Crear pipeline (con DI, resolver IPipeline)
var pipeline = new Pipeline(
    defaultPipelineTimeout: TimeSpan.FromMinutes(5),
    defaultStepTimeout: TimeSpan.FromSeconds(30),
    loggerFactory: loggerFactory);

// Configurar contexto (CorrelationId se genera solo). El ejemplo tiene un step LLM,
// que exige un LlmPipelineContext; sin pasos LLM bastaría new PipelineContext(...).
var context = new LlmPipelineContext(myEventChannel);   // EventChannel opcional

// Ejecutar: el input se envuelve en el tipo de entrada del primer step
IStepResult result = await pipeline.ExecuteAsync(
    "MiPipeline",
    steps,
    "...",
    context);

if (result.HasError)
{
    Console.WriteLine($"Error: {result.Error?.Message}");
}
```

---

## Type Flow in the Pipeline (CRITICAL)

### Fundamental Principle: End-to-End Typed Pipeline

The pipeline must maintain **type safety** across the whole flow. Each step:
- Receives a **concrete type** as input (not the base `StepResult`)
- Produces a **concrete type** as output
- The next step declares which type it expects to receive

**Common Problem**: Using the base `StepResult` in signatures causes loss of type. When interpolating `$"{previousResult}"` `.ToString()` is called and you get the C# type name instead of the content.

### The IntentionAnalyzer → Router → Route Steps Pattern

```
┌─────────────────────────────────────────────────────────────────────┐
│  UserInput (p. ej. StepResult<string>)                               │
└─────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│  IntentionAnalyzerStep<TIn>                                          │
│  ─────────────────────────────                                       │
│  Opciones: IList<IntentionOption> (Id + Description)                 │
│  Input:  TIn (texto del usuario)                                     │
│  Output: LlmIntentionResult (LlmStepResult<Intention>, IRouterResult)│
│                                                                      │
│  Intention contiene:                                                 │
│   ├── SelectedOption - Id de la opción elegida                       │
│   ├── SelectionReason - Explicación de la decisión                   │
│   ├── DetailedPrompt - Prompt reformulado para el siguiente step     │
│   ├── UserFeedback - Mensaje y preguntas de aclaración para la UI    │
│   └── ExtraInformation - Datos extraídos del mensaje                 │
│                                                                      │
│  El catálogo de opciones (Id: Description) se añade al system prompt │
│  y la validación estructural rechaza un SelectedOption fuera de él.  │
└─────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│  RouterStep (SwitchStep<IRouterResult>)                              │
│  ────────────────────────────                                        │
│  Rutas: Dictionary<string, IStep> + defaultRoute opcional            │
│  Input:  IRouterResult (LlmIntentionResult)                          │
│  Output: el MISMO resultado de entrada                               │
│                                                                      │
│    PRINCIPIO CLAVE: El Router es TRANSPARENTE al flujo de datos      │
│      - Solo decide qué ruta tomar basándose en SelectedOption        │
│        (sin distinguir mayúsculas)                                   │
│      - NO transforma el dato, lo pasa tal cual al siguiente step     │
│      - Es un "switch" que enruta, no un transformador                │
└─────────────────────────────────────────────────────────────────────┘
                              │
              ┌───────────────┼───────────────┐
              ▼               ▼               ▼
┌────────────────┐  ┌────────────────┐  ┌────────────────┐
│  Ruta: LLM     │  │  Ruta: RAG     │  │  Ruta: Chat    │
├────────────────┤  ├────────────────┤  ├────────────────┤
│ Input: LlmInt- │  │ Input: LlmInt- │  │ Input: LlmInt- │
│ entionResult   │  │ entionResult   │  │ entionResult   │
│                │  │                │  │                │
│ Usa:           │  │ Usa:           │  │ Usa:           │
│ .Value         │  │ .Value         │  │ .Value         │
│ .Detailed      │  │ .ExtraInfor-   │  │ .UserFeedback  │
│ Prompt         │  │ mation →       │  │ → Formatear    │
│                │  │ Buscar en DB   │  │ respuesta      │
│                │  │ → LLM          │  │                │
└────────────────┘  └────────────────┘  └────────────────┘
```

### Type Flow Rules

1. **IntentionAnalyzerStep<TIn>**
   - **Input**: Free text (any `TIn : IStepResult`)
   - **Output**: `LlmIntentionResult` (`Value` is `Intention`)
   - Uses the `Description` of each `IntentionOption` to present the options to the LLM

2. **RouterStep**
   - **Input**: `IRouterResult` (implemented by `LlmIntentionResult`)
   - **Output**: the same input result (IDENTICAL)
   - The Router does **NOT modify the data**, it only selects the route
   - Each route points to a different step or sub-pipeline

3. **First Step of Each Route**
   - **Input**: `LlmIntentionResult`
   - **Can be any kind of step**, not necessarily LLM:
     - **LLM Step**: Uses `input.Value.DetailedPrompt` for the prompt
     - **RAG Step**: Uses `input.Value.ExtraInformation` to search, enriches context
     - **Action Step**: Uses `input.Value.SelectedOption` to decide the action
     - **Conversion Step**: Formats `input.Value.UserFeedback` for the response

### Example of a Correctly Typed Step

```csharp
// CORRECTO: El step declara el tipo exacto que espera
public class StoryWriterStep(LlmRole role)
    : BaseLlmStep<LlmIntentionResult, LlmStringStepResult>("StoryWriter", role, prompt: string.Empty)
{
    protected override Task<string?> BuildUserMessageAsync(
        IStepResult input,
        PipelineContext context)
    {
        // Acceso tipado y seguro al valor (BaseLlmStep<TIn, TOut> ya ha convertido el input a TIn)
        Intention? intention = ((LlmIntentionResult)input).Value;

        return Task.FromResult<string?>($"""
            Write a story based on:
            {intention?.DetailedPrompt}

            Style: {intention?.ExtraInformation.GetValueOrDefault("style")}
            """);
    }
}
```

```csharp
// INCORRECTO: Usar un resultado genérico pierde el tipo
public class BadStep(LlmRole role)
    : BaseLlmStep<StepResult<object>, LlmStringStepResult>("Bad", role, prompt: string.Empty)
{
    protected override Task<string?> BuildUserMessageAsync(
        IStepResult input,  // ← Perdemos el tipo
        PipelineContext context)
    {
        // input.Value es object?, perdemos type safety
        // $"{input.Value}" llama ToString() → nombre del tipo C#
        return Task.FromResult<string?>($"{input.Value}"); // ← BUG!
    }
}
```

### Implementation Checklist

- [ ] `IntentionAnalyzerStep<TIn>` receives the text, produces `LlmIntentionResult`
- [ ] `RouterStep` receives and produces the **same result** (transparent)
- [ ] First step of each route declares `LlmIntentionResult` as input
- [ ] Never interpolate `$"{result}"` directly - access `.Value.PropertyName`
- [ ] Non-LLM steps of a route can also receive `LlmIntentionResult` and extract what they need

---

## Best Practices

### Do

- **One public type per file** (except small related records)
- **Prefer `IEnumerable<T>` over `List<T>`** when you only iterate
- **Use hooks for observability** instead of inline code
- **Validate results** with `ValidateAsync()` or `IStepValidator` validators (`AddValidator`)
- **Use `class` for Results** that need a setter for Value
- **Document all public members with XML comments**

### Avoid

- **`sealed` on base classes** - The framework is extensible by inheritance and by extensions (`IExtension`)
- **Creating unnecessary Lists** - Use IEnumerable and lazy evaluation
- **Logic in constructors** - Use appropriate async methods
- **Ignoring CancellationToken** - Always propagate it
- **Swallowing exceptions** - Let the framework handle them

---

## License

MIT
