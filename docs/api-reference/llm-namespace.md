# LLM Namespace

`AITaskAgent.LLM.*`

## BaseLlmStep

Base class for LLM-powered steps.

```csharp
public class BaseLlmStep<TIn, TOut> : TypedStep<TIn, TOut>
    where TIn : IStepResult
    where TOut : ILlmStepResult
{
    public BaseLlmStep(
        ILlmService llmService,
        string name,
        LlmProviderConfig profile,
        Func<TIn, PipelineContext, Task<string>> messageBuilder,
        Func<TIn, PipelineContext, Task<string>>? systemMessageBuilder = null,
        List<ITool>? tools = null,
        Func<TOut, Task<(bool IsValid, string? Error)>>? resultValidator = null);
    
    protected int MaxToolIterations { get; init; } = 5;
    
    protected virtual ConversationContext GetConversationContext(PipelineContext context);
    protected virtual LlmRequest ConfigureLlmRequest(LlmRequest request, PipelineContext context);
    protected virtual Task<(TOut? Result, string? Error)> ParseLlmResponseAsync(LlmResponse response, PipelineContext context);
}
```

**Source:** [BaseLlmStep.cs](file:///c:/Users/juan.rozano/Desktop/AgenteAI/AITaskAgentFramework/Framework/LLM/Steps/BaseLlmStep.cs)

---

## Models

### LlmRequest

```csharp
public sealed record LlmRequest
{
    public required ConversationContext Conversation { get; init; }
    public required LlmProviderConfig Profile { get; init; }
    public string? SystemPrompt { get; init; }
    public float? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public float? TopP { get; init; }
    public int? TopK { get; init; }
    public float? FrequencyPenalty { get; init; }
    public float? PresencePenalty { get; init; }
    public List<ToolDefinition>? Tools { get; init; }
    public bool UseStreaming { get; init; }
    public ResponseFormatOptions? ResponseFormat { get; init; }
}
```

**Source:** [LlmRequest.cs](file:///c:/Users/juan.rozano/Desktop/AgenteAI/AITaskAgentFramework/Framework/LLM/Models/LlmRequest.cs)

### LlmResponse

```csharp
public sealed class LlmResponse
{
    public required string Content { get; init; }
    public int? TokensUsed { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public decimal? CostUsd { get; init; }
    public string? FinishReason { get; init; }
    public List<ToolCall>? ToolCalls { get; init; }
    public string? Model { get; init; }
}
```

**Source:** [LlmResponse.cs](file:///c:/Users/juan.rozano/Desktop/AgenteAI/AITaskAgentFramework/Framework/LLM/Models/LlmResponse.cs)

### ToolCall

```csharp
public record ToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Arguments { get; init; }
}
```

---

## ConversationContext

```csharp
public sealed class ConversationContext
{
    public ConversationContext(int maxTokens = 4000, Func<string, int>? tokenCounter = null);
    
    public string ConversationId { get; init; }
    public MessageHistory History { get; }
    public Dictionary<string, object?> Metadata { get; init; }
    
    public ConversationContext AddUserMessage(string content);
    public ConversationContext AddAssistantMessage(string content);
    public ConversationContext AddSystemMessage(string content);
    public ConversationContext AddToolMessage(string toolCallId, string content);
    public ConversationContext AddAssistantMessageWithToolCalls(IEnumerable<ToolCall> toolCalls);
    
    public string CreateBookmark();
    public void RestoreBookmark(string bookmark);
    public List<Message> GetMessagesForRequest(int? maxTokens = null, string? fromBookmark = null, bool useSlidingWindow = true);
    public ConversationContext Clone();
}
```

**Source:** [ConversationContext.cs](file:///c:/Users/juan.rozano/Desktop/AgenteAI/AITaskAgentFramework/Framework/LLM/Conversation/Context/ConversationContext.cs)

---

## LLM Steps

| Class | Description |
|-------|-------------|
| `LlmStep<TIn, TOut>` | Simple LLM step |
| `TemplateLlmStep<TIn, TOut>` | Template-based prompts |
| `StatelessTemplateLlmStep<TIn, TOut>` | No conversation history |
| `IntentionAnalyzerStep` | Intent detection |
| `IntentionRouterStep` | Route by intent |

---

## Configuration

### LlmProviderConfig

```csharp
public class LlmProviderConfig
{
    public string Provider { get; set; }
    public string Model { get; set; }
    public float? Temperature { get; set; }
    public int? MaxTokens { get; set; }
    public float? TopP { get; set; }
    public int? TopK { get; set; }
    public float? FrequencyPenalty { get; set; }
    public float? PresencePenalty { get; set; }
    public JsonResponseCapability JsonCapability { get; set; }
}
```
