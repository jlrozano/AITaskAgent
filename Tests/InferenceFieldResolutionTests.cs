using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;

namespace AITaskAgent.Tests;

/// <summary>
/// A role that declares <c>Inference</c> used to replace the provider's whole inference block: the typed bool
/// properties read <c>false</c> when their key is absent, so a role that only set Temperature silently turned off the
/// provider's UseStreaming / UseMaxCompletionTokens. Each option now resolves field by field: the role's value when it
/// set one, else the provider's, else the default.
/// </summary>
public sealed class InferenceFieldResolutionTests
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

    private sealed class ProbeStep(LlmRole role)
        : StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>("Probe", role, templateProvider: null, promtTemplateString: "{{Input}}")
    {
        public Task<LlmRequest> BuildAsync() =>
            BuildLlmRequestAsync(
                new LlmStringStepResult(new EmptyStep("Seed")) { Content = "hola" },
                new PipelineContext(),
                new ConversationContext(),
                CancellationToken.None);
    }

    private static Task<LlmRequest> Build(LlmInferenceSettings? provider, LlmInferenceSettings? role) =>
        new ProbeStep(new LlmRole
        {
            Provider = new LlmProvider { Service = new StubService(), Inference = provider },
            Model = "test-model",
            Inference = role
        }).BuildAsync();

    [Fact]
    public async Task A_role_that_does_not_set_the_flags_inherits_the_providers()
    {
        var request = await Build(
            provider: new LlmInferenceSettings { UseStreaming = true, UseMaxCompletionTokens = true },
            role: new LlmInferenceSettings { Temperature = 0.2f });

        Assert.True(request.UseStreaming);
        Assert.True(request.UseMaxCompletionTokens);
        Assert.Equal(0.2f, request.Temperature);
    }

    [Fact]
    public async Task A_role_that_sets_false_explicitly_wins_over_the_provider()
    {
        var request = await Build(
            provider: new LlmInferenceSettings { UseStreaming = true, UseMaxCompletionTokens = true },
            role: new LlmInferenceSettings { UseStreaming = false, UseMaxCompletionTokens = false });

        Assert.False(request.UseStreaming);
        Assert.False(request.UseMaxCompletionTokens);
    }

    [Fact]
    public async Task A_role_that_sets_true_wins_over_a_provider_that_sets_false()
    {
        var request = await Build(
            provider: new LlmInferenceSettings { UseStreaming = false },
            role: new LlmInferenceSettings { UseStreaming = true });

        Assert.True(request.UseStreaming);
    }

    [Fact]
    public async Task Without_either_level_the_flags_default_to_false()
    {
        var request = await Build(provider: null, role: new LlmInferenceSettings { Temperature = 0.5f });

        Assert.False(request.UseStreaming);
        Assert.False(request.UseMaxCompletionTokens);
    }
}
