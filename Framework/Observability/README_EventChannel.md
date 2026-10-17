# EventChannel - Observabilidad en Tiempo Real

## Propósito

`EventChannel` es el canal de comunicación en tiempo real para eventos de progreso del pipeline. Envía eventos a observers (UI, SSE, WebSocket) y, si `EventLogLevel` no es `None`, los registra además como logs estructurados.

## Características

### 1. **Logging Automático**
Cuando `EventLogLevel != LogLevel.None`, todos los eventos enviados a través de `SendAsync` (también los marcados con `SuppressFromUser`) se registran con `ILogger<EventChannel>` a ese nivel. El canal no conoce los eventos concretos: decide si se registra y a qué nivel, y llama a `IProgressEvent.WriteLog(ILogger, LogLevel)`; cada evento decide qué campos escribe.

```csharp
await eventChannel.SendAsync(new LlmResponseEvent {
    StepName = "ChatAgent",
    Content = "Hola",
    CorrelationId = "abc-123"
});

// Genera el log (LlmResponseEvent sobrescribe WriteLog e incluye Content):
// llm.response from ChatAgent | CorrelationId: abc-123 | IsThinking: False | FinishReason: Streaming | Content: Hola | Tokens: 0
```

El resto de eventos usan la implementación por defecto (`IProgressEvent.CommonLogFormat`), que solo escribe los campos comunes:

```
step.started from ChatAgent | CorrelationId: abc-123
```

Para que un evento propio registre sus campos, sobrescribe `WriteLog` (es `virtual` en `ProgressEventBase`):

```csharp
public sealed record MyEvent : ProgressEventBase
{
    public override string EventType => "my.event";
    public int Items { get; init; }

    public override void WriteLog(ILogger logger, LogLevel level) =>
        logger.Log(level, "{EventType} from {StepName} | CorrelationId: {CorrelationId} | Items: {Items}",
            EventType, StepName, CorrelationId, Items);
}
```

### 2. **Nivel de Log Configurable**

Con `AddAITaskAgent()`, el `IEventChannel` singleton toma el nivel de `AITaskAgent:Observability:EventLogLevel` (`ObservabilityOptions`, por defecto `Debug`) en `appsettings.json`:

```json
{
  "AITaskAgent": {
    "Observability": {
      "EventLogLevel": "Debug"  // Trace, Debug, Information, Warning, None
    }
  }
}
```

`None` desactiva el registro (los eventos siguen llegando a los suscriptores). Si construyes el canal a mano, `new EventChannel(eventLogLevel, logger)` usa `LogLevel.Information` por defecto. `EventChannelOptions.EventLogLevel` vale `None` por defecto, pero el canal no la recibe de configuración: la rellena con el parámetro del constructor.

### 3. **Filtrado en Serilog**

Configura nivel específico para EventChannel:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "AITaskAgent.Observability.EventChannel": "Debug"
      }
    }
  }
}
```

### 4. **Observer Pattern**

Suscribe observers para recibir eventos en tiempo real:

```csharp
using var subscription = eventChannel.Subscribe(async (evt, ct) => {
    if (evt is LlmResponseEvent llmEvt)
    {
        await sseStream.WriteAsync(llmEvt.Content);
    }
}, correlationId: null); // null = recibe todos los eventos
```

Para SSE/WebSocket existe también `CreateSubscription(capacity, correlationId)`, que expone un `ChannelReader` directamente.

## Tipos de Eventos

- `PipelineStartedEvent` / `PipelineCompletedEvent` - Ciclo de vida del pipeline completo
- `PipelineSuspendedEvent` / `PipelineResumingEvent` / `PipelineResumedEvent` - Suspensión y reanudación
- `StepStartedEvent` / `StepCompletedEvent` - Ciclo de vida de cada paso del pipeline
- `StepRoutingEvent`, `StepValidationEvent`, `StepTimeoutEvent`, `StepCachedHitEvent`, `LoopExhaustedEvent` - Enrutado, validación, timeout, caché y bucles agotados
- `BranchStartedEvent` / `BranchCompletedEvent` - Ramas paralelas
- `LlmResponseEvent` - Chunks de respuesta de texto/pensamiento del LLM
- `TagStartedEvent` / `TagCompletedEvent` - Tags detectados en el streaming
- `ToolStartedEvent` - Inicio de la ejecución de una herramienta (MCP u otra)
- `ToolCompletedEvent` - Fin de la ejecución de una herramienta (con éxito/error y duración)
- `StepProgressEvent` - Progreso customizado emitido por pasos individuales

## Replay de Sesiones

Con el registro activado (`EventLogLevel != None`), filtra logs por el `SourceContext` del canal para reproducir lo que vio el usuario (con `LlmResponseEvent` se registra también `Content`):

```sql
SELECT * FROM Logs 
WHERE SourceContext = 'AITaskAgent.Observability.EventChannel'
AND CorrelationId = 'abc-123'
ORDER BY Timestamp
```

## Separación de Responsabilidades

| Mecanismo | Propósito | Audiencia |
|-----------|-----------|-----------|
| **EventChannel** | Tiempo real + Replay | Usuario + Auditoría |
| **ILogger** | Debugging | Desarrolladores |
| **OpenTelemetry** | Performance | Ops/SRE |
| **Métricas** | Dashboards | Business |
