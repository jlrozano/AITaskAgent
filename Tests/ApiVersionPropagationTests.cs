using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;

namespace AITaskAgent.Tests;

/// <summary>
/// LlmProvider.ApiVersion and LlmRequest.ApiVersion have existed since Gemini support was added,
/// but BaseLlmStep.BuildLlmRequestAsync never copied one into the other - a role built through the
/// normal DI/config path always sent ApiVersion = null, no matter what the provider declared.
/// Azure OpenAI needs this (its endpoint requires ?api-version=...), which is what surfaced the gap.
/// </summary>
public sealed class ApiVersionPropagationTests
{
    private sealed class CapturingService : ILlmService
    {
        public LlmRequest? LastRequest { get; private set; }

        public string Name => "capturing-stub";

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(LlmResult.Success([new LlmResponse { Content = "ok", FinishReason = FinishReason.Stop }]));
        }

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<LlmStreamChunk>();

        public int EstimateTokenCount(string text) => text.Length / 4;
        public int GetMaxContextTokens(string? model = null) => 8192;
    }

    [Fact]
    public async Task Providers_ApiVersion_reaches_the_request()
    {
        var service = new CapturingService();
        var role = new LlmRole
        {
            Provider = new LlmProvider { Service = service, ApiVersion = "2024-08-01-preview" },
            Model = "test-model"
        };
        var step = new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            "Probe", role, templateProvider: null, promtTemplateString: "{{Input}}");

        await ((IStep)step).ExecuteAsync(
            new LlmStringStepResult(step) { Content = "hola" },
            new PipelineContext(), attempt: 1, lastStepResult: null, CancellationToken.None);

        Assert.Equal("2024-08-01-preview", service.LastRequest?.ApiVersion);
    }

    [Fact]
    public async Task A_provider_without_ApiVersion_sends_null_not_a_stale_default()
    {
        var service = new CapturingService();
        var role = new LlmRole
        {
            Provider = new LlmProvider { Service = service },
            Model = "test-model"
        };
        var step = new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            "Probe", role, templateProvider: null, promtTemplateString: "{{Input}}");

        await ((IStep)step).ExecuteAsync(
            new LlmStringStepResult(step) { Content = "hola" },
            new PipelineContext(), attempt: 1, lastStepResult: null, CancellationToken.None);

        Assert.Null(service.LastRequest?.ApiVersion);
    }
}
