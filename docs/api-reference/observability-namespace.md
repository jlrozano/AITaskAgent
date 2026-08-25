# Observability Namespace

`AITaskAgent.Observability.*`

## EventChannel

Default implementation of `IEventChannel`. Subscriptions are routed by **`CorrelationId`**: a
subscription created with a correlation id only receives events from that pipeline execution, while
`null` (the default) makes it global.

```csharp
public sealed class EventChannel : IEventChannel, IAsyncDisposable
{
    public EventChannel(
        LogLevel eventLogLevel = LogLevel.Information,
        ILogger<EventChannel>? logger = null);

    public Task SendAsync<TEvent>(TEvent progressEvent, CancellationToken cancellationToken = default)
        where TEvent : IProgressEvent;

    public IDisposable Subscribe(
        Func<IProgressEvent, CancellationToken, Task> handler,
        string? correlationId = null);

    public IEventSubscription CreateSubscription(int capacity = 100, string? correlationId = null);
}
```

The old generic `Subscribe<TEvent>(Func<TEvent, Task>)` and the capacity-only
`Subscribe(int capacity)` are gone. Two forms remain:

```csharp
// 1. Callback form — the handler runs on a background loop fed by a bounded channel.
using var subscription = eventChannel.Subscribe(async (evt, ct) =>
{
    if (evt is LlmResponseEvent llm && llm.FinishReason == FinishReason.Streaming)
    {
        Console.Write(llm.Content);
    }
});

// 2. Reader form — for SSE/WebSocket, exposes the ChannelReader directly.
await using var reader = eventChannel.CreateSubscription(capacity: 500, correlationId: context.CorrelationId);
await foreach (var evt in reader.Reader.ReadAllAsync(cancellationToken))
{
    // ...
}
```

Handlers always receive `IProgressEvent`; filter by type yourself. Events with
`SuppressFromUser = true` are logged but never delivered to subscribers.

```csharp
public interface IEventSubscription : IAsyncDisposable
{
    ChannelReader<IProgressEvent> Reader { get; }
}
```

`EventChannelOptions` controls the automatic logging level (`EventLogLevel`, default `None`) and
the buffer size (`ChannelCapacity`, default 1000).

**Source:** [EventChannel.cs](../../Framework/Observability/EventChannel.cs)

---

## Base Classes

### ProgressEventBase

Base record for all events, implementing `IProgressEvent`.

```csharp
public abstract record ProgressEventBase : IProgressEvent
{
    public string StepName { get; init; } = string.Empty;
    public abstract string EventType { get; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? CorrelationId { get; init; }
    public bool SuppressFromUser { get; init; }   // controls UI visibility
    public string SchemaVersion { get; init; } = "1.0";
}
```

`StepName` is not `required`; it defaults to an empty string. `SchemaVersion` is new and exists so
event payloads can evolve without breaking consumers.

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

#### PipelineSuspendedEvent / PipelineResumingEvent / PipelineResumedEvent

```csharp
public sealed record PipelineSuspendedEvent : ProgressEventBase
{
    public override string EventType => "pipeline.suspended";
    public required string TargetStepPath { get; init; }
    public bool IsBranchSuspension { get; init; }
    public string? BranchName { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record PipelineResumingEvent : ProgressEventBase
{
    public const string EventTypeName = "pipeline.resuming";
    public override string EventType => EventTypeName;
    public string? StepPath { get; init; }
    public string? TargetStepPath { get; init; }
}

public sealed record PipelineResumedEvent : ProgressEventBase
{
    public const string EventTypeName = "pipeline.resumed";
    public override string EventType => EventTypeName;
    public string? StepPath { get; init; }
    public string? TargetStepPath { get; init; }
}
```

`PipelineSuspendedEvent` is emitted by the pipeline when a step returns a result with
`SuspendInfo` set. `PipelineResumingEvent` and `PipelineResumedEvent` are declared for hosts that
drive the resume cycle themselves — the framework does not emit them. See
[Human in the loop](../guides/human-in-the-loop.md) for the flow itself.

---

### Step Events

#### StepStartedEvent

```csharp
public sealed record StepStartedEvent : ProgressEventBase
{
    public override string EventType => "step.started";
    public string? StepPath { get; init; }
    public int AttemptNumber { get; init; } = 1;
    public Dictionary<string, object>? AdditionalData { get; init; }
}
```

`RetryMiddleware` emits one of these per retry (attempts 2 and up) because it calls the step
directly rather than going through `ObservabilityMiddleware` again.

#### StepCompletedEvent

```csharp
public sealed record StepCompletedEvent : ProgressEventBase, IStepCompletedEvent
{
    public override string EventType => "step.completed";
    public string? StepPath { get; init; }
    public bool Success { get; init; }
    public TimeSpan Duration { get; init; }
    public string? ErrorMessage { get; init; }
    public Dictionary<string, object?> AdditionalData { get; init; } = [];
    public IStepResult? StepResult { get; init; }
}

public interface IStepCompletedEvent : IProgressEvent
{
    Dictionary<string, object?> AdditionalData => [];
    TimeSpan Duration { get; init; }
    string? ErrorMessage { get; init; }
    IStepResult? StepResult { get; init; }
}
```

#### StepProgressEvent

Implements `IProgressEvent` directly rather than deriving from `ProgressEventBase`, so `EventType`
is a settable `required` property.

```csharp
public sealed record StepProgressEvent : IProgressEvent
{
    public required string StepName { get; init; }
    public required string EventType { get; init; }
    public required string Message { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? CorrelationId { get; init; }
    public string SchemaVersion { get; init; } = "1.0";
    public bool SuppressFromUser { get; init; }
    public int? CurrentProgress { get; init; }
    public int? TotalProgress { get; init; }
    public Dictionary<string, object?>? Metadata { get; init; }
}
```

#### StepRoutingEvent

```csharp
public sealed record StepRoutingEvent : ProgressEventBase
{
    public override string EventType => "step.routing";
    public required string SelectedRoute { get; init; }
    public required string RoutingReason { get; init; }
}
```

#### StepValidationEvent

```csharp
public sealed record StepValidationEvent : ProgressEventBase
{
    public override string EventType => "step.validation";
    public bool IsValid { get; init; }
    public string? StepPath { get; init; }
    public string? ValidationError { get; init; }
    public string ValidationType { get; init; } = "Structural";   // or "Semantic"
    public int AttemptNumber { get; init; } = 1;
}
```

The error property is `ValidationError` and the attempt counter is `AttemptNumber`.
`ValidationType` values come from `AITaskAgentConstants.ValidationTypes`.

#### StepTimeoutEvent

```csharp
public sealed record StepTimeoutEvent : ProgressEventBase
{
    public override string EventType => "step.timeout";
    public string? StepPath { get; init; }
    public TimeSpan ConfiguredTimeout { get; init; }
}
```

#### StepCachedHitEvent

Declared for hosts that want to report a step served from `PipelineContext.StepResults` instead of
executing. The framework logs that case but does not currently emit this event.

```csharp
public sealed record StepCachedHitEvent : ProgressEventBase
{
    public override string EventType => "step.cached_hit";
    public string? StepPath { get; init; }
}
```

---

### Branch and Loop Events

#### BranchStartedEvent / BranchCompletedEvent

Emitted by `ParallelStep` around each branch, on the branch's cloned context.

```csharp
public sealed record BranchStartedEvent : ProgressEventBase
{
    public const string EventTypeName = "branch.started";
    public override string EventType => EventTypeName;
    public string? StepPath { get; init; }
    public string BranchName { get; init; } = string.Empty;
    public string BranchId { get; init; } = string.Empty;
}

public sealed record BranchCompletedEvent : ProgressEventBase
{
    public const string EventTypeName = "branch.completed";
    public override string EventType => EventTypeName;
    public string? StepPath { get; init; }
    public string BranchName { get; init; } = string.Empty;
    public string BranchId { get; init; } = string.Empty;
    public bool Success { get; init; }
    public TimeSpan Duration { get; init; }
    public string? ErrorMessage { get; init; }
}
```

#### LoopExhaustedEvent

Emitted when a `LoopStep` reaches `MaxIterations` without a `BreakStepResult`.

```csharp
public sealed record LoopExhaustedEvent : ProgressEventBase
{
    public override string EventType => "loop.exhausted";
    public string LoopName { get; init; } = string.Empty;
    public int IterationsCompleted { get; init; }
    public int MaxIterations { get; init; }
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
    public FinishReason FinishReason { get; init; } = FinishReason.Streaming;
    public int ChoiceIndex { get; init; }
    public string? RawFinishReason { get; init; }
    public bool IsThinking { get; init; }
    public int TokensUsed { get; init; }
    public string? Model { get; init; }
    public string? Provider { get; init; }
    public List<ToolCall>? ToolCalls { get; init; }
}
```

`FinishReason.Streaming` marks an incremental chunk; `IsThinking` marks reasoning traces, and
`ChoiceIndex` identifies the candidate when the model returns several.

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
    public Dictionary<string, object>? AdditionalData { get; init; }
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

Always present, in this order, after any user-registered middleware:

| Middleware | Purpose |
|------------|---------|
| `ObservabilityMiddleware` | Traces, metrics, `step.started` / `step.completed` |
| `TimeoutMiddleware` | Step timeout enforcement, emits `step.timeout` |
| `RetryMiddleware` | Retry loop with structural and semantic validation |

`RetryMiddleware` is **terminal**: it calls `step.ExecuteAsync` directly instead of `next()`, so its
retry loop runs inside the single `next()` call made by the middlewares above it. That is why it
emits its own `StepStartedEvent` for attempts after the first.

---

## Event Type Constants

`AITaskAgent.Core.AITaskAgentConstants.EventTypes` centralises the well-known strings:
`pipeline.started`, `pipeline.completed`, `pipeline.suspended`, `step.started`, `step.completed`,
`step.validation`, `step.routing`, `tool.started`, `tool.completed`, `llm.response`, `tag.started`,
`tag.completed`, `loop.exhausted`.

`branch.started`, `branch.completed`, `pipeline.resuming`, `pipeline.resumed`, `step.timeout` and
`step.cached_hit` are declared on their own event records (as `EventTypeName` where applicable).

---

## Telemetry Tags

```csharp
public static class AITaskAgentConstants.TelemetryTags
{
    public const string PipelineName      = "pipeline.name";
    public const string StepName          = "step.name";
    public const string StepType          = "step.type";
    public const string StepDurationMs    = "step.duration_ms";
    public const string StepSuccess       = "step.success";
    public const string CorrelationId     = "correlation.id";
    public const string Attempt           = "step.attempt";
    public const string MaxRetries        = "step.max_retries";
    public const string LlmModel          = "llm.model";
    public const string LlmProvider       = "llm.provider";
    public const string TokensUsed        = "llm.tokens_used";
    public const string ToolName          = "tool.name";
    public const string ToolCount         = "tool.count";
    public const string PipelineStepCount = "pipeline.step_count";
    public const string PipelineTimeoutMs = "pipeline.timeout_ms";

    public const string ActivitySourceName    = "AITaskAgent";
    public const string ActivitySourceVersion = "1.0.0";
    public const string MeterName             = "AITaskAgent.Pipeline";
    public const string MeterVersion          = "1.0.0";
}
```

Note `correlation.id`, not `correlation_id`.

**Source:** [AITaskAgentConstants.TelemetryTags.cs](../../Framework/Core/Constants/AITaskAgentConstants.TelemetryTags.cs)
