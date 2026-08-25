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

    /// <summary>
    /// Cost in USD of a call to <paramref name="model"/>, from the per-million prices declared in
    /// <see cref="Models"/>.
    /// </summary>
    /// <remarks>
    /// Lives on the provider rather than in each ILlmService implementation because pricing is
    /// configuration, not protocol: every provider prices the same way and none of them receives
    /// the model catalogue in its request.
    /// </remarks>
    /// <returns>The cost, or null when the model has no pricing configured.</returns>
    public decimal? CalculateCost(string? model, int? promptTokens, int? completionTokens)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var settings = Models.FirstOrDefault(m =>
            string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase));

        if (settings is null)
        {
            return null;
        }

        return (promptTokens ?? 0) / 1_000_000m * settings.InputPricePerMillion
             + (completionTokens ?? 0) / 1_000_000m * settings.OutputPricePerMillion;
    }
}

public record LlmModelSettings(
    string Name,
    decimal InputPricePerMillion,
    decimal OutputPricePerMillion,
    LlmInferenceSettings Inference
);