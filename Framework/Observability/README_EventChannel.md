# EventChannel - Observabilidad en Tiempo Real

## Propósito

`EventChannel` es el canal de comunicación en tiempo real para eventos de progreso del pipeline. Envía eventos a observers (UI, SSE, WebSocket) y **automáticamente** los registra como logs estructurados.

## Características

### 1. **Logging Automático**
Todos los eventos enviados a través de `SendAsync` se registran automáticamente con `ILogger<EventChannel>`:

```csharp
await eventChannel.SendAsync(new LlmResponseEvent {
    StepName = "ChatAgent",
    Delta = "Hola",
    EventType = "llm.response"
});

// Automáticamente genera log:
// [EVENT] llm.response from ChatAgent | CorrelationId: abc-123
```

### 2. **Nivel de Log Configurable**

Configura el nivel de log en `appsettings.json`:

```json
{
  "EventChannel": {
    "EventLogLevel": "Debug"  // Trace, Debug, Information, Warning, None
  }
}
```

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
eventChannel.Subscribe(async (evt) => {
    if (evt is LlmResponseEvent llmEvt)
    {
        await sseStream.WriteAsync(llmEvt.Delta);
    }
});
```

## Tipos de Eventos

- `PipelineStartedEvent` / `PipelineCompletedEvent`
- `StepStartedEvent` / `StepCompletedEvent`
- `LlmResponseEvent` - Chunks de respuesta LLM
- `ToolExecutionEvent` - Ejecución de herramientas
- `StepProgressEvent` - Progreso custom

## Replay de Sesiones

Filtra logs por `SourceContext = "EventChannel"` para reproducir exactamente lo que vio el usuario:

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
