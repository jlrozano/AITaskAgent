# Observability Namespace

`AITaskAgent.Observability.*`

## EventChannel

Default implementation of `IEventChannel`.

```csharp
public class EventChannel : IEventChannel
{
    public Task SendAsync<TEvent>(TEvent progressEvent, CancellationToken ct = default)
        where TEvent : IProgressEvent;
    
    public IEventSubscription Subscribe<TEvent>(Func<TEvent, Task> handler)
        where TEvent : IProgressEvent;
}
```

**Source:** [EventChannel.cs](file:///c:/Users/juan.rozano/Desktop/AgenteAI/AITaskAgentFramework/Framework/Observability/EventChannel.cs)

---

## Base Classes

### ProgressEventBase

Base record for all events, implementing `IProgressEvent`.

```csharp
public abstract record ProgressEventBase : IProgressEvent
{
    public required string StepName { get; init; }
    public abstract string EventType { get; }
    public DateTimeOffset Timestamp { get; init; }
    public string? CorrelationId { get; init; }
    public bool SuppressFromUser { get; init; } // Controls UI visibility
}
```

---

## Event Types

### Pipeline Events

#### PipelineStartedEvent

```csharp
public sealed record PipelineStartedEvent : ProgressEventBase
{
    public override string EventType => "pipeline.started";
    public required string PipelineName { get; init; }
    public int TotalSteps { get; init; }
}
```

#### PipelineCompletedEvent

```csharp
public sealed record PipelineCompletedEvent : ProgressEventBase
{
    public override string EventType => "pipeline.completed";
    public required string PipelineName { get; init; }
    public bool Success { get; init; }
    public TimeSpan Duration { get; init; }
    public string? ErrorMessage { get; init; }
}
```

---

### Step Events

#### StepStartedEvent

```csharp
public sealed record StepStartedEvent : ProgressEventBase
{
    public override string EventType => "step.started";
}
```

#### StepCompletedEvent

```csharp
public sealed record StepCompletedEvent : ProgressEventBase, IStepCompletedEvent
{
    public override string EventType => "step.completed";
    public bool Success { get; init; }
    public TimeSpan Duration { get; init; }
    public string? ErrorMessage { get; init; }
    public Dictionary<string, object?>? AdditionalData { get; set; }
}
```

#### StepProgressEvent

```csharp
public sealed record StepProgressEvent : ProgressEventBase
{
    public override string EventType => "step.progress";
    public required string Message { get; init; }
    public double? PercentComplete { get; init; }
}
```

#### StepRoutingEvent

```csharp
public sealed record StepRoutingEvent : ProgressEventBase
{
    public override string EventType => "step.routing";
    public required string SelectedRoute { get; init; }
    public string? RoutingReason { get; init; }
}
```

#### StepValidationEvent

```csharp
public sealed record StepValidationEvent : ProgressEventBase
{
    public override string EventType => "step.validation";
    public bool IsValid { get; init; }
    public string? Error { get; init; }
    public int Attempt { get; init; }
}
```

---

### LLM Events

#### LlmResponseEvent

```csharp
public sealed record LlmResponseEvent : ProgressEventBase
{
    public override string EventType => "llm.response";
    public required string Content { get; init; }
    public FinishReason FinishReason { get; init; }  // Streaming = chunk
    public string? RawFinishReason { get; init; }
    public int TokensUsed { get; init; }
    public string? Model { get; init; }
    public string? Provider { get; init; }
}
```

---

### Tool Events

#### ToolStartedEvent

```csharp
public sealed record ToolStartedEvent : ProgressEventBase
{
    public override string EventType => "tool.started";
    public required string ToolName { get; init; }
    public Dictionary<string, object>? AdditionalData { get; init; }
}
```

#### ToolCompletedEvent

```csharp
public sealed record ToolCompletedEvent : ProgressEventBase
{
    public override string EventType => "tool.completed";
    public required string ToolName { get; init; }
    public bool Success { get; init; }
    public TimeSpan Duration { get; init; }
    public string? ErrorMessage { get; init; }
}
```

---

### Artifact Generation Events (Streaming)

> [!NOTE]
> Unlike standard Tools (`ITool`), Streaming Tags (Artifacts) represent side-effects generated directly during the LLM streaming process (e.g., writing a file). They **do not** create a new turn in the conversation history and are "fire-and-forget" from the Agent's cognitive perspective. These events provide observability for those side-effects.

### System Prompt Injection

When Streaming Tags are enabled, the Framework automatically enriches the System Prompt to instruct the LLM on how to generate these artifacts. This ensures the model uses the correct XML syntax without manual user prompting.

**Injected Instruction Example:**
```markdown
## Available Streaming Actions
You can perform the following actions inline:

- **Create files**: Use <write_file path="relative/path.md">content</write_file>

IMPORTANT:
- Content inside tags will NOT appear in conversation history
- Only a file reference will be added: [File: path/to/file.md]
- If you need to verify the content later, use view_file tool
```

#### TagStartedEvent

```csharp
public sealed record TagStartedEvent : ProgressEventBase
{
    public override string EventType => "tag.started";
    /// <summary>
    /// Name of the artifact being generated (e.g., "write_file", "svg_diagram").
    /// </summary>
    public required string TagName { get; init; }
    public Dictionary<string, string>? Attributes { get; init; }
    public Dictionary<string, object>? AdditionalData { get; init; }
}
```

#### TagCompletedEvent

```csharp
public sealed record TagCompletedEvent : ProgressEventBase
{
    public override string EventType => "tag.completed";
    public required string TagName { get; init; }
    public bool Success { get; init; } = true;
    public TimeSpan Duration { get; init; }
    public string? ErrorMessage { get; init; }
    public Dictionary<string, object>? AdditionalData { get; init; }
}
```

---

## Internal Middlewares

| Middleware | Purpose |
|------------|---------|
| `ObservabilityMiddleware` | Traces, metrics, events |
| `TimeoutMiddleware` | Step timeout enforcement |
| `RetryMiddleware` | Retry with validation |

---

## Telemetry Tags

```csharp
public static class AITaskAgentConstants.TelemetryTags
{
    public const string PipelineName = "pipeline.name";
    public const string StepName = "step.name";
    public const string StepType = "step.type";
    public const string StepSuccess = "step.success";
    public const string StepDurationMs = "step.duration_ms";
    public const string CorrelationId = "correlation_id";
    public const string LlmModel = "llm.model";
    public const string LlmProvider = "llm.provider";
    public const string TokensUsed = "llm.tokens_used";
}
```
