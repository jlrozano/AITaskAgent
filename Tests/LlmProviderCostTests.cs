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
}
