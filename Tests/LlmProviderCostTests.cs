using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Models;

namespace AITaskAgent.Tests;

/// <summary>
/// Cost was silently null for the whole of the imported branch: the prices were configured, the
/// metric and the metadata key existed, and nothing ever computed the number. These pin the
/// calculation so it cannot rot back into place unnoticed.
/// </summary>
public class LlmProviderCostTests
{
    private sealed class StubService : ILlmService
    {
        public string Name => "stub";
        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(LlmResult.Success([]));
        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<LlmStreamChunk>();
        public int EstimateTokenCount(string text) => text.Length / 4;
        public int GetMaxContextTokens(string? model = null) => 8192;
    }

    private static LlmProvider ProviderWith(params LlmModelSettings[] models) => new()
    {
        Service = new StubService(),
        Models = [.. models]
    };

    [Fact]
    public void Prices_prompt_and_completion_tokens_separately()
    {
        var provider = ProviderWith(new LlmModelSettings("gpt-test", 3m, 15m, new LlmInferenceSettings()));

        var cost = provider.CalculateCost("gpt-test", promptTokens: 1_000_000, completionTokens: 2_000_000);

        Assert.Equal(3m + 30m, cost);
    }

    [Fact]
    public void Matches_the_model_name_case_insensitively()
    {
        var provider = ProviderWith(new LlmModelSettings("GPT-Test", 1m, 1m, new LlmInferenceSettings()));

        var cost = provider.CalculateCost("gpt-test", 1_000_000, 0);

        Assert.Equal(1m, cost);
    }

    [Fact]
    public void Returns_null_when_the_model_has_no_pricing()
    {
        var provider = ProviderWith(new LlmModelSettings("known", 1m, 1m, new LlmInferenceSettings()));

        Assert.Null(provider.CalculateCost("unknown", 1000, 1000));
    }

    [Fact]
    public void Returns_null_when_either_price_is_unknown_rather_than_counting_it_as_free()
    {
        var provider = ProviderWith(
            new LlmModelSettings("no-input", null, 1m, new LlmInferenceSettings()),
            new LlmModelSettings("no-output", 1m, null, new LlmInferenceSettings()));

        Assert.Null(provider.CalculateCost("no-input", 1000, 1000));
        Assert.Null(provider.CalculateCost("no-output", 1000, 1000));
    }

    [Fact]
    public void Returns_null_without_a_model()
    {
        var provider = ProviderWith(new LlmModelSettings("known", 1m, 1m, new LlmInferenceSettings()));

        Assert.Null(provider.CalculateCost(null, 1000, 1000));
    }

    [Fact]
    public void Treats_missing_token_counts_as_zero()
    {
        var provider = ProviderWith(new LlmModelSettings("m", 10m, 10m, new LlmInferenceSettings()));

        var cost = provider.CalculateCost("m", promptTokens: null, completionTokens: 1_000_000);

        Assert.Equal(10m, cost);
    }

    [Fact]
    public void Cached_prompt_tokens_are_billed_at_the_cached_price_and_the_rest_at_the_input_price()
    {
        var provider = ProviderWith(new LlmModelSettings("m", 2.5m, 15m, new LlmInferenceSettings(), CachedInputPricePerMillion: 0.25m));

        // 1 M de entrada de los que 0,6 M son caché: 0,4 M a 2,5 + 0,6 M a 0,25 + 1 M de salida a 15.
        var cost = provider.CalculateCost("m", promptTokens: 1_000_000, completionTokens: 1_000_000, cachedPromptTokens: 600_000);

        Assert.Equal(1m + 0.15m + 15m, cost);
    }

    [Fact]
    public void Without_a_cached_price_cached_tokens_are_billed_at_the_input_price()
    {
        var provider = ProviderWith(new LlmModelSettings("m", 2.5m, 15m, new LlmInferenceSettings()));

        var cost = provider.CalculateCost("m", promptTokens: 1_000_000, completionTokens: 0, cachedPromptTokens: 600_000);

        Assert.Equal(2.5m, cost);
    }

    [Fact]
    public void Without_cached_tokens_the_cached_price_changes_nothing()
    {
        var provider = ProviderWith(new LlmModelSettings("m", 2.5m, 15m, new LlmInferenceSettings(), CachedInputPricePerMillion: 0.25m));

        Assert.Equal(2.5m, provider.CalculateCost("m", 1_000_000, 0));
        Assert.Equal(2.5m, provider.CalculateCost("m", 1_000_000, 0, cachedPromptTokens: null));
    }

    [Fact]
    public void More_cached_tokens_than_prompt_tokens_are_capped_at_the_prompt()
    {
        var provider = ProviderWith(new LlmModelSettings("m", 2m, 0m, new LlmInferenceSettings(), CachedInputPricePerMillion: 1m));

        Assert.Equal(1m, provider.CalculateCost("m", promptTokens: 1_000_000, completionTokens: 0, cachedPromptTokens: 5_000_000));
    }
}
