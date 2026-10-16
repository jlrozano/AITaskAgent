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
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Logging;

namespace AITaskAgent.Tests;

/// <summary>
/// The usage of each call travels on its final <see cref="LlmResponseEvent"/>, so a host can add up input tokens,
/// output tokens and cost per step and per run. Before, the event only had the total, and the step result lost the
/// split and turned "no price" into a cost of 0 as soon as a tool iteration happened.
/// </summary>
public sealed class LlmCallUsageEventTests
{
    private sealed class EchoTool : ITool
    {
        public string Name => "echo";
        public string Description => "test tool";
        public bool IsReadOnly => true;
        public ToolDefinition GetDefinition() => new() { Name = Name, Description = Description, ParametersJsonSchema = "{}" };
        public Task<string> ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, ILogger logger, CancellationToken cancellationToken) =>
            Task.FromResult("ok");
    }

    /// <summary>Answers with the queued responses in order; the model name is the one Azure would send back.</summary>
    private sealed class QueuedService(params LlmResult[] results) : ILlmService
    {
        private readonly Queue<LlmResult> _results = new(results);

        public string Name => "queued-stub";

        /// <summary>cached_tokens reported on the streamed usage chunk; null as a provider that does not send them.</summary>
        public int? StreamCachedTokens { get; init; }

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(_results.Dequeue());

        public async IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new LlmStreamChunk { Delta = "hola" };
            yield return new LlmStreamChunk
            {
                Delta = "",
                IsComplete = true,
                FinishReason = FinishReason.Stop,
                TokensUsed = 3_000_000,
                PromptTokens = 2_000_000,
                CompletionTokens = 1_000_000,
                CachedPromptTokens = StreamCachedTokens
            };
        }

        public int EstimateTokenCount(string text) => text.Length / 4;
        public int GetMaxContextTokens(string? model = null) => 8192;
    }

    private static LlmResult Answer(string content, int prompt, int completion, List<ToolCall>? toolCalls = null, int? cached = null) =>
        LlmResult.Success(
            [new LlmResponse { Content = content, FinishReason = toolCalls != null ? FinishReason.ToolCalls : FinishReason.Stop, ToolCalls = toolCalls }],
            tokensUsed: prompt + completion,
            promptTokens: prompt,
            completionTokens: completion,
            model: "deployment-2026-01-01",
            cachedPromptTokens: cached);

    private static async Task<(IStepResult Result, List<LlmResponseEvent> Finals)> RunAsync(
        ILlmService service, bool priced, bool streaming = false, bool withTool = false, decimal? cachedPrice = null)
    {
        var provider = new LlmProvider
        {
            Service = service,
            Models = priced ? [new LlmModelSettings("deployment", 1m, 10m, new LlmInferenceSettings(), cachedPrice)] : []
        };
        IStep step = new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            "Usage",
            new LlmRole { Provider = provider, Model = "deployment", Inference = new LlmInferenceSettings { UseStreaming = streaming } },
            templateProvider: null,
            promtTemplateString: "{{Input}}")
        {
            Tools = withTool ? [new EchoTool()] : []
        };

        var events = new List<IProgressEvent>();
        var channel = new EventChannel();
        using (channel.Subscribe((ev, _) => { lock (events) events.Add(ev); return Task.CompletedTask; }))
        {
            var result = await step.ExecuteAsync(
                new LlmStringStepResult(new EmptyStep("Seed")) { Content = "hazlo" },
                new PipelineContext(channel), attempt: 1, lastStepResult: null, CancellationToken.None);
            await channel.DisposeAsync();
            lock (events)
            {
                return (result, [.. events.OfType<LlmResponseEvent>().Where(e => e.FinishReason != FinishReason.Streaming)]);
            }
        }
    }

    [Fact]
    public async Task The_final_event_carries_the_split_and_the_price_of_the_requested_model_when_the_answer_names_a_version()
    {
        var (result, finals) = await RunAsync(new QueuedService(Answer("hola", 2_000_000, 1_000_000)), priced: true);

        var final = Assert.Single(finals);
        Assert.Equal((2_000_000, 1_000_000), (final.PromptTokens, final.CompletionTokens));
        // 2 M de entrada a 1 $ y 1 M de salida a 10 $: el catálogo nombra el despliegue, la respuesta la versión.
        Assert.Equal(12m, final.CostUsd);
        Assert.Equal(12m, ((ILlmStepResult)result).CostUsd);
    }

    [Fact]
    public async Task Without_a_price_the_cost_stays_null_across_tool_iterations()
    {
        var call = new List<ToolCall> { new() { Id = "c1", Name = "echo", Arguments = "{}" } };
        var service = new QueuedService(Answer("", 100, 10, call), Answer("fin", 200, 20));

        var (result, finals) = await RunAsync(service, priced: false, withTool: true);

        Assert.Equal(2, finals.Count);
        Assert.Equal([100, 200], finals.Select(f => f.PromptTokens ?? -1));
        Assert.Equal([10, 20], finals.Select(f => f.CompletionTokens ?? -1));
        Assert.All(finals, f => Assert.Null(f.CostUsd));
        Assert.Null(((ILlmStepResult)result).CostUsd);
    }

    [Fact]
    public async Task With_a_price_the_step_cost_adds_up_every_tool_iteration()
    {
        var call = new List<ToolCall> { new() { Id = "c1", Name = "echo", Arguments = "{}" } };
        var service = new QueuedService(Answer("", 1_000_000, 0, call), Answer("fin", 1_000_000, 100_000));

        var (result, finals) = await RunAsync(service, priced: true, withTool: true);

        Assert.Equal([1m, 2m], finals.Select(f => f.CostUsd ?? -1));
        Assert.Equal(3m, ((ILlmStepResult)result).CostUsd);
    }

    [Fact]
    public async Task An_empty_answer_still_gets_its_final_event_with_the_usage()
    {
        var (_, finals) = await RunAsync(new QueuedService(Answer("", 5_000, 65_536)), priced: false);

        var final = Assert.Single(finals);
        Assert.Equal((5_000, 65_536), (final.PromptTokens, final.CompletionTokens));
    }

    [Fact]
    public async Task A_streamed_call_carries_the_usage_of_its_last_chunk_and_its_cost()
    {
        var (_, finals) = await RunAsync(new QueuedService(), priced: true, streaming: true);

        var final = Assert.Single(finals);
        Assert.Equal((2_000_000, 1_000_000), (final.PromptTokens, final.CompletionTokens));
        Assert.Equal(12m, final.CostUsd);
        Assert.Null(final.CachedPromptTokens);
    }

    [Fact]
    public async Task A_streamed_call_prices_its_cached_tokens_at_the_cached_price()
    {
        var service = new QueuedService { StreamCachedTokens = 1_000_000 };

        var (result, finals) = await RunAsync(service, priced: true, streaming: true, cachedPrice: 0.1m);

        var final = Assert.Single(finals);
        Assert.Equal(1_000_000, final.CachedPromptTokens);
        // 1 M sin caché a 1 $ + 1 M de caché a 0,1 $ + 1 M de salida a 10 $.
        Assert.Equal(11.1m, final.CostUsd);
        Assert.Equal(11.1m, ((ILlmStepResult)result).CostUsd);
    }

    [Fact]
    public async Task Cached_tokens_and_their_discount_add_up_across_tool_iterations()
    {
        var call = new List<ToolCall> { new() { Id = "c1", Name = "echo", Arguments = "{}" } };
        // Solo la segunda vuelta trae cached_tokens: la primera cuenta como 0, no anula la suma.
        var service = new QueuedService(Answer("", 1_000_000, 0, call), Answer("fin", 1_000_000, 0, cached: 500_000));

        var (result, finals) = await RunAsync(service, priced: true, withTool: true, cachedPrice: 0.1m);

        Assert.Equal([null, 500_000], finals.Select(f => f.CachedPromptTokens));
        // 1 $ la primera; 0,5 M a 1 $ + 0,5 M a 0,1 $ la segunda.
        Assert.Equal([1m, 0.55m], finals.Select(f => f.CostUsd ?? -1));
        Assert.Equal(1.55m, ((ILlmStepResult)result).CostUsd);
    }
}
