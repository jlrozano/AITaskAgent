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
    /// <param name="cachedPromptTokens">
    /// Prompt tokens served from the provider's prompt cache. They are part of <paramref name="promptTokens"/>, not
    /// on top of it: they are billed at <see cref="LlmModelSettings.CachedInputPricePerMillion"/> and the rest of the
    /// prompt at the normal input price. When the model declares no cached price they are billed at the normal input
    /// price, which overstates the cost rather than understating it.
    /// </param>
    /// <returns>
    /// The cost, or null when the model has no pricing configured or either of its prices is null: an unknown cost
    /// must not read as a free call.
    /// </returns>
    public decimal? CalculateCost(string? model, int? promptTokens, int? completionTokens, int? cachedPromptTokens = null)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var settings = Models.FirstOrDefault(m =>
            string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase));

        if (settings is not { InputPricePerMillion: { } inputPrice, OutputPricePerMillion: { } outputPrice })
        {
            return null;
        }

        var prompt = promptTokens ?? 0;
        // Clamped so a provider reporting more cached tokens than prompt tokens cannot make the uncached part negative.
        var cached = Math.Clamp(cachedPromptTokens ?? 0, 0, Math.Max(prompt, 0));
        var cachedPrice = settings.CachedInputPricePerMillion ?? inputPrice;

        return (prompt - cached) / 1_000_000m * inputPrice
             + cached / 1_000_000m * cachedPrice
             + (completionTokens ?? 0) / 1_000_000m * outputPrice;
    }
}

/// <summary>
/// A model of a provider with its prices in USD per million tokens. A null price means unknown (the model is listed
/// but not priced yet), and <see cref="LlmProvider.CalculateCost"/> then reports no cost rather than zero.
/// <see cref="CachedInputPricePerMillion"/> is the price of prompt tokens served from the provider's prompt cache;
/// when null they are billed at <see cref="InputPricePerMillion"/>.
/// </summary>
public record LlmModelSettings(
    string Name,
    decimal? InputPricePerMillion,
    decimal? OutputPricePerMillion,
    LlmInferenceSettings Inference,
    decimal? CachedInputPricePerMillion = null
);