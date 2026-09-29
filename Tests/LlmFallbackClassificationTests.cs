using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.LLM.Tools.Abstractions;
using Microsoft.Extensions.Logging;

namespace AITaskAgent.Tests;

/// <summary>
/// SwitchToNextFallback used to change engine on any error that carried an exception, so an exhausted
/// tool loop was replayed against every fallback. Errors are now typed: only LlmProviderException moves
/// to the next provider, and the tool-iteration limit keeps its own type for a derived step to handle.
/// </summary>
public sealed class LlmFallbackClassificationTests
{
    private sealed class FakeReadOnlyTool : ITool
    {
        public string Name => "list_dir";
        public string Description => "test tool";
        public bool IsReadOnly => true;
        public ToolDefinition GetDefinition() => new() { Name = Name, Description = Description, ParametersJsonSchema = "{}" };
        public Task<string> ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, ILogger logger, CancellationToken cancellationToken) =>
            Task.FromResult("ok");
    }

    private sealed class ScriptedService(Func<int, LlmResult> respond) : ILlmService
    {
        public int Calls { get; private set; }

        public string Name => "scripted";

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(++Calls));
        }

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException("not exercised by these tests");

        public int EstimateTokenCount(string text) => text.Length / 4;
        public int GetMaxContextTokens(string? model = null) => 8192;
    }

    private static readonly ILlmService FallbackService = new ScriptedService(_ => LlmResult.Success([new LlmResponse { Content = "fallback" }]));

    private static IStep BuildStep(ILlmService service, int maxToolIterations = 5) =>
        new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            "Agent",
            new LlmRole
            {
                Provider = new LlmProvider { Service = service },
                Model = "primary-model",
                Inference = new LlmInferenceSettings { UseStreaming = false },
                Fallbacks = [new LlmFallback("fallback-model", new LlmProvider { Service = FallbackService })]
            },
            templateProvider: null,
            promtTemplateString: "{{Input}}")
        {
            Tools = [new FakeReadOnlyTool()],
            MaxToolIterations = maxToolIterations
        };

    private static Task<IStepResult> RunAsync(IStep step, PipelineContext context, CancellationToken cancellationToken = default) =>
        step.ExecuteAsync(
            new LlmStringStepResult(new EmptyStep("Seed")) { Content = "hazlo" },
            context, attempt: 1, lastStepResult: null, cancellationToken);

    [Fact]
    public async Task An_exhausted_tool_loop_is_typed_and_does_not_switch_provider()
    {
        // Distinct arguments on every call, so the stuck-loop guard does not end the turn first.
        var service = new ScriptedService(n => LlmResult.Success([new LlmResponse
        {
            Content = "",
            FinishReason = FinishReason.ToolCalls,
            ToolCalls = [new ToolCall { Id = $"call_{n}", Name = "list_dir", Arguments = $"{{\"path\":\"dir{n}\"}}" }]
        }]));
        var step = BuildStep(service, maxToolIterations: 2);
        var context = new PipelineContext();

        var result = await RunAsync(step, context);

        var limit = Assert.IsType<ToolIterationLimitExceededException>(result.Error?.OriginalException);
        Assert.Equal(2, limit.MaxToolIterations);
        Assert.Equal("Agent", limit.StepName);
        Assert.False(((IFallbackable)step).SwitchToNextFallback(result, context, 1));
    }

    [Fact]
    public async Task A_failure_returned_by_the_provider_is_typed_and_switches_provider()
    {
        var service = new ScriptedService(_ => LlmResult.Failure(new HttpRequestException("503 Service Unavailable"), true));
        var step = BuildStep(service);
        var context = new PipelineContext();

        var result = await RunAsync(step, context);

        var provider = Assert.IsType<LlmProviderException>(result.Error?.OriginalException);
        Assert.True(provider.IsRetriable);
        Assert.IsType<HttpRequestException>(provider.InnerException);
        Assert.True(((IFallbackable)step).SwitchToNextFallback(result, context, 1));
    }

    [Fact]
    public async Task A_provider_that_throws_is_typed_as_a_provider_failure_instead_of_escaping()
    {
        var service = new ScriptedService(_ => throw new InvalidOperationException("stream broke"));
        var step = BuildStep(service);
        var context = new PipelineContext();

        var result = await RunAsync(step, context);

        var provider = Assert.IsType<LlmProviderException>(result.Error?.OriginalException);
        Assert.IsType<InvalidOperationException>(provider.InnerException);
        Assert.True(((IFallbackable)step).SwitchToNextFallback(result, context, 1));
    }

    [Fact]
    public async Task A_requested_cancellation_propagates_instead_of_becoming_a_provider_failure()
    {
        var service = new ScriptedService(_ => LlmResult.Success([new LlmResponse { Content = "never" }]));
        var step = BuildStep(service);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(step, new PipelineContext(), cts.Token));
    }

    [Fact]
    public async Task An_error_without_a_provider_exception_does_not_switch_provider()
    {
        var step = BuildStep(new ScriptedService(_ => LlmResult.Success([new LlmResponse { Content = "ok" }])));
        var context = new PipelineContext();
        await RunAsync(step, context);
        var templateBug = new LlmStringStepResult(new EmptyStep("Seed"))
        {
            Error = new StepError { Message = "template failed", OriginalException = new FormatException("bad template") }
        };

        Assert.False(((IFallbackable)step).SwitchToNextFallback(templateBug, context, 1));
    }
}
