# LLM Namespace

`AITaskAgent.LLM.*`

> From v2 this code lives in the `AITaskAgent.Extensions.Llm` assembly, which depends on `AITaskAgent.Core` and is
> registered with `LlmExtension` (`AddAITaskAgentLlm()`, or `AddAITaskAgent()` for core plus LLM). Namespaces did not change.
> The conversation is no longer part of the base `PipelineContext`: it lives in `LlmPipelineContext` (below).

## Configuration model: provider × role

There is no `LlmProviderConfig`, no `LlmProvidersConfig` and no `ILlmProviderResolver`. LLM
configuration is split along two axes:

- **`LlmProvider`** — *where* to call: connection details plus the catalogue of models available
  there, with their prices.
- **`LlmRole`** — *how* a step should behave: which model of which provider, the system prompt,
  inference overrides and a fallback chain.

`LlmEnvironment` is the root object, bound from `AITaskAgent:LlmProviders` and read via
`IOptions<LlmEnvironment>`.

### LlmEnvironment

```csharp
public sealed class LlmEnvironment
{
    public Dictionary<string, LlmProvider> Providers { get; init; }  // case-insensitive keys
    public Dictionary<string, LlmRole> Roles { get; init; }          // case-insensitive keys
    public string DefaultRole { get; set; } = "default";
    public List<FallbackProviderSettings> FallbackProviders { get; init; } = [];
}

public class FallbackProviderSettings
{
    public string Provider { get; init; } = string.Empty;   // must be a key in Providers
    public string Model { get; init; } = string.Empty;
    public LlmInferenceSettings? Inference { get; init; }
}
```

**Source:** [LlmEnvironment.cs](../../AITaskAgent.Extensions.Llm/Configuration/LlmEnvironment.cs)

### LlmProvider

```csharp
public class LlmProvider
{
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string? ApiVersion { get; init; }
    public LlmInferenceSettings? Inference { get; init; }
    public List<LlmModelSettings> Models { get; init; } = [];
    public required ILlmService Service { get; init; }

    public decimal? CalculateCost(string? model, int? promptTokens, int? completionTokens, int? cachedPromptTokens = null);
}

public record LlmModelSettings(
    string Name,
    decimal? InputPricePerMillion,   // null = unknown: CalculateCost returns null
    decimal? OutputPricePerMillion,
    LlmInferenceSettings Inference,
    decimal? CachedInputPricePerMillion = null);   // null = cached tokens billed at InputPricePerMillion
```

Pricing lives on the provider, not in the `ILlmService`: every provider prices the same way and no
service ever receives the model catalogue. `BaseLlmStep` calls `CalculateCost` after each
invocation and writes the value into `LlmResult.CostUsd`. The model is looked up
case-insensitively, first by the model the service reports and then by the requested one; cached
prompt tokens are clamped to the prompt tokens and billed at `CachedInputPricePerMillion`.

`Service` is resolved at binding time by matching the JSON `ServiceType` (defaulting to the
provider key) against the `Name` of each registered `ILlmService`.

**Source:** [LlmProvider.cs](../../AITaskAgent.Extensions.Llm/Configuration/LlmProvider.cs)

### LlmRole

```csharp
public class LlmRole
{
    public required LlmProvider Provider { get; init; }
    public required string Model { get; init; }
    public string? SystemPrompt { get; init; }
    public LlmInferenceSettings? Inference { get; init; }
    public IList<LlmFallback>? Fallbacks { get; init; }
    public IList<ITool>? Tools { get; init; }
}

public record LlmFallback(
    string Model,
    LlmProvider Provider,
    LlmInferenceSettings? Inference = null);
```

A role's `Fallbacks` default to `LlmEnvironment.FallbackProviders` when the role declares none.
`BaseLlmStep` walks the chain through `IFallbackable.SwitchToNextFallback`, rewinding the
conversation to its initial bookmark on each switch. It only switches when the error's
`OriginalException` is an `LlmProviderException`, and refuses a fallback whose `Model` or
`Provider.Service` did not resolve.

**Source:** [LlmRole.cs](../../AITaskAgent.Extensions.Llm/Configuration/LlmRole.cs)

### LlmInferenceSettings

A strongly-typed facade over a raw `JObject`. The typed properties are the parameters every vendor
understands; anything vendor-specific travels in `Options` and is read back with `GetOption<T>`.

```csharp
public record LlmInferenceSettings
{
    public JObject Options { get; init; } = [];   // single source of truth

    public float? Temperature { get; init; }
    public float? TopP { get; init; }
    public int? MaxTokens { get; init; }
    public bool UseStreaming { get; init; }       // default false
    public bool JsonSupport { get; init; }        // default false
    public bool ToolSupport { get; init; }        // default true
    public IList<string>? Stop { get; init; }
    public bool UseMaxCompletionTokens { get; init; }          // default false
    public int? RequestTimeoutSeconds { get; init; }
    public int? StreamingInactivityTimeoutSeconds { get; init; }

    public T? GetOption<T>(string key);           // case-insensitive
}
```

```csharp
// Inside an ILlmService implementation:
var topK = request.GetOption<int?>("TopK");
var thinkingBudget = request.GetOption<int?>("ThinkingBudget");
```

Configuration mirrors that split:

```json
"Roles": {
  "default": {
    "Provider": "Gemini",
    "Model": "gemini-2.5-flash",
    "Inference": {
      "Temperature": 0.7,
      "UseStreaming": true,
      "Options": {
        "ThinkingConfig": { "IncludeThoughts": true, "ThinkingBudget": 2048 }
      }
    }
  }
}
```

**Source:** [LlmInferenceSettings.cs](../../AITaskAgent.Extensions.Llm/Configuration/LlmInferenceSettings.cs)

---

## BaseLlmStep

Base class for LLM-powered steps. It is constructed from an `LlmRole`, not from an `ILlmService`
plus a profile — the service comes from the role's provider (or from the active fallback).

```csharp
public abstract partial class BaseLlmStep(
    string name,
    LlmRole role,
    Type inputType,
    Type outputType,
    string prompt,
    string? systemPrompt = null) : StepBase(name, inputType, outputType), IFallbackable
{
    public int MaxToolIterations { get; init; } = 5;
    public virtual IList<ITool> Tools { get; set; }
    public virtual List<ITagHandler> TagHandlers { get; init; } = [];
    public string? SystemPrompt { get; set; }
    public string? Prompt { get; set; }

    // Resolved for the active engine (role, or the current fallback) of this execution
    protected ILlmService GetLlmService(PipelineContext context);
    protected string GetModel(PipelineContext context);
    protected LlmProvider GetProvider(PipelineContext context);
    protected LlmInferenceSettings GetInference(PipelineContext context);

    protected virtual Task<string?> BuildUserMessageAsync(IStepResult input, PipelineContext context);
    protected virtual Task<string?> BuildSystemMessageAsync(IStepResult input, PipelineContext context);
    protected virtual ConversationContext GetConversationContext(PipelineContext context);
    protected virtual void AddConversationToContext(PipelineContext context, ConversationContext conversation, ILlmStepResult result);
    protected virtual Task<LlmRequest> BuildLlmRequestAsync(...);
    protected virtual LlmRequest ConfigureLlmRequest(LlmRequest request, PipelineContext context);
    protected virtual Task<(IStepResult? Result, string? Error)> ParseLlmResponseAsync(...);
    protected internal void SetRole(LlmRole newRole);
}

public class BaseLlmStep<TIn, TOut>(
    string name,
    LlmRole role,
    string prompt,
    string? systemPrompt = null) : BaseLlmStep(...), IStep<TIn, TOut>
    where TIn : IStepResult
    where TOut : ILlmStepResult;
```

`ParseLlmResponseAsync` receives the `LlmResult`, not a bare response. `SetRole` exists for steps
built before their role is known (`YamlLlmStep` calls it once its configuration has been populated) and rewinds the fallback chain.

`BaseLlmStep` declares `RequiredContext => typeof(LlmPipelineContext)`: running it with a base `PipelineContext` fails
before the first step runs. `GetConversationContext` reads the conversation through `context.AsLlmContext(Name)`.

```csharp
var step = new BaseLlmStep<LlmStringStepResult, LlmStringStepResult>("Chat", role, userPrompt)
{
    Tools = [new GetTimeTool()]
};
```

**Source:** [BaseLlmStep.cs](../../AITaskAgent.Extensions.Llm/Steps/BaseLlmStep.cs)

---

## Models

### LlmRequest

`LlmRequest` inherits from `LlmInferenceSettings`, so `Temperature`, `MaxTokens`, `UseStreaming`,
`Options` and `GetOption<T>` are all available on it directly.

```csharp
public sealed record LlmRequest : LlmInferenceSettings
{
    public required ConversationContext Conversation { get; init; }
    public required string Model { get; init; }

    public bool UseSlidingWindow { get; init; } = true;
    public int SlidingWindowMaxTokens { get; init; } = 8192;
    public int MessageMaxTokens { get; init; } = 8192;

    public string? SystemPrompt { get; init; }
    public ResponseFormatOptions? ResponseFormat { get; init; }
    public string? User { get; init; }
    public IList<ToolDefinition>? Tools { get; init; }

    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string? ApiVersion { get; init; }

    public LlmRequest Copy();
}
```

There is no `Profile` property. Connection data is copied onto the request by the step, so an
`ILlmService` is stateless with respect to configuration.

**Source:** [LlmRequest.cs](../../AITaskAgent.Extensions.Llm/Models/LlmRequest.cs)

### LlmResult

What `ILlmService.InvokeAsync` returns: the whole call, including every candidate and the error
state. It implements the Result pattern — provider failures come back as `Error`/`IsRetriable`
rather than as exceptions.

```csharp
public sealed class LlmResult
{
    public IReadOnlyList<LlmResponse> Candidates { get; init; } = [];

    public int? TokensUsed { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public int? ReasoningTokens { get; init; }
    public int? CachedPromptTokens { get; init; }
    public decimal? CostUsd { get; internal set; }   // filled by BaseLlmStep
    public string? Model { get; init; }
    public object? NativeResponse { get; init; }

    public Exception? Error { get; init; }
    public bool IsRetriable { get; init; }
    public bool IsSuccess => Error == null;

    // Shortcuts to the first candidate
    public string Content { get; }
    public string? Reasoning { get; }
    public FinishReason? FinishReason { get; }
    public List<ToolCall>? ToolCalls { get; }

    public static LlmResult Success(
        IReadOnlyList<LlmResponse> candidates,
        int? tokensUsed = null, int? promptTokens = null, int? completionTokens = null,
        decimal? costUsd = null, string? model = null, object? nativeResponse = null,
        int? reasoningTokens = null, int? cachedPromptTokens = null);

    public static LlmResult Failure(Exception error, bool isRetriable);
}
```

**Source:** [LlmResult.cs](../../AITaskAgent.Extensions.Llm/Models/LlmResult.cs)

### LlmResponse

`LlmResponse` is now **one candidate**, not the whole response. `LlmChoice` no longer exists.

```csharp
public sealed record LlmResponse
{
    public int Index { get; init; }
    public required string Content { get; init; }
    public string? Reasoning { get; init; }
    public FinishReason? FinishReason { get; init; }
    public string? RawFinishReason { get; init; }
    public List<ToolCall>? ToolCalls { get; init; }
    public string? ToolLoopStopMessage { get; init; }   // set when the step cut its tool loop
}
```

**Source:** [LlmResponse.cs](../../AITaskAgent.Extensions.Llm/Models/LlmResponse.cs)

### LlmStreamChunk

```csharp
public sealed class LlmStreamChunk
{
    public required string Delta { get; init; }
    public bool IsThinking { get; init; }
    public bool IsComplete { get; init; }
    public FinishReason? FinishReason { get; init; }
    public string? RawFinishReason { get; init; }
    public int? TokensUsed { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public int? ReasoningTokens { get; init; }
    public int? CachedPromptTokens { get; init; }
    public IReadOnlyList<ToolCallUpdate>? ToolCallUpdates { get; init; }
    public int ChoiceIndex { get; init; }
    public object? NativeChunk { get; init; }
}
```

### ToolCall / ToolDefinition

```csharp
public sealed record ToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Arguments { get; init; }   // JSON string
    public NativeObject? Native { get; init; }        // [JsonIgnore]
}

public sealed record ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string ParametersJsonSchema { get; init; }   // JSON Schema as a string
    public NativeObject? Native { get; init; }
}
```

`ToolDefinition` carries the schema as a **string**, not as an anonymous object.

### FinishReason

```csharp
public enum FinishReason { Streaming, Stop, Length, ToolCalls, ContentFilter, Other }
```

`FinishReasonExtensions.Parse(string?)` returns `(FinishReason, string? Raw)`; `Raw` is non-null
only for `Other`, preserving the provider's original string.

---

## Step Results

```csharp
public interface ILlmStepResult : IStepResult
{
    string AssistantMessage { get; set; }
    decimal? CostUsd { get; }
    FinishReason? FinishReason { get; }
    string? Model { get; }
    int? TokensUsed { get; }
    string GetAssistantMessageOrDefault(string? defaultMessage = null);
}

public interface ILlmStepResult<T> : ILlmStepResult, IStepResult<T> { }
```

| Class | Value | Notes |
|-------|-------|-------|
| `LlmStepResult` | `object?` | Adds `Reasoning` and `ToolLoopStopMessage` on top of `ILlmStepResult` |
| `LlmStepResult<T>` | `T` | Typed variant |
| `LlmStringStepResult` | `string` | Adds `Content` and `ToolCalls`; valid when either is present |
| `LlmIntentionResult` | `Intention` | Produced by `IntentionAnalyzerStep`, consumed by `RouterStep` |
| `LlmJsonStepResult` | `JToken` | `LlmStepResult<JToken>` plus a `JsonSchema`; validates with `JsonStepResult.Validate`. Produced by `YamlLlmStep` when it has an `outputSchema` |

---

## Tool Base Class

```csharp
public abstract class LlmTool : ITool, IDisplayable
{
    public const string ParametersDataKey = "parameters";   // keys used in the tool events' AdditionalData
    public const string ResultDataKey = "result";

    public abstract string Name { get; }
    public abstract string Description { get; }
    public virtual string? UsageGuidelines => null;
    public virtual bool IsReadOnly => false;

    // IDisplayable: templates of the text shown in ToolStartedEvent.Display / ToolCompletedEvent.Display
    public virtual string? DisplayStart { get; set; }
    public virtual string? DisplayEnd { get; set; }

    protected abstract BinaryData ParametersSchema { get; }
    public virtual ToolDefinition GetDefinition();

    // Template Method: ExecuteAsync (not virtual) handles traces, metrics and tool.* events
    public Task<string> ExecuteAsync(string argumentsJson, PipelineContext context, string stepName,
        ILogger logger, CancellationToken cancellationToken = default);

    protected abstract Task<string> InternalExecuteAsync(
        string argumentsJson,
        PipelineContext context,
        ILogger logger,
        CancellationToken cancellationToken);

    protected virtual void EnrichActivityBefore(Activity? activity, string argumentsJson, PipelineContext context);
    protected virtual void EnrichActivityAfter(Activity? activity, string result, PipelineContext context);
    protected virtual ToolStartedEvent EnrichStartedEvent(ToolStartedEvent baseEvent, string argumentsJson, PipelineContext context);
    protected virtual ToolCompletedEvent EnrichCompletedEvent(ToolCompletedEvent baseEvent, string result, PipelineContext context);
}
```

`LlmTool` implements `ITool` and `IDisplayable` — it is not an `IEnrichable`/`IEnrichableStep`. `DisplayStart` / `DisplayEnd` are the templates of
`ToolStartedEvent.Display` / `ToolCompletedEvent.Display`; see [Display text](observability-namespace.md#display-text).

**Source:** [LlmTool.cs](../../AITaskAgent.Extensions.Llm/Tools/Base/LlmTool.cs)

### Registry

```csharp
public sealed class AvailableTools : ITools
{
    public static AvailableTools Instance { get; }

    public AvailableTools();
    public AvailableTools(IEnumerable<ITool> tools);

    public void Register(ITool tool);
    public ITool? GetTool(string name);
    public IReadOnlyList<ITool> GetTools(params string[] names);
    public IReadOnlyList<ITool> GetAllTools(string? module = null);
}
```

`AvailableTools` replaces `ToolRegistry`; `ITools` replaces `IToolRegistry`. Keys are
case-insensitive, and `GetAllTools(module)` filters by the assembly name declaring the tool.

---

## LlmPipelineContext

The context LLM steps require. It extends the base `PipelineContext` with the conversation.

```csharp
// namespace AITaskAgent.LLM.Conversation.Context
public record LlmPipelineContext : PipelineContext
{
    public LlmPipelineContext(IEventChannel? eventChannel = null);
    public LlmPipelineContext(IEventChannel? eventChannel, IServiceProvider serviceProvider);

    public ConversationContext Conversation { get; init; }
    public override LlmPipelineContext CloneForBranch();   // clones the conversation

    public static LlmPipelineContext From(PipelineContext context, string? requester = null);
}

public static class LlmPipelineContextExtensions
{
    public static LlmPipelineContext AsLlmContext(this PipelineContext context, string? requester = null);
}

public class LlmPipelineContextFactory : PipelineContextFactory
{
    public static LlmPipelineContext Create(
        ConversationContext? conversation = null,
        IEnumerable<Func<IProgressEvent, CancellationToken, Task>>? subscribers = null);
}
```

`AddAITaskAgent()` / `AddAITaskAgentLlm()` register `LlmPipelineContextFactory` as the `PipelineContextFactory`, so the context a pipeline gets by
default is already an `LlmPipelineContext`. `AsLlmContext` (and `From`) throw `InvalidOperationException` with a message that names the
requesting step and the actual context type when the context is not an LLM one. A parallel branch gets a clone with its own conversation, so
concurrent branches never write into each other.

**Source:** [LlmPipelineContext.cs](../../AITaskAgent.Extensions.Llm/Conversation/Context/LlmPipelineContext.cs)

---

## ConversationContext

```csharp
public sealed class ConversationContext(
    int maxTokens = 4000,
    Func<string, int>? tokenCounter = null,
    ILogger<ConversationContext>? logger = null,
    ConversationOptions? conversationOptions = null)   // KeepFirstNMessages, UseBookmarks
{
    public string ConversationId { get; init; }
    public MessageHistory History { get; }
    public Dictionary<string, object?> Metadata { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime LastActivityAt { get; set; }

    public ConversationContext AddUserMessage(string content);
    public ConversationContext AddUserMessage(string content, IReadOnlyList<MessageAttachment>? attachments = null);
    public ConversationContext AddAssistantMessage(string content);
    public ConversationContext AddSystemMessage(string content);
    public ConversationContext AddToolMessage(string toolCallId, string content);
    public ConversationContext AddAssistantMessageWithToolCalls(IEnumerable<ToolCall> toolCalls, string? content = null);
    public ConversationContext ReplaceMessage(int index, Message message);
    public void AddMessage(Message message);

    public string CreateBookmark();
    public void RestoreBookmark(string bookmark);

    public List<Message> GetMessagesForRequest(
        int? maxTokens = null,
        string? fromBookmark = null,
        bool useSlidingWindow = true);

    public ConversationContext Clone();
}
```

**Source:** [ConversationContext.cs](../../AITaskAgent.Extensions.Llm/Conversation/Context/ConversationContext.cs)

---

## LLM Steps

| Class | Description |
|-------|-------------|
| `BaseLlmStep<TIn, TOut>` | Generic typed LLM step (there is no `LlmStep<TIn, TOut>`) |
| `TemplateLlmStep<TIn, TOut>` | Prompts rendered from templates, stateful conversation |
| `StatelessTemplateLlmStep<TIn, TOut>` | Same, with an isolated conversation per invocation |
| `IntentionAnalyzerStep<TIn>` | Classifies input into one of a list of `IntentionOption` |
| `RouterStep` | `SwitchStep<IRouterResult>` that routes by the analyzed intention |
| `YamlLlmStep` | Declarative LLM step (tag `LlmStep`) created by the step registry from YAML; implements `IJsonSchemaStep`, resolves its role from `LlmEnvironment` (`profileName`, or the default role) after its configuration is populated and fails while the pipeline is built if the role does not exist |

```csharp
public class TemplateLlmStep<TIn, TOut>(
    string name,
    LlmRole role,
    ITemplateProvider? templateProvider = null,
    IList<string>? tools = null,          // tool NAMES, resolved via AvailableTools.Instance
    string? promtTemplateString = null,   // defaults to "{name}_prompt.md"
    string? systemTemplateString = null)  // defaults to role.SystemPrompt ?? "{name}_system.md"
    : BaseLlmStep<TIn, TOut>(...);

public sealed class RouterStep(
    string name,
    Dictionary<string, IStep> routes,     // matched case-insensitively
    IStep? defaultRoute = null)
    : SwitchStep<IRouterResult>(name), IRouterOptions;
```

---

## Prompt Templates

`ITemplateEngine`/`JsonTemplateEngine` were replaced by `ITemplateRendered`/`TinyTemplateRendered`.
Formatting is done with pipes rather than the old `{{X:format}}` syntax:

```text
{{ input.title }}
{{ input.items | json }}
{{ input.name | upcase }}
{{ context.mykey | default "none" }}
{{ include 'partials/header.md' }}
```

Built-in filters: `json`, `yaml`, `upcase`, `downcase`, `capitalize`, `strip` / `strip_newlines`,
`date`, `default`, `csv`, `size`.

```csharp
public sealed class FileTemplateProvider(
    string folderPath,
    ITemplateRendered? templateEngine = null,   // falls back to TinyTemplateRendered
    bool enableCache = true,
    TimeSpan? ttl = null,
    long? maxCacheSizeBytes = null,
    bool validateTemplates = false) : ITemplateProvider;
```

Both the code-first path (`TemplateLlmStep`) and the declarative one (`YamlLlmStep`) render through
the shared helper `PromptTemplateResolver`, so the template surface is identical:

```csharp
public static class PromptTemplateResolver
{
    public static string Resolve(
        string? rawPrompt,
        IStepResult input,
        PipelineContext context,
        ITemplateProvider? templateProvider = null,
        IEnumerable<KeyValuePair<string, object?>>? variables = null);
}
```

The model exposed to templates is `Input` (the step input's `Value`, as `{{ input.x }}`) and
`Context` (pipeline metadata merged with step variables, as `{{ context.key }}`). Keys are
lowercased; step variables win over pipeline metadata on collision.

**Source:** [PromptTemplateResolver.cs](../../AITaskAgent.Extensions.Llm/Support/PromptTemplateResolver.cs)
