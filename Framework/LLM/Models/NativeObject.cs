namespace AITaskAgent.LLM.Models;

/// <summary>
/// Wrapper for provider-specific native objects with metadata.
/// Enables smart conversion: reuse if same provider, convert if different.
/// </summary>
public sealed record NativeObject
{
    /// <summary>Provider that generated this object (e.g., "OpenAI", "Gemini").</summary>
    public required string Provider { get; init; }

    /// <summary>Native object from the provider (ChatMessage, FunctionCall, etc.).</summary>
    public required object Value { get; init; }

    /// <summary>Type of the native object for deserialization.</summary>
    public required string TypeName { get; init; }

    /// <summary>Timestamp when this object was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

