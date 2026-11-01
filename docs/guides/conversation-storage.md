# Conversation Storage

## Overview

AITaskAgent provides persistence for conversation history through the `IConversationStorage` interface.

## Implementations

| Implementation | Storage | Use Case |
|----------------|---------|----------|
| `InMemoryConversationStorage` | Memory | Testing, short sessions. **Registered by default** |
| `FileConversationStorage` | JSON files | Simple persistence, debugging |
| `YamlFileConversationStorage` | YAML files | Human-readable transcripts |

`AddAITaskAgent()` registers the one chosen by `AITaskAgent:Storage:Conversation` (`memory` by
default) with `TryAddSingleton`, so a registration of your own wins whether you add it before
`AddAITaskAgent()` (TryAdd keeps it) or after it (the last registration is resolved):

```json
"AITaskAgent": {
  "Storage": {
    "Conversation": { "Type": "yaml", "Directory": "${APP_DATA}/conversations" }
  }
}
```

| `Type` | Storage built | `Directory` |
|---|---|---|
| `memory` | `InMemoryConversationStorage` | ignored |
| `file` | `FileConversationStorage(Directory)` | required |
| `yaml` | `YamlFileConversationStorage(Directory)` | required |

The type is case-insensitive; a relative `Directory` is resolved against the current directory and
`${VAR}` is expanded. An unknown type, a file type without `Directory`, or a `${VAR}` that is not
defined fail in `UseAITaskAgent()` with a message naming the key. See
[Configuration](configuration.md#storage-aitaskagentstorage).

---

## IConversationStorage Interface

```csharp
public interface IConversationStorage
{
    Task SaveConversationAsync(ConversationContext conversation, CancellationToken ct = default);
    Task<ConversationContext?> GetConversationAsync(string conversationId, CancellationToken ct = default);
}
```

---

## FileConversationStorage

Stores each conversation as a separate JSON file.

### Configuration

```csharp
services.AddSingleton<IConversationStorage>(
    new FileConversationStorage("./data/conversations"));
```

### File Structure

```
./data/conversations/
├── abc123-def456.json
├── xyz789-ghi012.json
└── ...
```

`basePath` defaults to `"conversations"`. Each file holds `ConversationId`, `MaxTokens`, `Messages`,
`Metadata`, `StartedAt` and `LastActivityAt`. Bookmarks and the token counter are not persisted: a
loaded conversation uses the built-in 4-characters-per-token estimator.

### Usage

```csharp
var storage = new FileConversationStorage("./conversations");

// Save conversation
await storage.SaveConversationAsync(context);

// Load conversation
var loaded = await storage.GetConversationAsync(context.ConversationId);

// List all conversations
var ids = await storage.ListConversationsAsync();

// Delete conversation
await storage.DeleteConversationAsync(context.ConversationId);
```

> [!NOTE]
> `ListConversationsAsync` and `DeleteConversationAsync` are **not** on `IConversationStorage` —
> they are extra members of `FileConversationStorage`. Depend on the concrete type when you need
> them.

---

## InMemoryConversationStorage

Stores conversations in a thread-safe dictionary. Data is lost on restart.

```csharp
services.AddSingleton<IConversationStorage, InMemoryConversationStorage>();
```

---

## YamlFileConversationStorage

Same contract as `FileConversationStorage`, serializing to YAML instead of JSON, which makes saved
transcripts readable and diffable. It reads and writes a session format: one `session-{id}.yaml`
file per conversation with `id`, `title`, `agents`, `startedAt`, `completedAt`, `messages` and
`events`. Only the `title` and `agents` metadata entries survive a round trip; other metadata,
`MaxTokens` and bookmarks are not stored.

```csharp
services.AddSingleton<IConversationStorage>(
    new YamlFileConversationStorage("./data/conversations"));
```

The `basePath` is required — there is no default.

---

## Custom Implementation

`ConversationContext` does not round-trip through a plain serializer: `History` is get-only and its
`Messages` is read-only. Persist the messages and rebuild the context, as `FileConversationStorage`
does:

```csharp
public class RedisConversationStorage : IConversationStorage
{
    private readonly IDatabase _redis;

    private sealed record Snapshot(string Id, int MaxTokens, List<Message> Messages,
        Dictionary<string, object?> Metadata, DateTime StartedAt, DateTime LastActivityAt);

    public async Task SaveConversationAsync(ConversationContext conversation, CancellationToken ct)
    {
        var json = JsonConvert.SerializeObject(new Snapshot(conversation.ConversationId,
            conversation.History.MaxTokens, [.. conversation.History.Messages], conversation.Metadata,
            conversation.StartedAt, conversation.LastActivityAt));
        await _redis.StringSetAsync($"conv:{conversation.ConversationId}", json);
    }

    public async Task<ConversationContext?> GetConversationAsync(string conversationId, CancellationToken ct)
    {
        var json = await _redis.StringGetAsync($"conv:{conversationId}");
        if (json.IsNull) return null;

        var s = JsonConvert.DeserializeObject<Snapshot>(json!)!;
        var context = new ConversationContext(s.MaxTokens)
        {
            ConversationId = s.Id,
            Metadata = s.Metadata,
            StartedAt = s.StartedAt,
            LastActivityAt = s.LastActivityAt
        };
        context.History.AddMessages(s.Messages);
        return context;
    }
}
```

---

## Integration with Pipeline

Persist around a step by overriding `FinalizeAsync`, which runs once after all retries. The storage
comes from `context.ServiceProvider`, so the step needs no constructor injection:

```csharp
public sealed class ConversationalStep(string name, LlmRole role, string prompt)
    : BaseLlmStep<LlmStringStepResult, LlmStringStepResult>(name, role, prompt)
{
    protected override async Task FinalizeAsync(
        IStepResult result, PipelineContext context, CancellationToken cancellationToken)
    {
        await base.FinalizeAsync(result, context, cancellationToken);

        var storage = context.ServiceProvider.GetRequiredService<IConversationStorage>();
        await storage.SaveConversationAsync(context.AsLlmContext(Name).Conversation, cancellationToken);
    }
}
```

To rehydrate a conversation before the pipeline starts, build the context from the registered
storage by id. `CreateForConversationAsync` loads it, or starts an empty conversation with that id
when the storage does not have it, and the LLM steps of the run see its messages as history:

```csharp
var context = await LlmPipelineContextFactory.CreateForConversationAsync(provider, conversationId);
context.Metadata["customer"] = "ACME";           // initial values, {{context.customer}} in prompts
var result = await yamlPipeline.ExecuteAsync(input, context);

// The framework does not save it back: do it when the run ends.
await provider.GetRequiredService<IConversationStorage>().SaveConversationAsync(context.Conversation);
```

The same by hand, with a storage you hold yourself:

```csharp
var existing = await storage.GetConversationAsync(conversationId);
var context = LlmPipelineContextFactory.Create(existing ?? new ConversationContext());
```

A suspended run does not need this: a context loaded from a persistent `IPipelineContextStorage`
already carries its conversation (see [Human-in-the-Loop](human-in-the-loop.md#persisting-a-suspended-run)).

The pipeline itself persists the context of a suspended run: `Pipeline.ExecuteAsync` saves it in the
registered `IPipelineContextStorage` when the run ends suspended and deletes it when a resumed run
completes without error. Without a registered storage it only logs a warning, and if `Save` fails the
exception propagates. Details in [Human-in-the-Loop](human-in-the-loop.md#persisting-a-suspended-run).

`MessageHistory.CopyFrom(other)` is available when you need to merge into an existing history
instead of replacing the context.
