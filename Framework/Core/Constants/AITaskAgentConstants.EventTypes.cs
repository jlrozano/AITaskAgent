namespace AITaskAgent.Core;

/// <summary>
/// Centralized constants for the AITaskAgent framework.
/// </summary>
public static partial class AITaskAgentConstants
{
    /// <summary>
    /// Event type constants for observability events.
    /// </summary>
    public static class EventTypes
    {
        /// <summary>Event type for pipeline started.</summary>
        public const string PipelineStarted = "pipeline.started";

        /// <summary>Event type for pipeline completed.</summary>
        public const string PipelineCompleted = "pipeline.completed";

        /// <summary>Event type for step started.</summary>
        public const string StepStarted = "step.started";

        /// <summary>Event type for step completed.</summary>
        public const string StepCompleted = "step.completed";

        /// <summary>Event type for step validation.</summary>
        public const string StepValidation = "step.validation";

        /// <summary>Event type for tool started.</summary>
        public const string ToolStarted = "tool.started";

        /// <summary>Event type for tool completed.</summary>
        public const string ToolCompleted = "tool.completed";

        /// <summary>Event type for LLM response (streaming chunk or final).</summary>
        public const string LlmResponse = "llm.response";

        /// <summary>Event type for step routing decision.</summary>
        public const string StepRouting = "step.routing";

        /// <summary>Event type for streaming tag started.</summary>
        public const string TagStarted = "tag.started";

        /// <summary>Event type for streaming tag completed.</summary>
        public const string TagCompleted = "tag.completed";
    }
}

