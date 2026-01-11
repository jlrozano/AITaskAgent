namespace AITaskAgent.Observability;

/// <summary>
/// Event representing a trace point in step execution.
/// Maps to distributed tracing concepts (spans, events, attributes).
/// </summary>
public sealed class StepTraceEvent
{
    /// <summary>Name of the step emitting the event.</summary>
    public required string StepName { get; init; }

    /// <summary>Current status of the step.</summary>
    public required StepStatus Status { get; init; }

    /// <summary>Optional message providing additional context.</summary>
    public string? Message { get; init; }

    /// <summary>Correlation ID for tracing the entire pipeline execution (Trace ID).</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Name of the pipeline executing this step.</summary>
    public string? PipelineName { get; init; }

    /// <summary>Type of step (e.g., "LlmStep", "LambdaStep") for span classification.</summary>
    public string? StepType { get; init; }

    /// <summary>Parent step name for nested pipeline hierarchies.</summary>
    public string? ParentStepName { get; init; }

    /// <summary>Extensible attributes for span tags (e.g., model name, token count).</summary>
    public Dictionary<string, object> Attributes { get; init; } = [];

    /// <summary>Timestamp of the event.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Streaming content delta (for LLM streaming, separate from tracing).</summary>
    public string? StreamingContent { get; init; }
}

