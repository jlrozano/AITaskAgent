using System.Diagnostics.Metrics;

namespace AITaskAgent.Core.Base;

/// <summary>
/// Centralized native .NET Meter for pipeline metrics.
/// OpenTelemetry SDK can consume these via .AddMeter("AITaskAgent.Pipeline").
/// </summary>
internal static class Metrics
{
    /// <summary>Main meter for AITaskAgent pipeline metrics.</summary>
    public static readonly Meter Meter = new(
        AITaskAgentConstants.TelemetryTags.MeterName,
        AITaskAgentConstants.TelemetryTags.MeterVersion);

    // Pipeline Counters
    /// <summary>Total pipeline executions.</summary>
    public static readonly Counter<long> PipelineExecutions =
        Meter.CreateCounter<long>("aitaskagent.pipeline.executions",
            description: "Total pipeline executions");

    // Step Counters
    /// <summary>Total step executions.</summary>
    public static readonly Counter<long> StepExecutions =
        Meter.CreateCounter<long>("aitaskagent.step.executions",
            description: "Total step executions");

    /// <summary>Total step errors.</summary>
    public static readonly Counter<long> StepErrors =
        Meter.CreateCounter<long>("aitaskagent.step.errors",
            description: "Total step errors");

    /// <summary>Total step retries.</summary>
    public static readonly Counter<long> StepRetries =
        Meter.CreateCounter<long>("aitaskagent.step.retries",
            description: "Total step retries");

    // LLM Counters
    /// <summary>Total LLM API requests.</summary>
    public static readonly Counter<long> LlmRequests =
        Meter.CreateCounter<long>("aitaskagent.llm.requests",
            description: "Total LLM API requests");

    /// <summary>Total tokens used across all LLM calls.</summary>
    public static readonly Counter<long> LlmTokens =
        Meter.CreateCounter<long>("aitaskagent.llm.tokens",
            unit: "tokens",
            description: "Total tokens used");

    /// <summary>Total LLM cost in USD.</summary>
    public static readonly Counter<double> LlmCost =
        Meter.CreateCounter<double>("aitaskagent.llm.cost",
            unit: "USD",
            description: "Total LLM cost");

    // Tool Counters
    /// <summary>Total tool executions.</summary>
    public static readonly Counter<long> ToolExecutions =
        Meter.CreateCounter<long>("aitaskagent.tool.executions",
            description: "Total tool executions");

    // Histograms
    /// <summary>Step duration distribution in milliseconds.</summary>
    public static readonly Histogram<double> StepDuration =
        Meter.CreateHistogram<double>("aitaskagent.step.duration",
            unit: "ms",
            description: "Step execution duration");

    /// <summary>LLM call duration distribution in milliseconds.</summary>
    public static readonly Histogram<double> LlmDuration =
        Meter.CreateHistogram<double>("aitaskagent.llm.duration",
            unit: "ms",
            description: "LLM call duration");

    /// <summary>Pipeline duration distribution in milliseconds.</summary>
    public static readonly Histogram<double> PipelineDuration =
        Meter.CreateHistogram<double>("aitaskagent.pipeline.duration",
            unit: "ms",
            description: "Pipeline execution duration");

    /// <summary>Tool duration distribution in milliseconds.</summary>
    public static readonly Histogram<double> ToolDuration =
        Meter.CreateHistogram<double>("aitaskagent.tool.duration",
            unit: "ms",
            description: "Tool execution duration");
}
