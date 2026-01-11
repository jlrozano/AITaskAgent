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
- `AITelemetry.Source` es el `ActivitySource` centralizado
- Todos los Steps y Tools crean spans automáticamente
- Tags estandarizados en `AITelemetry.Tags`
- Constantes centralizadas en `AITaskAgentConstants`

**Uso:**
```csharp
using Activity? activity = AITelemetry.Source.StartActivity("Operation");
activity?.SetTag(AITelemetry.Tags.StepName, "MyStep");
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

**Decisión:** Distinción clara entre validación del DTO (`IStepResult.ValidateAsync`) y validación de negocio (delegado inyectado).

**Separación de responsabilidades:**

| Tipo | Responsable | Cuándo se ejecuta | Ejemplos |
|------|-------------|-------------------|----------|
| **Estructural** | `IStepResult.ValidateAsync()` | Siempre, incluso en dry-run | Nulls, tipos, formatos básicos |
| **Semántica** | Delegado en Step | Solo en producción, tras validación estructural | Compilación, queries DB, lógica compleja |

**Constantes:**
- `AITaskAgentConstants.ValidationTypes.Structural`
- `AITaskAgentConstants.ValidationTypes.Semantic`

### Validación Interna con Feedback Loop

**Decisión:** La corrección de errores semánticos (ej: código que no compila) ocurre **dentro** del Step mediante un bucle de reintentos con feedback del validador. El Pipeline principal es lineal y no gestiona retrocesos (*forward-only*).

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

**Decisión:** Usar `System.Reflection` para extracción de parámetros de templates y parsing de resultados, sin cacheo manual.

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
└── AITaskAgentConstants.ValidationTypes.cs ← Tipos de validación

LLM/Constants/
└── LlmConstants.cs                         ← Roles de mensajes, finish reasons
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
                   │  - CancellationToken                         │
                   │  - EventChannel (real-time events)           │
                   │  - MetricsCollector (telemetry)              │
                   │  - StepResults (shared storage)              │
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
└── StepBase (abstract class)
    │   ├── InvokeAsync()                    [public] - Controla observabilidad, reintentos
    │   │
    │   │   ══════════════════════════════════════════════════════════════
    │   │   HOOKS DE OBSERVABILIDAD (Patrón uniforme)
    │   │   Pre-exec: (input, context) | Post-exec: (result)
    │   │   ══════════════════════════════════════════════════════════════
    │   ├── EnrichActivity()                 [protected virtual] - Tags pre-ejecución
    │   ├── EnrichStartedEvent()             [protected virtual] - Evento inicio
    │   ├── EnrichActivityAfterExecution()   [protected virtual] - Tags post-ejecución
    │   ├── EnrichMetrics()                  [protected virtual] - Enriquecer métricas
    │   ├── EnrichCompletedEvent()           [protected virtual] - Evento fin
    │   │
    │   ├── InternalInvokeAsync()            [protected abstract] - IMPLEMENTAR: lógica del paso
    │   ├── CreateResult()                   [protected abstract] - Factory de resultado
    │   ├── CreateErrorResult()              [protected] - Factory de error
    │   └── AfterInvoke()                    [protected virtual] - Hook post-ejecución
    │
    └── TypedStep<TIn, TOut> (generic abstract class)
        │   ├── InternalInvokeTypedAsync()   [protected abstract] - IMPLEMENTAR: lógica tipada
        │   ├── CreateResultInstance()       [protected] - Factory tipada
        │   └── CreateErrorResult()          [protected new] - Factory error tipada
        │
        ├── LambdaStep<TIn, TOut>
        │   └── Step simple basado en función lambda
        │
        └── BaseLlmStep<TIn, TOut>
            │   ├── BuildMessageAsync()      [protected virtual] - Construir mensaje
            │   ├── GetSystemPrompt()        [protected virtual] - Prompt del sistema
            │   ├── BuildLlmRequestAsync()   [protected virtual] - Personalizar request
            │   ├── ParseLlmResponseAsync()  [protected virtual] - Parsear respuesta
            │   ├── ExecuteToolsAsync()      [protected virtual] - Ejecutar tools
            │   ├── OnToolExecutedAsync()    [protected virtual] - Hook post-tool
            │   └── ShouldRetryAsync()       [protected virtual] - Decidir reintento
            │
            └── IntentionAnalyzerStep<TEnum>
                └── Análisis de intención con enums
```

### Results (Resultados de Steps)

**IMPORTANTE:** `Value` es la propiedad principal donde se almacena el contenido del resultado. Las propiedades adicionales en clases derivadas son **metadatos** del paso (ej: TokensUsed en LLM), NO el contenido principal.

- Usa `StepResult<T>` o `LlmStepResult<T>` donde `T` es el tipo del contenido.
- Las propiedades adicionales en clases derivadas son para metadatos de observabilidad.

```
IStepResult (interface)
└── StepResult (abstract class)
    │   ├── Value                    [public] - CONTENIDO PRINCIPAL del resultado
    │   ├── HasError                 [public] - Indica si hay error
    │   ├── Error                    [public] - Información del error
    │   ├── Step                     [public] - Referencia al paso
    │   ├── ValidateAsync()          [public virtual] - Validación estructural
    │   └── GetParameters()          [public virtual] - Parámetros para templates
    │
    ├── StepResult<T>                [Resultado tipado genérico]
    │   └── Value                    [public T?] - Contenido tipado
    │
    └── LlmStepResult (abstract class) [Metadatos LLM adicionales]
        │   ├── TokensUsed           [public] - Tokens consumidos (metadata)
        │   ├── CostUsd              [public] - Costo estimado (metadata)
        │   ├── Model                [public] - Modelo usado (metadata)
        │   ├── FinishReason         [public] - Razón de finalización (metadata)
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
    ├── ParametersSchema         [protected abstract] - IMPLEMENTAR: JSON Schema
    ├── GetDefinition()          [public] - Genera ToolDefinition
    │
    │   ══════════════════════════════════════════════════════════════
    │   EJECUCIÓN (Template Method)
    │   ══════════════════════════════════════════════════════════════
    ├── ExecuteAsync()           [public] - Orquestador: controla observabilidad
    ├── InternalExecuteAsync()   [protected abstract] - IMPLEMENTAR: tu lógica
    │
    │   ══════════════════════════════════════════════════════════════
    │   HOOKS DE OBSERVABILIDAD (Patrón uniforme con StepBase)
    │   Pre-exec: (input, context) | Post-exec: (result)
    │   ══════════════════════════════════════════════════════════════
    ├── EnrichActivity()           [protected virtual] - Tags pre-ejecución
    ├── EnrichStartedEvent()       [protected virtual] - Evento inicio
    ├── EnrichActivityAfterExecution() [protected virtual] - Tags post-ejecución
    ├── EnrichMetrics()            [protected virtual] - Enriquecer métricas
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

Pre-ejecución reciben `(input, context)`, post-ejecución reciben `(result)`:

| Hook | Parámetros | Cuándo |
|------|------------|--------|
| `EnrichActivity()` | `(Activity?, IStepResult input, PipelineContext context)` | Antes de ejecutar |
| `EnrichStartedEvent()` | `(StepStartedEvent, IStepResult input, PipelineContext context)` | Antes de enviar evento |
| `EnrichActivityAfterExecution()` | `(Activity?, IStepResult result, bool success)` | Después de ejecutar |
| `EnrichMetrics()` | `(StepMetricData, IStepResult result)` | Antes de enviar métricas |
| `EnrichCompletedEvent()` | `(StepCompletedEvent, IStepResult result)` | Antes de enviar evento |

### LlmTool - Hooks de Observabilidad (Mismo Patrón)

Pre-ejecución reciben `(argumentsJson, context)`, post-ejecución reciben `(result)`:

| Hook | Parámetros | Cuándo |
|------|------------|--------|
| `EnrichActivity()` | `(Activity?, string argumentsJson, PipelineContext context)` | Antes de ejecutar |
| `EnrichStartedEvent()` | `(ToolStartedEvent, string argumentsJson, PipelineContext context)` | Antes de enviar evento |
| `EnrichActivityAfterExecution()` | `(Activity?, string result, bool success)` | Después de ejecutar |
| `EnrichMetrics()` | `(ToolMetricData, string result)` | Antes de enviar métricas |
| `EnrichCompletedEvent()` | `(ToolCompletedEvent, string result)` | Antes de enviar evento |

### BaseLlmStep<TIn, TOut> - Hooks LLM Adicionales

| Método | Acceso | Tipo | Descripción |
|--------|--------|------|-------------|
| `BuildMessageAsync()` | protected | virtual | Construye el mensaje del usuario |
| `GetSystemPrompt()` | protected | virtual | Retorna prompt del sistema |
| `BuildLlmRequestAsync()` | protected | virtual | Personaliza completamente el request |
| `ParseLlmResponseAsync()` | protected | virtual | Parsea respuesta del LLM |
| `ExecuteToolsAsync()` | protected | virtual | Ejecuta tools llamados por LLM |
| `OnToolExecutedAsync()` | protected | virtual | Hook post-ejecución de tool |
| `ShouldRetryAsync()` | protected | virtual | Decide si reintentar |

---

## Creando un Step Personalizado

### Paso 1: Definir el Resultado

Cada Step necesita un tipo de resultado que extienda `StepResult`:

```csharp
namespace MiProyecto.Results;

using AITaskAgent.Core.Models;

/// <summary>
/// Resultado del paso de validación de datos.
/// </summary>
public class DataValidationResult : StepResult
{
    public DataValidationResult(IStep step) : base(step) { }
    
    /// <summary>Indica si los datos son válidos.</summary>
    public bool IsValid { get; init; }
    
    /// <summary>Lista de errores de validación.</summary>
    public IReadOnlyList<string> ValidationErrors { get; init; } = [];
    
    /// <summary>Datos validados (si son válidos).</summary>
    public object? ValidatedData { get; init; }
}
```

### Paso 2: Crear el Step

```csharp
namespace MiProyecto.Steps;

using AITaskAgent.Core.Base;
using AITaskAgent.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Step que valida datos de entrada.
/// </summary>
public class DataValidationStep(
    string name = "DataValidation",
    int maxRetries = 3) 
    : TypedStep<StepResult, DataValidationResult>(name, maxRetryCount: maxRetries)
{
    /// <summary>
    /// Lógica principal del paso (protected abstract - DEBE implementarse).
    /// </summary>
    protected override async Task<DataValidationResult> InternalInvokeTypedAsync(
        StepResult input,          // Resultado del paso anterior
        int retryCount,            // Número de intento actual (1-based)
        string? previousError,     // Mensaje de error del intento anterior
        PipelineContext context,   // Contexto del pipeline
        ILogger logger)            // Logger contextual
    {
        logger.LogDebug("Validando datos, intento {Attempt}", retryCount);
        
        // Lógica de validación...
        List<string> errors = [];
        
        if (input.Value == null)
        {
            errors.Add("Los datos de entrada son nulos");
        }
        
        // Crear resultado usando el factory protegido
        DataValidationResult result = CreateResultInstance();
        
        // Asignar valor al resultado (Value tiene setter protected)
        result.Value = new
        {
            IsValid = errors.Count == 0,
            ValidationErrors = errors
        };
        
        return result;
    }
}
```

### Paso 3: Enriquecer Observabilidad (Opcional)

Si tu Step tiene métricas específicas, sobrescribe los hooks:

```csharp
public class DataValidationStep : TypedStep<StepResult, DataValidationResult>
{
    private int _recordsProcessed;
    private int _recordsFailed;
    
    /// <summary>
    /// HOOK: Añade tags OpenTelemetry ANTES de la ejecución.
    /// Llamado por StepBase.InvokeAsync() automáticamente.
    /// </summary>
    protected override void EnrichActivity(
        Activity? activity, 
        IStepResult input, 
        PipelineContext context)
    {
        activity?.SetTag("validation.input_type", input.Value?.GetType().Name);
    }
    
    /// <summary>
    /// HOOK: Añade tags OpenTelemetry DESPUÉS de la ejecución.
    /// Llamado por StepBase.InvokeAsync() automáticamente.
    /// </summary>
    protected override void EnrichActivityAfterExecution(
        Activity? activity, 
        IStepResult result, 
        bool success)
    {
        activity?.SetTag("validation.records_processed", _recordsProcessed);
        activity?.SetTag("validation.records_failed", _recordsFailed);
    }
    
    /// <summary>
    /// HOOK: Enriquece las métricas con datos específicos.
    /// Llamado por StepBase.InvokeAsync() antes de enviar a MetricsCollector.
    /// </summary>
    protected override IMetricData EnrichMetrics(
        StepMetricData baseMetrics, 
        IStepResult result)
    {
        // Puedes devolver un tipo de métrica más específico o modificar el base
        return baseMetrics;
    }
    
    /// <summary>
    /// HOOK: Enriquece el evento de completado.
    /// Llamado por StepBase.InvokeAsync() antes de enviar a EventChannel.
    /// </summary>
    protected override StepCompletedEvent EnrichCompletedEvent(
        StepCompletedEvent baseEvent, 
        IStepResult result)
    {
        return baseEvent with
        {
            AdditionalData = new Dictionary<string, object>
            {
                ["recordsProcessed"] = _recordsProcessed,
                ["recordsFailed"] = _recordsFailed
            }
        };
    }
    
    protected override async Task<DataValidationResult> InternalInvokeTypedAsync(...)
    {
        // Tu lógica aquí...
        _recordsProcessed = 100;
        _recordsFailed = 5;
        // ...
    }
}
```

---

## Creando un Step LLM

Para Steps que interactúan con LLMs, extiende `BaseLlmStep`:

### Paso 1: Definir el DTO y el Resultado LLM

**IMPORTANTE:** El contenido del LLM va en `Value`. Crea un DTO para los datos JSON y usa `LlmStepResult<T>`.

```csharp
namespace MiProyecto.Results;

using AITaskAgent.Core.Abstractions;
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
    public override Task<(bool IsValid, string? Error)> ValidateAsync()
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

```csharp
namespace MiProyecto.Steps;

using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Steps;
using Microsoft.Extensions.Logging;

/// <summary>
/// Step que analiza el sentimiento de un texto usando LLM.
/// </summary>
public class SentimentAnalysisStep : BaseLlmStep<TextInput, SentimentAnalysisResult>
{
    public SentimentAnalysisStep(
        ILlmService llmService,
        LlmProfile profile,
        string name = "SentimentAnalysis",
        int maxRetries = 3,
        TimeSpan? llmTimeout = null)
        : base(llmService, profile, name, maxRetries: maxRetries, llmTimeout: llmTimeout)
    {
    }
    
    /// <summary>
    /// HOOK: Construye el mensaje para el LLM (protected virtual).
    /// </summary>
    protected override Task<string> BuildMessageAsync(
        TextInput input,
        PipelineContext context,
        ILogger logger)
    {
        return Task.FromResult($"""
            Analiza el sentimiento del siguiente texto y responde en JSON:
            
            TEXTO:
            {input.Text}
            
            Responde SOLO con JSON válido.
            """);
    }
    
    /// <summary>
    /// HOOK: Personaliza el prompt del sistema (protected virtual).
    /// </summary>
    protected override string? GetSystemPrompt(TextInput input, PipelineContext context)
    {
        return """
            Eres un experto analizador de sentimientos.
            Siempre respondes en formato JSON con las propiedades:
            - sentiment: "positive", "negative", o "neutral"
            - confidence: número entre 0 y 1
            - reasoning: explicación breve
            """;
    }
}
```

---

## Creando un Tool para LLM

Los Tools son funciones que el LLM puede invocar durante su ejecución:

```csharp
namespace MiProyecto.Tools;

using AITaskAgent.LLM.Tools.Base;
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

```csharp
var sentimentStep = new SentimentAnalysisStep(llmService, profile)
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
| **Metrics** | `IMetricsCollector` | Telemetría agregada | Prometheus, Grafana |
| **Events** | `IEventChannel` | Updates real-time a UI | SSE, WebSocket, SignalR |

### Flujo de Observabilidad en StepBase

El método `StepBase.InvokeAsync()` controla todo el flujo:

```
InvokeAsync() [StepBase - Orquestador]
│
├─1. TRACES: StartActivity("Step.{Name}")
│   ├─ SetTag("step.name", Name)
│   ├─ SetTag("step.type", GetType().Name)
│   ├─ SetTag("step.correlation_id", context.CorrelationId)
│   └─ SetTag("step.max_retries", MaxRetries)
│
├─2. TRACES HOOK: EnrichActivity(activity, input, context)
│   └─ [Tu código] - Añade tags personalizados pre-ejecución
│
├─3. EVENTS: SendEventAsync(StepStartedEvent)
│   └─ Enviado a: context.EventChannel
│
├─4. [RETRY LOOP]
│   ├─ TRACES: SetTag("step.attempt", attemptNumber)
│   ├─ LOGS: LogDebug("Step {Name} starting attempt {Attempt}")
│   │
│   ├─ InternalInvokeAsync() ← TU LÓGICA
│   │
│   ├─ VALIDATION - Structural:
│   │   ├─ stepResult.ValidateAsync()
│   │   └─ EVENTS: SendEventAsync(StepValidationEvent { ValidationType: "structural" })
│   │
│   ├─ VALIDATION - Semantic (si hay ResultValidator):
│   │   ├─ ResultValidator(stepResult)
│   │   └─ EVENTS: SendEventAsync(StepValidationEvent { ValidationType: "semantic" })
│   │
│   └─ Si falla y hay reintentos: LOGS: LogWarning("Preparing retry...")
│
├─5. TRACES HOOK: EnrichActivityAfterExecution(activity, result, success)
│   └─ [Tu código] - Añade tags personalizados post-ejecución
│
├─6. TRACES: SetTag("step.duration_ms", duration)
│   └─ SetTag("step.attempts", totalAttempts)
│
├─7. METRICS: 
│   ├─ Crear StepMetricData con datos base
│   ├─ HOOK: EnrichMetrics(baseMetrics, result)
│   │   └─ [Tu código] - Retorna métricas enriquecidas
│   └─ context.CollectMetric(enrichedMetrics)
│       └─ Enviado a: context.MetricsCollector
│
├─8. EVENTS:
│   ├─ Crear StepCompletedEvent con datos base
│   ├─ HOOK: EnrichCompletedEvent(baseEvent, result)
│   │   └─ [Tu código] - Retorna evento enriquecido
│   └─ context.SendEventAsync(enrichedEvent)
│       └─ Enviado a: context.EventChannel
│
└─9. TRACES: Activity.SetStatus(OK/Error)
```

### Flujo de Observabilidad en BaseLlmStep (Adicional)

`BaseLlmStep` añade observabilidad específica de LLM **además** de la de `StepBase`:

```
BaseLlmStep.InternalInvokeAsync()
│
├─ [Por cada iteración LLM]
│   ├─ TRACES: StartActivity("LLM.Request")
│   │   └─ SetTag("llm.model", profile.Model)
│   │   └─ SetTag("llm.provider", profile.Provider)
│   │
│   ├─ LOGS: LogDebug("Sending LLM request...")
│   │
│   ├─ [Llamada al LLM]
│   │
│   ├─ LOGS: LogInformation("LLM response: {Tokens} tokens, ${Cost}")
│   │
│   └─ Si hay tool calls:
│       └─ [Ver flujo de Tools abajo]
│
└─ Acumula: _totalTokens, _totalCost, _toolCallCount
```

### Flujo de Observabilidad en Tools

Actualmente manejado por `BaseLlmStep.ExecuteToolsAsync()`:

```
ExecuteToolsAsync() [BaseLlmStep]
│
├─ [Por cada tool call]
│   ├─ TRACES: StartActivity("Tool.{ToolName}")
│   │   └─ SetTag("tool.name", toolCall.Name)
│   │   └─ SetTag("tool.step", StepName)
│   │
│   ├─ EVENTS: SendEventAsync(ToolStartedEvent)
│   │   └─ Enviado a: context.EventChannel
│   │
│   ├─ LOGS: LogDebug("Executing tool {ToolName}")
│   │
│   ├─ tool.ExecuteAsync(arguments)
│   │
│   ├─ LOGS: LogDebug("Tool {ToolName} completed")
│   │
│   ├─ TRACES: Activity.SetStatus(OK/Error)
│   │
│   ├─ METRICS: context.CollectMetric(ToolMetricData)
│   │   └─ Enviado a: context.MetricsCollector
│   │
│   └─ EVENTS: SendEventAsync(ToolCompletedEvent)
│       └─ Enviado a: context.EventChannel
```

### Flujo de Observabilidad en Pipeline

El Pipeline maneja observabilidad a nivel de ejecución completa:

```
Pipeline.ExecuteAsync()
│
├─ TRACES: StartActivity("Pipeline.{Name}")
│   └─ SetTag("pipeline.name", Name)
│   └─ SetTag("pipeline.steps", steps.Count)
│   └─ SetTag("pipeline.correlation_id", context.CorrelationId)
│
├─ EVENTS: SendEventAsync(PipelineStartedEvent)
│
├─ [Por cada Step]
│   └─ step.InvokeAsync() ← [Ver flujo de StepBase arriba]
│
├─ TRACES: Activity.SetStatus(OK/Error)
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
| `StepStartedEvent` | `StepBase.InvokeAsync()` | Inicio de cada step | `StepName`, `AttemptNumber`, `AdditionalData` |
| `StepValidationEvent` | `StepBase.InvokeAsync()` | Tras validar resultado | `IsValid`, `ValidationError`, `ValidationType`, `AttemptNumber` |
| `StepCompletedEvent` | `StepBase.InvokeAsync()` | Fin de cada step | `Success`, `Duration`, `ErrorMessage`, `AdditionalData` |
| `StepProgressEvent` | Manual (tu código) | Durante ejecución | `Message`, `CurrentProgress`, `TotalProgress` |
| `LlmResponseEvent` | `BaseLlmStep` (streaming) | Cada chunk LLM | `Delta`, `IsComplete`, `FinishReason` |
| `ToolStartedEvent` | `LlmTool.ExecuteAsync()` | Inicio de tool | `ToolName`, `StepName`, `AdditionalData` |
| `ToolCompletedEvent` | `LlmTool.ExecuteAsync()` | Fin de tool | `Success`, `Duration`, `ErrorMessage`, `AdditionalData` |

### Suscribirse a Eventos (System.Threading.Channels)

`EventChannel` usa `System.Threading.Channels` para entrega asíncrona de eventos:

```csharp
// Obtener el EventChannel (inyectado o del contexto)
EventChannel eventChannel = serviceProvider.GetRequiredService<EventChannel>();

// Opción 1: Suscripción simple con ChannelReader
ChannelReader<IProgressEvent> reader = eventChannel.Subscribe(capacity: 100);

// Consumir eventos asíncronamente
await foreach (IProgressEvent evt in reader.ReadAllAsync(cancellationToken))
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
}

// Opción 2: Suscripción con IEventSubscription (auto-unsubscribe)
await using IEventSubscription subscription = eventChannel.CreateSubscription(capacity: 100);

await foreach (IProgressEvent evt in subscription.Reader.ReadAllAsync(cancellationToken))
{
    // Procesar eventos...
}
// Al salir del using, se desuscribe automáticamente
```

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

### Tipos de Métricas

| Tipo | Enviado desde | Propiedades Base | Propiedades Adicionales |
|------|---------------|------------------|------------------------|
| `StepMetricData` | `StepBase.InvokeAsync()` | `StepName`, `Duration`, `Success`, `CorrelationId` | `RetryCount`, `ErrorMessage` |
| `LlmMetricData` | Steps LLM (hook) | Hereda de StepMetricData | `TokensUsed`, `CostUsd`, `Model`, `Provider`, `ToolCallCount` |
| `ToolMetricData` | `BaseLlmStep.ExecuteToolsAsync()` | `ToolName`, `Duration`, `Success` | `ArgumentsLength`, `ResultLength` |

### Implementar IMetricsCollector

```csharp
public class PrometheusMetricsCollector : IMetricsCollector
{
    private readonly Counter _stepExecutions;
    private readonly Histogram _stepDuration;
    private readonly Counter _tokensUsed;
    
    public void CollectMetric<TMetric>(TMetric metric) where TMetric : IMetricData
    {
        switch (metric)
        {
            case LlmMetricData llm:
                _stepExecutions.Labels(llm.StepName, llm.Model ?? "unknown").Inc();
                _stepDuration.Labels(llm.StepName).Observe(llm.Duration.TotalSeconds);
                if (llm.TokensUsed.HasValue)
                    _tokensUsed.Labels(llm.Model ?? "unknown").Inc(llm.TokensUsed.Value);
                break;
                
            case StepMetricData step:
                _stepExecutions.Labels(step.StepName, "none").Inc();
                _stepDuration.Labels(step.StepName).Observe(step.Duration.TotalSeconds);
                break;
                
            case ToolMetricData tool:
                _stepExecutions.Labels($"tool:{tool.ToolName}", "none").Inc();
                _stepDuration.Labels(tool.ToolName).Observe(tool.Duration.TotalSeconds);
                break;
        }
    }
}
```

---

## Configuración de Pipelines

### Crear y Ejecutar un Pipeline

```csharp
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;

// Crear steps
IEnumerable<IStep> steps = [
    new DataValidationStep(),
    new SentimentAnalysisStep(llmService, profile),
    new SaveResultsStep()
];

// Crear pipeline
var pipeline = new Pipeline("MiPipeline", steps, loggerFactory)
{
    PipelineTimeout = TimeSpan.FromMinutes(5),
    DefaultStepTimeout = TimeSpan.FromSeconds(30)
};

// Configurar contexto
var context = new PipelineContext
{
    CorrelationId = Guid.NewGuid().ToString(),
    EventChannel = myEventChannel,         // Opcional
    MetricsCollector = metricsCollector    // Opcional
};

// Ejecutar
IStepResult result = await pipeline.ExecuteAsync(
    new InitialInput { Data = "..." }, 
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
│  UserInput (string / LlmStringStepResult)                            │
└─────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│  IntentionAnalyzerStep<TEnum>                                        │
│  ─────────────────────────────                                       │
│  Input:  string (texto del usuario)                                  │
│  Output: LlmStepResult<IntentionInfo<TEnum>>                         │
│                                                                      │
│  IntentionInfo<TEnum> contiene:                                      │
│   ├── Option (TEnum) - La opción seleccionada del enum               │
│   ├── OptimizedPrompt - Prompt reformulado para la opción elegida    │
│   ├── RagKeys - Claves para búsqueda en sistemas RAG                 │
│   ├── RagTags - Tags de filtrado para RAG                            │
│   ├── Confidence (0.0-1.0) - Nivel de confianza                      │
│   ├── Reasoning - Explicación de la decisión                         │
│   └── AdditionalData - Entidades extraídas u otro contexto           │
│                                                                      │
│  BaseLlmStep usa [Description] del Enum para generar las opciones    │
│  disponibles, ya sea en el prompt o como parámetros del LLM.         │
└─────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│  IntentionRouterStep<TEnum>                                          │
│  ────────────────────────────                                        │
│  Input:  LlmStepResult<IntentionInfo<TEnum>>                         │
│  Output: LlmStepResult<IntentionInfo<TEnum>>  ← MISMO QUE ENTRADA    │
│                                                                      │
│    PRINCIPIO CLAVE: El Router es TRANSPARENTE al flujo de datos   │
│      - Solo decide qué ruta tomar basándose en Option                │
│      - NO transforma el dato, lo pasa tal cual al siguiente step     │
│      - Es un "switch" que enruta, no un transformador                │
└─────────────────────────────────────────────────────────────────────┘
                              │
              ┌───────────────┼───────────────┐
              ▼               ▼               ▼
┌────────────────┐  ┌────────────────┐  ┌────────────────┐
│  Ruta: LLM     │  │  Ruta: RAG     │  │  Ruta: Chat    │
├────────────────┤  ├────────────────┤  ├────────────────┤
│ Input: LlmStep │  │ Input: LlmStep │  │ Input: LlmStep │
│ Result<Intent  │  │ Result<Intent  │  │ Result<Intent  │
│ Info<T>>       │  │ Info<T>>       │  │ Info<T>>       │
│                │  │                │  │                │
│ Usa:           │  │ Usa:           │  │ Usa:           │
│ .Value         │  │ .Value         │  │ .Value         │
│ .Optimized     │  │ .RagKeys →     │  │ .Reasoning →   │
│ Prompt         │  │ Buscar en DB   │  │ Formatear      │
│                │  │ → Enriquecer   │  │ respuesta      │
│                │  │ → LLM          │  │                │
└────────────────┘  └────────────────┘  └────────────────┘
```

### Reglas del Flujo de Tipos

1. **IntentionAnalyzerStep<TEnum>**
   - **Entrada**: Texto libre (string o `LlmStringStepResult`)
   - **Salida**: `LlmStepResult<IntentionInfo<TEnum>>`
   - Usa los atributos `[Description]` del enum `TEnum` para generar opciones

2. **IntentionRouterStep<TEnum>**
   - **Entrada**: `LlmStepResult<IntentionInfo<TEnum>>`
   - **Salida**: `LlmStepResult<IntentionInfo<TEnum>>` (IDÉNTICO a la entrada)
   - El Router **NO modifica el dato**, solo selecciona la ruta
   - Cada ruta apunta a un step o sub-pipeline diferente

3. **Primer Step de Cada Ruta**
   - **Entrada**: `LlmStepResult<IntentionInfo<TEnum>>`
   - **Puede ser cualquier tipo de step**, no necesariamente LLM:
     - **LLM Step**: Usa `input.Value.OptimizedPrompt` para el prompt
     - **RAG Step**: Usa `input.Value.RagKeys` para buscar, enriquece contexto
     - **Action Step**: Usa `input.Value.Option` para decidir acción
     - **Conversion Step**: Formatea `input.Value.Reasoning` para respuesta

### Ejemplo de Step Tipado Correctamente

```csharp
// CORRECTO: El step declara el tipo exacto que espera
public class StoryWriterStep : BaseLlmStep<LlmStepResult<IntentionInfo<StoryOption>>, LlmStringStepResult>
{
    protected override Task<string> BuildMessageAsync(
        LlmStepResult<IntentionInfo<StoryOption>> input, 
        StepExecutionContext context)
    {
        // Acceso tipado y seguro al valor
        IntentionInfo<StoryOption>? intention = input.Value;
        
        return Task.FromResult($"""
            Write a story based on:
            {intention?.OptimizedPrompt}
            
            Style: {intention?.AdditionalData?.GetValueOrDefault("style")}
            """);
    }
}
```

```csharp
// INCORRECTO: Usar StepResult base pierde el tipo
public class BadStep : BaseLlmStep<StepResult, LlmStringStepResult>
{
    protected override Task<string> BuildMessageAsync(
        StepResult input,  // ← Perdemos el tipo
        StepExecutionContext context)
    {
        // input.Value es object?, perdemos type safety
        // $"{input.Value}" llama ToString() → nombre del tipo C#
        return Task.FromResult($"{input.Value}"); // ← BUG!
    }
}
```

### Checklist de Implementación

- [ ] `IntentionAnalyzerStep<TEnum>` recibe string, produce `LlmStepResult<IntentionInfo<TEnum>>`
- [ ] `IntentionRouterStep<TEnum>` recibe y produce el **mismo tipo** (transparente)
- [ ] Primer step de cada ruta declara `LlmStepResult<IntentionInfo<TEnum>>` como input
- [ ] Nunca interpolar `$"{result}"` directamente - acceder a `.Value.PropertyName`
- [ ] Los steps no-LLM de ruta también pueden recibir `IntentionInfo<T>` y extraer lo que necesiten

---

## Buenas Prácticas

### Hacer

- **Un tipo público por archivo** (excepto records pequeños relacionados)
- **Preferir `IEnumerable<T>` sobre `List<T>`** cuando solo iteras
- **Usar hooks para observabilidad** en lugar de código inline
- **Validar resultados** con `ValidateAsync()` o `resultValidator`
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
