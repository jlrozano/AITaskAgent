using AITaskAgent.LLM.Tools.Abstractions;

namespace AITaskAgent.LLM.Configuration;

public class LlmRole
{
    public required LlmProvider Provider { get; init; }
    public required string Model { get; init; } = string.Empty;
    public string? SystemPrompt { get; init; }
    public LlmInferenceSettings? Inference { get; init; } = null;
    public IList<LlmFallback>? Fallbacks { get; init; } = null;
    public IList<ITool>? Tools { get; init; } = null;
}