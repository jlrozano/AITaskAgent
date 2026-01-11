namespace PipelineVisualizer.Models;

/// <summary>
/// Request model for the chat endpoint.
/// </summary>
public sealed record ChatRequest
{
    /// <summary>
    /// User's message to process through the pipeline.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Optional conversation ID for continuing an existing session.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// Name of the pipeline to execute (default: "StoryMachine").
    /// </summary>
    public string PipelineName { get; init; } = "StoryMachine";
}

/// <summary>
/// Response model for the chat endpoint.
/// </summary>
public sealed record ChatResponse
{
    /// <summary>
    /// Unique identifier for this pipeline execution.
    /// </summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// Conversation ID for this session (generated if not provided).
    /// </summary>
    public required string ConversationId { get; init; }

    /// <summary>
    /// Final result from the pipeline.
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// Error message if pipeline failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Whether the pipeline completed successfully.
    /// </summary>
    public bool Success => string.IsNullOrEmpty(Error);
}
