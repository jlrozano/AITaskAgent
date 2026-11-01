namespace AITaskAgent.Core;

/// <summary>
/// Centralized constants for the AITaskAgent framework.
/// </summary>
public static partial class AITaskAgentConstants
{
    /// <summary>
    /// OpenTelemetry tag name constants for distributed tracing.
    /// Use with Activity.SetTag() for consistent tag naming across spans.
    /// </summary>
    public static class TelemetryTags
    {
        /// <summary>Tag for pipeline name.</summary>
        public const string PipelineName = "pipeline.name";

        /// <summary>Tag for step name.</summary>
        public const string StepName = "step.name";

        /// <summary>Tag for step type (class name).</summary>
        public const string StepType = "step.type";

        /// <summary>Tag for step duration in milliseconds.</summary>
        public const string StepDurationMs = "step.duration_ms";

        /// <summary>Tag for step success status.</summary>
        public const string StepSuccess = "step.success";

        /// <summary>Tag for correlation ID.</summary>
        public const string CorrelationId = "correlation.id";

        /// <summary>Tag for retry attempt number.</summary>
        public const string Attempt = "step.attempt";

        /// <summary>Tag for maximum retry count.</summary>
        public const string MaxRetries = "step.max_retries";

        /// <summary>Tag for LLM model name.</summary>
        public const string LlmModel = "llm.model";

        /// <summary>Tag for LLM provider name.</summary>
        public const string LlmProvider = "llm.provider";

        /// <summary>Tag for tokens used in LLM call.</summary>
        public const string TokensUsed = "llm.tokens_used";

        /// <summary>Tag for tool name.</summary>
        public const string ToolName = "tool.name";

        /// <summary>Tag for tool count.</summary>
        public const string ToolCount = "tool.count";

        /// <summary>ActivitySource name for OpenTelemetry tracing.</summary>
        public const string ActivitySourceName = "AITaskAgent";

        /// <summary>ActivitySource version for OpenTelemetry tracing.</summary>
        public const string ActivitySourceVersion = "1.0.0";

        /// <summary>Meter name for native .NET metrics.</summary>
        public const string MeterName = "AITaskAgent.Pipeline";

        /// <summary>Meter version for native .NET metrics.</summary>
        public const string MeterVersion = "1.0.0";

        /// <summary>Tag for pipeline step count.</summary>
        public const string PipelineStepCount = "pipeline.step_count";

        /// <summary>Tag for pipeline timeout in milliseconds.</summary>
        public const string PipelineTimeoutMs = "pipeline.timeout_ms";
    }
}

