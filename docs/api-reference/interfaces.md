# Interfaces

## Core Interfaces

### IStep

Base interface for all pipeline steps.

```csharp
public interface IStep
{
    string Name { get; }
    IResume? Resume { get; }
    int MaxRetries => 1;
    int MiillisecondsBetweenRetries => 100;   // spelling is as in the source
    TimeSpan? Timeout => null;
    Type? RequiredContext => null;            // null = any PipelineContext
    Type InputType { get; }
    Type OutputType { get; }

    Task<IStepResult> ExecuteAsync(
        IStepResult input,
        PipelineContext context,
        int attempt,
        IStepResult? lastStepResult,
        CancellationToken cancellationToken);

    Task FinalizeAsync(
        IStepResult input,
        PipelineContext context,
        CancellationToken cancellationToken) => Task.CompletedTask;

    Task<(bool IsValid, string? Error)> ValidateAsync(
        IStepResult result,
        PipelineContext context,
        CancellationToken cancellationToken) => Task.FromResult((true, (string?)null));
}
```

`RequiredContext` is the concrete `PipelineContext` type a step needs (LLM steps return `typeof(LlmPipelineContext)`). `Pipeline.ExecuteAsync` checks it
for the whole step tree (including every case and the default of a declarative `SwitchStep`) before running anything and throws `InvalidOperationException` with a message naming the step; steps reached later through
`NextSteps` or a programmatic `SwitchStep<TIn>` selector are checked just before they run and yield an error result with the same message.

`Resume` is the suspension hook: a step that returns a result with `SuspendInfo` set **must**
expose a non-null `Resume`, otherwise the pipeline throws. `StepBase` returns itself.

**Source:** [IStep.cs](../../AITaskAgent.Abstractions/Abstractions/IStep.cs)

---

### IStep&lt;TIn, TOut&gt;

Strongly-typed step interface.

```csharp
public interface IStep<TIn, TOut> : IStep
    where TIn : IStepResult
    where TOut : IStepResult
{
    Task<TOut> ExecuteAsync(
        TIn input,
        PipelineContext context,
        int attempt,
        TOut? lastStepResult,
        CancellationToken cancellationToken);

    new Type InputType => typeof(TIn);
    new Type OutputType => typeof(TOut);
}
```

---

### IStepResult

Result of step execution.

```csharp
public interface IStepResult
{
    bool HasError => Error != null;
    IStepError? Error { get; set; }
    object? SuspendInfo { get; set; }
    string StepName { get; }
    string StepType { get; }
    object? Value { get; }
    List<IStep> NextSteps { get; }

    Task<(bool IsValid, string? Error)> ValidateAsync(PipelineContext context)
        => Task.FromResult((true, (string?)null));
}

public interface IStepResult<T> : IStepResult
{
    new T? Value { get; }
}
```

Two changes matter for consumers:

- The result no longer carries the `IStep` instance. It carries **`StepName`** and **`StepType`**
  (the step's class name), captured at construction. This is what makes results serializable.
- **`SuspendInfo`** signals that the step paused instead of finishing.

**Source:** [IStepResult.cs](../../AITaskAgent.Abstractions/Abstractions/IStepResult.cs)

---

### IEnrichable

Optional observability hooks. It is no longer a step interface (`IEnrichableStep` is gone) and no
longer derives from `IStep` — enrichment is attached by composition through `StepBase.Enrichable`,
or by overriding the corresponding `protected virtual` hooks on `StepBase`.

```csharp
public interface IEnrichable
{
    void EnrichActivityBefore(Activity? activity, IStepResult input, PipelineContext context) { }
    void EnrichActivityAfter(Activity? activity, IStepResult result, PipelineContext context) { }
    StepStartedEvent EnrichStartedEvent(StepStartedEvent baseEvent, IStepResult input, PipelineContext context);
    IStepCompletedEvent EnrichCompletedEvent(IStepCompletedEvent baseEvent, IStepResult result, PipelineContext context);
}
```

**Source:** [IEnrichable.cs](../../AITaskAgent.Abstractions/Abstractions/IEnrichable.cs)

---

### IPipelineMiddleware

Middleware for pipeline execution.

```csharp
public interface IPipelineMiddleware
{
    Task<IStepResult> InvokeAsync(
        IStep step,
        IStepResult input,
        PipelineContext context,
        Func<CancellationToken, Task<IStepResult>> next,
        CancellationToken cancellationToken);
}
```

**Source:** [IPipelineMiddleware.cs](../../AITaskAgent.Abstractions/Abstractions/IPipelineMiddleware.cs)

---

### IPipeline

```csharp
public interface IPipeline
{
    TimeSpan? DefaultPipelineTimeout { get; }
    TimeSpan? DefaultStepTimeout { get; }

    Task<IStepResult> ExecuteAsync<T>(
        string name,
        IReadOnlyList<IStep> steps,
        T input,
        PipelineContext? context = null,
        TimeSpan? pipelineTimeout = null,
        CancellationToken cancellationToken = default,
        IEnumerable<Func<IProgressEvent, CancellationToken, Task>>? subscribers = null);
}
```

---

### Supporting Core Interfaces

```csharp
public interface IStepError
{
    string Message { get; init; }
    Exception? OriginalException { get; init; }
}

public interface IStepValidator
{
    string Id { get; }
    Task<(bool IsValid, string? Error)> ValidateAsync(
        IStepResult result, PipelineContext context, CancellationToken cancellationToken);
}

public interface IStepActivator
{
    object Create(Type stepType, IServiceProvider? serviceProvider);
}
```

Without a provider `DefaultStepActivator` tries the same constructors with default values only; if none can be called it throws `InvalidOperationException` instead of returning an unconstructed object.

```csharp

public interface IFallbackable
{
    bool SwitchToNextFallback(IStepResult error, PipelineContext context, int attempt);
}

public interface IResume
{
    Task<object?> SaveStateAsync(IStep callingStep);
    Task RestoreStateAsync(IStep step, object? state, object? ResumeResponse);
}

public interface IFactory<T> where T : class
{
    T GetService(string key);
}

public interface IPipelineContextStorage
{
    Task SaveAsync(string correlationId, PipelineContext context, CancellationToken cancellationToken);
    Task<PipelineContext?> LoadAsync(string correlationId, CancellationToken cancellationToken);
    Task DeleteAsync(string correlationId, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
}
```

`IResume` and `IPipelineContextStorage` are covered in
[Human in the loop](../guides/human-in-the-loop.md).

`Pipeline.ExecuteAsync` is the one that uses `IPipelineContextStorage` for suspension: when a run ends suspended it saves the context
(`SaveAsync(correlationId, context, ...)`) before emitting `PipelineCompletedEvent`; without a registered storage it only logs a warning, and if
`SaveAsync` throws the exception propagates. When a resumed run completes without error it calls `DeleteAsync`; a run that failed keeps its
saved context. The implementation registered by `AddAITaskAgent*` is chosen by `AITaskAgent:Storage:Context` (`memory` or `file`; see
[Extensions](extensions.md#configuration-keys)).

`IExtension`, `IExtensionBuilder` and `ExtensionInfo` are described in [Extensions](extensions.md#iextension-and-iextensionbuilder).

---

## LLM Interfaces

### ILlmService

Provider-agnostic LLM service.

```csharp
public interface ILlmService
{
    string Name { get; }

    Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default);

    int EstimateTokenCount(string text);

    int GetMaxContextTokens(string? model = null);
}
```

- `InvokeAsync` returns **`LlmResult`** (a set of candidates plus usage and error state), not a
  single `LlmResponse`.
- `Name` is load-bearing: the configuration binder matches each provider's `ServiceType` against the
  `Name` of the registered `ILlmService` instances.

**Source:** [ILlmService.cs](../../AITaskAgent.Extensions.Llm/Abstractions/ILlmService.cs)

---

### ITool

Tool invocable by LLMs.

```csharp
public interface ITool
{
    string Name { get; }
    string Description { get; }
    string? UsageGuidelines => null;
    bool IsReadOnly => false;

    ToolDefinition GetDefinition();

    Task<string> ExecuteAsync(
        string argumentsJson,
        PipelineContext context,
        string stepName,
        ILogger logger,
        CancellationToken cancellationToken = default);
}
```

`IsReadOnly` defaults to `false` — assume side effects unless the tool says otherwise. Declare it
`true` on pure readers; see [Custom Tools](../extensibility/custom-tools.md).

**Source:** [ITool.cs](../../AITaskAgent.Extensions.Llm/Tools/Abstractions/ITool.cs)

### ITools

The registry contract (formerly `IToolRegistry`).

```csharp
public interface ITools
{
    IReadOnlyList<ITool> GetAllTools(string? moduleName = null);
    ITool? GetTool(string name);
    IReadOnlyList<ITool> GetTools(params string[] names);
    void Register(ITool tool);
}
```

Implemented by `AvailableTools`, which also exposes a process-wide `AvailableTools.Instance`.
Lookups are case-insensitive; `moduleName` filters by the assembly name that declares the tool.

**Source:** [ITools.cs](../../AITaskAgent.Extensions.Llm/Tools/Abstractions/ITools.cs)

---

## Template Interfaces

```csharp
public interface ITemplateRendered
{
    string Render(string template, object obj);
}

public interface ITemplateProvider
{
    string? GetTemplate(string name);
    string? Render(string name, object model);
}
```

`ITemplateRendered` replaces `ITemplateEngine`; the default implementation is
`TinyTemplateRendered` (registered as a singleton), replacing `JsonTemplateEngine`.

---

## Observability Interfaces

### IEventChannel

Async event pub/sub with correlation-based routing.

```csharp
public interface IEventChannel
{
    Task SendAsync<TEvent>(TEvent progressEvent, CancellationToken cancellationToken = default)
        where TEvent : IProgressEvent;

    IDisposable Subscribe(
        Func<IProgressEvent, CancellationToken, Task> handler,
        string? correlationId = null);

    IEventSubscription CreateSubscription(int capacity = 100, string? correlationId = null);
}

public interface IEventSubscription : IAsyncDisposable
{
    ChannelReader<IProgressEvent> Reader { get; }
}
```

There is no generic `Subscribe<TEvent>` and no `Subscribe(int capacity)`. Handlers receive every
event as `IProgressEvent` and filter by type themselves; passing a `correlationId` restricts a
subscription to a single pipeline execution, while `null` makes it global.

**Source:** [IEventChannel.cs](../../AITaskAgent.Abstractions/Observability/IEventChannel.cs)

---

### IProgressEvent

Base interface for events.

```csharp
public interface IProgressEvent
{
    const string CommonLogFormat = "{EventType} from {StepName} | CorrelationId: {CorrelationId}";

    string StepName { get; }
    string EventType { get; }
    DateTimeOffset Timestamp { get; }
    string? CorrelationId { get; }
    bool SuppressFromUser { get; }
    string SchemaVersion { get; }

    void WriteLog(ILogger logger, LogLevel level) =>
        logger.Log(level, CommonLogFormat, EventType, StepName, CorrelationId);
}
```

`SuppressFromUser` events are logged but never routed to subscribers. `SchemaVersion` supports
backward-compatible evolution of the event payloads; the framework emits `"1.0"`.

`WriteLog` is called by `EventChannel` for every event when its log level is not `LogLevel.None`:
the channel picks whether and at which level, the event picks which fields appear. The default
interface implementation writes only the common fields; `ProgressEventBase` redeclares it as
`public virtual` so derived records can override it (`LlmResponseEvent` adds its `Content`). Note
that a default interface member is only reachable through the interface, so a custom event that
implements `IProgressEvent` directly and wants its own log line must declare `WriteLog` itself.

**Source:** [IProgressEvent.cs](../../AITaskAgent.Abstractions/Observability/IProgressEvent.cs)
