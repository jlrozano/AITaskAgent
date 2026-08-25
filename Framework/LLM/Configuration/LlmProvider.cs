using AITaskAgent.LLM.Abstractions;

namespace AITaskAgent.LLM.Configuration;

public class LlmProvider
{
    public string? BaseUrl { get; init; } = null;
    public string? ApiKey { get; init; } = null;
    public LlmInferenceSettings? Inference { get; init; } = null;
    public List<LlmModelSettings> Models { get; init; } = [];
    public string? ApiVersion { get; init; } = null;
    public required ILlmService Service { get; init; }
}

public record LlmModelSettings(
    string Name,
    decimal InputPricePerMillion,
    decimal OutputPricePerMillion,
    LlmInferenceSettings Inference
);