# Conversation Storage

## Overview

AITaskAgent provides persistence for conversation history through the `IConversationStorage` interface.

## Implementations

| Implementation | Storage | Use Case |
|----------------|---------|----------|
| `InMemoryConversationStorage` | Memory | Testing, short sessions |
| `FileConversationStorage` | JSON files | Simple persistence, debugging |

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

---

## InMemoryConversationStorage

Stores conversations in a thread-safe dictionary. Data is lost on restart.

```csharp
services.AddSingleton<IConversationStorage, InMemoryConversationStorage>();
```

---

## Custom Implementation

```csharp
public class RedisConversationStorage : IConversationStorage
{
    private readonly IDatabase _redis;
    
    public async Task SaveConversationAsync(ConversationContext conversation, CancellationToken ct)
    {
        var json = JsonConvert.SerializeObject(conversation);
        await _redis.StringSetAsync($"conv:{conversation.ConversationId}", json);
    }
    
    public async Task<ConversationContext?> GetConversationAsync(string conversationId, CancellationToken ct)
    {
        var json = await _redis.StringGetAsync($"conv:{conversationId}");
        return json.IsNull ? null : JsonConvert.DeserializeObject<ConversationContext>(json!);
    }
}
```

---

## Integration with Pipeline

```csharp
public class ConversationalStep : BaseLlmStep<StringResult, LlmStepResult<string>>
{
    private readonly IConversationStorage _storage;
    
    protected override async Task<LlmStepResult<string>> ExecuteAsync(...)
    {
        // Load existing conversation
        var existing = await _storage.GetConversationAsync(context.CorrelationId);
        if (existing != null)
        {
            context.Conversation.History.CopyFrom(existing.History);
        }
        
        var result = await base.ExecuteAsync(...);
        
        // Save conversation after step
        await _storage.SaveConversationAsync(context.Conversation);
        
        return result;
    }
}
```
