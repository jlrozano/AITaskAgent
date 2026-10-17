# AITaskAgent Framework

Framework extensible para crear agentes de IA que ejecutan tareas complejas mediante pipelines de pasos (Steps).

## Tabla de Contenidos

- [Decisiones Arquitectónicas](#decisiones-arquitectónicas)
- [Arquitectura General](#arquitectura-general)
- [Jerarquía de Clases](#jerarquía-de-clases)
- [Referencia de API](#referencia-de-api)
- [Creando un Step Personalizado](#creando-un-step-personalizado)
- [Creando un Step LLM](#creando-un-step-llm)
- [Creando un Tool para LLM](#creando-un-tool-para-llm)
- [Guía de Observabilidad](#guía-de-observabilidad)
- [Eventos del Sistema](#eventos-del-sistema)
- [Métricas](#métricas)
- [Configuración de Pipelines](#configuración-de-pipelines)

---

## Decisiones Arquitectónicas

Este framework está construido sobre decisiones arquitectónicas fundamentales que garantizan robustez, observabilidad y mantenibilidad en entornos de producción.

### Observabilidad: OpenTelemetry

**Decisión:** Uso de **OpenTelemetry** como estándar para distributed tracing.

**Justificación:**
- Estándar de la industria (CNCF)
- Soporte nativo en .NET con `System.Diagnostics.Activity`
- Integración con Jaeger, Zipkin, Azure Application Insights, AWS X-Ray
- Separación clara entre Traces, Metrics y Logs

**Implementación:**
- `Telemetry.Source` (`AITaskAgent.Core.Base`) es el `ActivitySource` centralizado (nombre `AITaskAgent`)
- Pipeline, Steps (vía `ObservabilityMiddleware`), invocaciones LLM y Tools crean spans automáticamente
- Tags estandarizados en `AITaskAgentConstants.TelemetryTags`
- Constantes centralizadas en `AITaskAgentConstants`

**Uso:**
```csharp
using Activity? activity = Telemetry.Source.StartActivity("Operation");
activity?.SetTag(AITaskAgentConstants.TelemetryTags.StepName, "MyStep");
```

### JSON: Newtonsoft.Json + NJsonSchema

**Decisión:** Uso de **Newtonsoft.Json** y **NJsonSchema** para toda la deserialización de respuestas LLM y validación de esquemas.

**Justificación:**
- **Robustez:** `System.Text.Json` es demasiado estricto para la "creatividad" sintáctica de los LLMs (comas extra, comentarios)
- **Tolerancia a fallos:** Newtonsoft prioriza la tolerancia sobre micro-optimizaciones de CPU
- **Contexto I/O bound:** El overhead de JSON parsing es insignificante comparado con la latencia del LLM (1-5 segundos)
- **Validación:** NJsonSchema proporciona validación robusta de esquemas JSON

**Alternativas rechazadas:**
- `System.Text.Json`: Demasiado estricto, falla con JSON "creativo" de LLMs
- Validación manual: Propensa a errores y difícil de mantener

### Async Obligatorio

**Decisión:** Toda la API pública es asíncrona. Prohibido bloquear hilos con `.Result`, `.Wait()` o locks excesivos.

**Justificación:**
- Las operaciones de LLM son inherentemente asíncronas (HTTP, streaming)
- Escalabilidad: Miles de requests concurrentes sin agotar threads
- Integración natural con ASP.NET Core, Blazor, etc.

**Requisitos:**
- Uso de `Task.WhenAll` o `Parallel.ForEachAsync` para concurrencia
- `CancellationToken` propagado en todas las operaciones
- Evitar `ConfigureAwait(false)` (innecesario en .NET Core+)

### Validación Híbrida (Estructural vs Semántica)

**Decisión:** Distinción clara entre validación del DTO (`IStepResult.ValidateAsync`) y validación de negocio (validadores `IStepValidator` del Step).

**Separación de responsabilidades:**

| Tipo | Responsable | Cuándo se ejecuta | Ejemplos |
|------|-------------|-------------------|----------|
| **Estructural** | `IStepResult.ValidateAsync(PipelineContext)` | Tras cada intento que no devuelve error | Nulls, tipos, formatos básicos |
| **Semántica** | `IStep.ValidateAsync()` (en `StepBase`, los `IStepValidator` añadidos con `AddValidator`) | Tras superar la validación estructural | Compilación, queries DB, lógica compleja |

Ambas las ejecuta `RetryMiddleware`; si una falla, emite un `StepValidationEvent` y reintenta el Step.

**Constantes:**
- `AITaskAgentConstants.ValidationTypes.Structural`
- `AITaskAgentConstants.ValidationTypes.Semantic`

### Validación Interna con Feedback Loop

**Decisión:** La corrección de errores semánticos (ej: código que no compila) ocurre **dentro** de la ejecución del Step mediante el bucle de reintentos de `RetryMiddleware`, que vuelve a llamar al Step pasándole el resultado fallido del intento anterior (`lastStepResult`) como feedback. El Pipeline principal no gestiona retrocesos (*forward-only*).

**Justificación:**
- **Observabilidad:** El pipeline ve solo "Step completado con éxito o fallo"
- **Encapsulación:** La lógica de corrección está contenida en el Step
- **Performance:** Los reintentos no atraviesan toda la cadena de observabilidad

### Separación de Contextos (Técnico vs Negocio)

**Decisión:** `PipelineContext` (técnico, inmutable) es separado de `ConversationContext` (negocio, mutable pero cloneable).

**Justificación:**
- **Thread-safety:** El contexto técnico es inmutable (record C#), seguro por diseño
- **Context Scoping:** Al clonar, permitimos filtrar el historial. Los sub-agentes no necesitan toda la conversación anterior
- **Aislamiento:** Cada rama paralela posee su propia instancia, evitando corrupciones

### Uso de Reflection

**Decisión:** Usar `System.Reflection` para extracción de parámetros de templates (`StepResult.GetParameters`, sin cacheo) y creación de resultados (`StepResultFactory`, que sí cachea por tipo los constructores compilados).

**Justificación:**
- **Ergonomía:** Los usuarios definen propiedades normales, el framework las lee automáticamente
- **Realidad de performance:** En un sistema I/O bound, optimizar CPU es optimización prematura
- **Mediciones empíricas:** Reflection es ~0.0015% del tiempo total (15µs vs 1000ms del LLM)

### Constantes Centralizadas

**Decisión:** Todas las magic strings están centralizadas en clases `static partial` para facilitar mantenimiento y extensibilidad.

**Estructura:**
```
Core/Constants/
├── AITaskAgentConstants.EventTypes.cs      ← Tipos de eventos
├── AITaskAgentConstants.TelemetryTags.cs   ← Tags de OpenTelemetry, ActivitySource y Meter
└── AITaskAgentConstants.ValidationTypes.cs ← Tipos de validación

LLM/Constants/
├── LlmConstants.cs                         ← Roles de mensajes
└── FinishReason.cs                         ← Enum de finish reasons
```

**Ventajas:**
- Cero magic strings dispersas
- IntelliSense centralizado
- Extensible con `partial` desde LLM
- Refactoring seguro



## Arquitectura General

El framework sigue una arquitectura de **Pipeline** donde cada tarea se descompone en **Steps** (pasos) que se ejecutan secuencialmente. Cada Step recibe un resultado del paso anterior y produce un nuevo resultado.

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
                   │  - Conversation (ConversationContext)        │
                   │  - StepResults / Metadata (shared storage)   │
                   └──────────────────────────────────────────────┘
```

### Principios de Diseño

1. **Template Method Pattern**: Las clases base controlan el flujo de observabilidad; las derivadas implementan la lógica específica.
2. **Hooks Virtuales**: Métodos que las clases derivadas pueden sobrescribir para enriquecer métricas, eventos y traces.
3. **Separación de Responsabilidades**: Logs, Traces, Metrics y Events son pilares independientes.
4. **Extensibilidad**: Diseñado como NuGet package - todas las clases base pueden derivarse.

---

## Jerarquía de Clases

### Steps (Pasos de Pipeline)

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
│   │   │
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
│   ├── GroupStep / LoopStep / ParallelStep / BreakStep
│   │   └── Control de flujo
│   │
│   └── BaseLlmStep (abstract, no genérico)
│       │   ├── BuildUserMessageAsync()      [protected virtual] - Mensaje del usuario
│       │   ├── BuildSystemMessageAsync()    [protected virtual] - Prompt del sistema
│       │   ├── BuildLlmRequestAsync()       [protected virtual] - Personalizar request
│       │   ├── ConfigureLlmRequest()        [protected virtual] - Ajustes menores del request
│       │   └── ParseLlmResponseAsync()      [protected virtual] - Parsear respuesta
│       │
│       ├── YamlLlmStep
│       │   └── Paso LLM declarado en YAML
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

### Results (Resultados de Steps)

**IMPORTANTE:** `Value` es la propiedad principal donde se almacena el contenido del resultado. Las propiedades adicionales en clases derivadas son **metadatos** del paso (ej: TokensUsed en LLM), NO el contenido principal.

- Usa `StepResult<T>` o `LlmStepResult<T>` donde `T` es el tipo del contenido.
- Las propiedades adicionales en clases derivadas son para metadatos de observabilidad.

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

### Tools (Herramientas para LLM)

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

## Referencia de API

### Modificadores de Acceso

| Modificador | Significado |
|-------------|-------------|
| `public` | Accesible desde cualquier código |
| `protected` | Solo desde clase derivada |
| `protected internal` | Desde clase derivada o mismo assembly |
| `abstract` | **DEBE** implementarse en clase derivada |
| `virtual` | **PUEDE** sobrescribirse en clase derivada |

### StepBase - Hooks de Observabilidad (Patrón Uniforme)

Todos reciben el `PipelineContext`; los de pre-ejecución reciben además el input y los de post-ejecución, el resultado. Los invoca `ObservabilityMiddleware` a través de `IEnrichable`:

| Hook | Parámetros | Cuándo |
|------|------------|--------|
| `EnrichActivityBefore()` | `(Activity?, IStepResult input, PipelineContext context)` | Antes de ejecutar |
| `EnrichStartedEvent()` | `(StepStartedEvent, IStepResult input, PipelineContext context)` | Antes de enviar evento |
| `EnrichActivityAfter()` | `(Activity?, IStepResult result, PipelineContext context)` | Después de ejecutar |
| `EnrichCompletedEvent()` | `(IStepCompletedEvent, IStepResult result, PipelineContext context)` | Antes de enviar evento |

### LlmTool - Hooks de Observabilidad (Mismo Patrón)

Pre-ejecución reciben `(argumentsJson, context)`, post-ejecución reciben `(result, context)`:

| Hook | Parámetros | Cuándo |
|------|------------|--------|
| `EnrichActivityBefore()` | `(Activity?, string argumentsJson, PipelineContext context)` | Antes de ejecutar |
| `EnrichStartedEvent()` | `(ToolStartedEvent, string argumentsJson, PipelineContext context)` | Antes de enviar evento |
| `EnrichActivityAfter()` | `(Activity?, string result, PipelineContext context)` | Después de ejecutar |
| `EnrichCompletedEvent()` | `(ToolCompletedEvent, string result, PipelineContext context)` | Antes de enviar evento |

### BaseLlmStep - Hooks LLM Adicionales

`BaseLlmStep` es **no genérico**: recibe `Type inputType` y `Type outputType` por constructor, de
modo que un mismo paso sirve tanto al camino code-first como al declarativo en YAML, donde los tipos
sólo se conocen al compilar los esquemas. `BaseLlmStep<TIn, TOut>` sigue existiendo como heredero
tipado para quien escribe pasos en C#.

El constructor toma `(string name, LlmRole role, Type inputType, Type outputType, string prompt,
string? systemPrompt = null)`. No recibe `ILlmService`: el servicio sale de `role.Provider.Service`,
y cambia solo al avanzar por la cadena de *fallbacks*.

| Método | Acceso | Tipo | Descripción |
|--------|--------|------|-------------|
| `BuildUserMessageAsync()` | protected | virtual | Construye el mensaje del usuario (por defecto, `Prompt`) |
| `BuildSystemMessageAsync()` | protected | virtual | Construye el mensaje de sistema (por defecto, `SystemPrompt`) |
| `GetConversationContext()` | protected | virtual | Elige la conversación del paso (por defecto, la del pipeline) |
| `BuildLlmRequestAsync()` | protected | virtual | Personaliza completamente el request |
| `ConfigureLlmRequest()` | protected | virtual | Ajustes menores sobre el request ya construido |
| `ParseLlmResponseAsync()` | protected | virtual | Parsea la respuesta del LLM |
| `SwitchToNextFallback()` | protected | override | Avanza al siguiente proveedor de la cadena (`IFallbackable`) |

El bucle de tools (`MaxToolIterations`, 5 por defecto) y su ejecución son privados de `BaseLlmStep`;
los reintentos los decide `RetryMiddleware`.

---

## Creando un Step Personalizado

### Paso 1: Definir el Resultado

Cada Step necesita un tipo de resultado que extienda `StepResult` (normalmente `StepResult<T>`, con el contenido en `Value`):

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

### Paso 2: Crear el Step

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

### Paso 3: Enriquecer Observabilidad (Opcional)

Si tu Step tiene datos específicos, sobrescribe los hooks:

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

No hay hook de métricas: las métricas son instrumentos nativos de `System.Diagnostics.Metrics` que registra el framework (ver [Métricas](#métricas)).

---

## Creando un Step LLM

Para Steps que interactúan con LLMs, extiende `BaseLlmStep`:

### Paso 1: Definir el DTO y el Resultado LLM

**IMPORTANTE:** El contenido del LLM va en `Value`. Crea un DTO para los datos JSON y usa `LlmStepResult<T>`.

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

**Acceso a los datos:**
```csharp
// CORRECTO: Acceder a través de Value
string sentiment = result.Value?.Sentiment ?? "unknown";
double confidence = result.Value?.Confidence ?? 0;

// CORRECTO: Metadatos del paso
int? tokens = result.TokensUsed;
decimal? cost = result.CostUsd;
```

### Paso 2: Crear el Step LLM

El servicio LLM no se inyecta en el Step: sale del `LlmRole` (`role.Provider.Service`), que también aporta modelo, inferencia, *fallbacks* y tools.

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

## Creando un Tool para LLM

Los Tools son funciones que el LLM puede invocar durante su ejecución:

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

### Registrar Tools en un Step

Por defecto el Step toma `role.Tools`; también se pueden asignar en el propio Step:

```csharp
var sentimentStep = new SentimentAnalysisStep(role)
{
    Tools = [new WeatherTool(), new SearchTool()]
};
```

---

## Guía de Observabilidad

El framework implementa **4 pilares de observabilidad**, cada uno con sus propias APIs y destinos:

| Pilar | API | Propósito | Destinos Comunes |
|-------|-----|-----------|------------------|
| **Logs** | `ILogger` | Debugging, errores | Console, Archivos, Seq |
| **Traces** | `Activity` (OpenTelemetry) | Distributed tracing | Jaeger, Zipkin, App Insights |
| **Metrics** | `System.Diagnostics.Metrics` (Meter `AITaskAgent.Pipeline`) | Telemetría agregada | Prometheus, Grafana |
| **Events** | `IEventChannel` | Updates real-time a UI | SSE, WebSocket, SignalR |

### Flujo de Observabilidad de un Step

`Pipeline` ejecuta cada Step a través de una cadena de middlewares: primero los registrados en
`PipelineMiddlewareRegistry` y después los internos `ObservabilityMiddleware` → `TimeoutMiddleware`
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

### Flujo de Observabilidad en BaseLlmStep (Adicional)

`BaseLlmStep` añade observabilidad específica de LLM **además** de la del Step:

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

### Flujo de Observabilidad en Tools

Lo controla `LlmTool.ExecuteAsync()`, invocado por `BaseLlmStep` para cada tool call:

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

### Flujo de Observabilidad en Pipeline

El Pipeline maneja observabilidad a nivel de ejecución completa:

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

## Eventos del Sistema

### Tabla de Eventos y Origen

| Evento | Enviado desde | Cuándo | Propiedades |
|--------|---------------|--------|-------------|
| `PipelineStartedEvent` | `Pipeline.ExecuteAsync()` | Inicio del pipeline | `PipelineName`, `TotalSteps`, `CorrelationId` |
| `PipelineCompletedEvent` | `Pipeline.ExecuteAsync()` | Fin del pipeline | `Success`, `Duration`, `ErrorMessage` |
| `StepStartedEvent` | `ObservabilityMiddleware` (intento 1) y `RetryMiddleware` (reintentos) | Inicio de cada intento | `StepName`, `StepPath`, `AttemptNumber`, `AdditionalData` |
| `StepValidationEvent` | `RetryMiddleware` | Cuando una validación falla | `IsValid`, `ValidationError`, `ValidationType`, `AttemptNumber` |
| `StepCompletedEvent` | `ObservabilityMiddleware` | Fin de cada step | `Success`, `Duration`, `ErrorMessage`, `AdditionalData`, `StepResult` |
| `StepProgressEvent` | Manual (tu código) | Durante ejecución | `Message`, `CurrentProgress`, `TotalProgress` |
| `LlmResponseEvent` | `BaseLlmStep` | Cada chunk en streaming y respuesta final | `Content`, `IsThinking`, `FinishReason`, `TokensUsed`, `CostUsd`, `ToolCalls` |
| `ToolStartedEvent` | `LlmTool.ExecuteAsync()` | Inicio de tool | `ToolName`, `StepName`, `AdditionalData` (`parameters`: los argumentos JSON de la llamada) |
| `ToolCompletedEvent` | `LlmTool.ExecuteAsync()` | Fin de tool | `Success`, `Duration`, `ErrorMessage`, `AdditionalData` (`parameters` y, si va bien, `result`: el texto devuelto, sin truncar; las mismas claves que `McpToolWrapper`) |

Hay más eventos en `Observability/Events/` (`StepRoutingEvent`, `TagStartedEvent`/`TagCompletedEvent`, `PipelineSuspendedEvent`/`PipelineResumingEvent`/`PipelineResumedEvent`, `BranchStartedEvent`/`BranchCompletedEvent`, `LoopExhaustedEvent`, `StepCachedHitEvent`, `StepTimeoutEvent`).

### Registro de Eventos en el Log

`EventChannel` no conoce los eventos concretos: si `EventLogLevel` es distinto de `LogLevel.None`, llama a
`evt.WriteLog(logger, level)` por cada evento, también los `SuppressFromUser`. `IProgressEvent.WriteLog` tiene una
implementación por defecto con los campos comunes (`EventType`, `StepName`, `CorrelationId`);
`ProgressEventBase` la declara `virtual` y `LlmResponseEvent` la sobrescribe para incluir `IsThinking`,
`FinishReason`, `Content` y `TokensUsed`. Un evento propio puede sobrescribir `WriteLog` para registrar sus campos.

El nivel por defecto depende de cómo se cree el canal: `EventChannelOptions.EventLogLevel` es `None`, el
registro por DI (`AddAITaskAgent`) usa `ObservabilityOptions.EventLogLevel` (`Debug` por defecto) y el
constructor `new EventChannel()` usa `Information`.

### Suscribirse a Eventos (System.Threading.Channels)

`EventChannel` usa `System.Threading.Channels` para entrega asíncrona de eventos:

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

Ambas aceptan un `correlationId` opcional para recibir solo los eventos de una ejecución.

### Integración con SSE (Server-Sent Events)

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

## Métricas

### Instrumentos

El framework no expone un colector propio: registra instrumentos nativos de `System.Diagnostics.Metrics` en el
Meter `AITaskAgent.Pipeline` (versión `1.0.0`) y las trazas en el `ActivitySource` `AITaskAgent`. Se recogen con
cualquier `MeterListener` o con OpenTelemetry (`AddMeter("AITaskAgent.Pipeline")`, `AddSource("AITaskAgent")`).

| Instrumento | Tipo | Registrado desde | Tags |
|-------------|------|------------------|------|
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

## Configuración de Pipelines

### Crear y Ejecutar un Pipeline

```csharp
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;

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

// Configurar contexto (CorrelationId se genera solo)
var context = new PipelineContext(myEventChannel);   // EventChannel opcional

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

## Flujo de Tipos en el Pipeline (CRÍTICO)

### Principio Fundamental: Pipeline Tipado de Extremo a Extremo

El pipeline debe mantener **type safety** en todo el flujo. Cada step:
- Recibe un **tipo concreto** de input (no `StepResult` base)
- Produce un **tipo concreto** de output
- El siguiente step declara qué tipo espera recibir

**Problema Común**: Usar `StepResult` base en firmas causa pérdida de tipo. Al interpolar `$"{previousResult}"` se llama `.ToString()` y obtienes el nombre del tipo C# en lugar del contenido.

### El Patrón IntentionAnalyzer → Router → Steps de Ruta

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

### Reglas del Flujo de Tipos

1. **IntentionAnalyzerStep<TIn>**
   - **Entrada**: Texto libre (cualquier `TIn : IStepResult`)
   - **Salida**: `LlmIntentionResult` (`Value` es `Intention`)
   - Usa la `Description` de cada `IntentionOption` para presentar las opciones al LLM

2. **RouterStep**
   - **Entrada**: `IRouterResult` (lo implementa `LlmIntentionResult`)
   - **Salida**: el mismo resultado de entrada (IDÉNTICO)
   - El Router **NO modifica el dato**, solo selecciona la ruta
   - Cada ruta apunta a un step o sub-pipeline diferente

3. **Primer Step de Cada Ruta**
   - **Entrada**: `LlmIntentionResult`
   - **Puede ser cualquier tipo de step**, no necesariamente LLM:
     - **LLM Step**: Usa `input.Value.DetailedPrompt` para el prompt
     - **RAG Step**: Usa `input.Value.ExtraInformation` para buscar, enriquece contexto
     - **Action Step**: Usa `input.Value.SelectedOption` para decidir acción
     - **Conversion Step**: Formatea `input.Value.UserFeedback` para respuesta

### Ejemplo de Step Tipado Correctamente

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

### Checklist de Implementación

- [ ] `IntentionAnalyzerStep<TIn>` recibe el texto, produce `LlmIntentionResult`
- [ ] `RouterStep` recibe y produce el **mismo resultado** (transparente)
- [ ] Primer step de cada ruta declara `LlmIntentionResult` como input
- [ ] Nunca interpolar `$"{result}"` directamente - acceder a `.Value.PropertyName`
- [ ] Los steps no-LLM de ruta también pueden recibir `LlmIntentionResult` y extraer lo que necesiten

---

## Buenas Prácticas

### Hacer

- **Un tipo público por archivo** (excepto records pequeños relacionados)
- **Preferir `IEnumerable<T>` sobre `List<T>`** cuando solo iteras
- **Usar hooks para observabilidad** en lugar de código inline
- **Validar resultados** con `ValidateAsync()` o validadores `IStepValidator` (`AddValidator`)
- **Usar `class` para Results** que necesitan setter para Value
- **Documentar con XML comments** todos los miembros públicos

### Evitar

- **`sealed` en clases base** - El framework es extensible por NuGet
- **Crear Lists innecesarias** - Usa IEnumerable y lazy evaluation
- **Lógica en constructores** - Usa métodos async apropiados
- **Ignorar CancellationToken** - Siempre propágalo
- **Swallow exceptions** - Deja que el framework las maneje

---

## Licencia

MIT
