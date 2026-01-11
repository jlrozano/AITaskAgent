# Interfaces

## Core Interfaces

### IStep

Base interface for all pipeline steps.

```csharp
public interface IStep
{
    string Name { get; }
    int MaxRetries => 1;
    int MillisecondsBetweenRetries => 100;
    TimeSpan? Timeout => TimeSpan.FromMinutes(1);
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

**Source:** [IStep.cs](file:///c:/Users/juan.rozano/Desktop/AgenteAI/AITaskAgentFramework/Framework/Core/Abstractions/IStep.cs)

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
    IStep Step { get; }
    object? Value { get; }
    List<IStep> NextSteps { get; }
    
    Task<(bool IsValid, string? Error)> ValidateAsync();
}
```

**Source:** [IStepResult.cs](file:///c:/Users/juan.rozano/Desktop/AgenteAI/AITaskAgentFramework/Framework/Core/Abstractions/IStepResult.cs)

---

### IEnrichableStep

Optional interface for observability hooks.

```csharp
public interface IEnrichableStep : IStep
{
    void EnrichActivityBefore(Activity? activity, IStepResult input, PipelineContext context);
    void EnrichActivityAfter(Activity? activity, IStepResult result, PipelineContext context);
    StepStartedEvent EnrichStartedEvent(StepStartedEvent baseEvent, IStepResult input, PipelineContext context);
    IStepCompletedEvent EnrichCompletedEvent(IStepCompletedEvent baseEvent, IStepResult result, PipelineContext context);
}
```

**Source:** [IEnrichableStep.cs](file:///c:/Users/juan.rozano/Desktop/AgenteAI/AITaskAgentFramework/Framework/Core/Abstractions/IEnrichableStep.cs)

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

**Source:** [IPipelineMiddleware.cs](file:///c:/Users/juan.rozano/Desktop/AgenteAI/AITaskAgentFramework/Framework/Core/Abstractions/IPipelineMiddleware.cs)

---

## LLM Interfaces

### ILlmService

Provider-agnostic LLM service.

```csharp
public interface ILlmService
{
    Task<LlmResponse> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default);
    int EstimateTokenCount(string text);
    int GetMaxContextTokens(string? model = null);
}
```

**Source:** [ILlmService.cs](file:///c:/Users/juan.rozano/Desktop/AgenteAI/AITaskAgentFramework/Framework/LLM/Abstractions/ILlmService.cs)

---

### ITool

Tool invocable by LLMs.

```csharp
public interface ITool
{
    string Name { get; }
    string Description { get; }
    ToolDefinition GetDefinition();
    
    Task<string> ExecuteAsync(
        string argumentsJson,
        PipelineContext context,
        string stepName,
        ILogger logger,
        CancellationToken cancellationToken = default);
}
```

**Source:** [ITool.cs](file:///c:/Users/juan.rozano/Desktop/AgenteAI/AITaskAgentFramework/Framework/LLM/Tools/Abstractions/ITool.cs)

---

## Observability Interfaces

### IEventChannel

Async event pub/sub.

```csharp
public interface IEventChannel
{
    Task SendAsync<TEvent>(TEvent progressEvent, CancellationToken cancellationToken = default)
        where TEvent : IProgressEvent;
}
```

**Source:** [IEventChannel.cs](file:///c:/Users/juan.rozano/Desktop/AgenteAI/AITaskAgentFramework/Framework/Observability/IEventChannel.cs)

---

### IProgressEvent

Base interface for events.

```csharp
public interface IProgressEvent
{
    string StepName { get; }
    string EventType { get; }
    DateTimeOffset Timestamp { get; }
    string? CorrelationId { get; }
}
```

**Source:** [IProgressEvent.cs](file:///c:/Users/juan.rozano/Desktop/AgenteAI/AITaskAgentFramework/Framework/Observability/IProgressEvent.cs)
