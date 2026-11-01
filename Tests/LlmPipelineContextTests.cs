using System.Collections.Concurrent;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;

namespace AITaskAgent.Tests;

/// <summary>
/// The conversation lives in <see cref="LlmPipelineContext"/>, not in the base context. Branches
/// must keep that type (with their own copy of the conversation), and a pipeline handed a base
/// context must refuse LLM steps before running anything.
/// </summary>
public class LlmPipelineContextTests
{
    private sealed class CountingService : ILlmService
    {
        public int Calls;

        public string Name => "counting-stub";

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(LlmResult.Success(
                [new LlmResponse { Content = "reply", FinishReason = FinishReason.Stop }]));
        }

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("not exercised by this test");

        public int EstimateTokenCount(string text) => text.Length / 4;
        public int GetMaxContextTokens(string? model = null) => 8192;
    }

    /// <summary>Records the context each branch ran on and writes one message to its conversation.</summary>
    private sealed class ContextRecordingStep(string name, ConcurrentDictionary<string, PipelineContext> seen)
        : StepBase(name, typeof(IStepResult), typeof(StepResult<string>))
    {
        public override Type? RequiredContext => typeof(LlmPipelineContext);

        protected override Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt,
            IStepResult? lastStepResult, CancellationToken cancellationToken)
        {
            seen[Name] = context;
            context.AsLlmContext(Name).Conversation.AddUserMessage(Name);
            return Task.FromResult<IStepResult>(new StepResult<string>(this, Name));
        }
    }

    private sealed class CountingStep(string name, Action onRun)
        : StepBase(name, typeof(IStepResult), typeof(LlmStringStepResult))
    {
        protected override Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt,
            IStepResult? lastStepResult, CancellationToken cancellationToken)
        {
            onRun();
            return Task.FromResult<IStepResult>(new LlmStringStepResult(this) { Content = "hello" });
        }
    }

    private static (IStep Step, CountingService Service) LlmStep(string name = "Ask")
    {
        var service = new CountingService();
        var role = new LlmRole { Provider = new LlmProvider { Service = service }, Model = "test-model" };
        return (new BaseLlmStep<LlmStringStepResult, LlmStringStepResult>(name, role, "Say hi"), service);
    }

    private static StepResult<string> Seed() => new(new EmptyStep("Seed"), "seed");

    [Fact]
    public async Task A_parallel_branch_keeps_the_llm_context_and_gets_a_cloned_conversation()
    {
        var seen = new ConcurrentDictionary<string, PipelineContext>();
        var parallel = new ParallelStep("Fan",
            new ContextRecordingStep("A", seen),
            new ContextRecordingStep("B", seen));
        var context = new LlmPipelineContext();
        context.Conversation.AddUserMessage("before the fan-out");

        var result = await new Pipeline().ExecuteAsync("Test", [parallel], Seed(), context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(2, seen.Count);
        foreach (var (name, branchContext) in seen)
        {
            var llmBranch = Assert.IsType<LlmPipelineContext>(branchContext);
            Assert.NotSame(context, llmBranch);
            Assert.NotSame(context.Conversation, llmBranch.Conversation);
            // The clone starts from the parent's history and adds only its own message.
            Assert.Equal(
                ["before the fan-out", name],
                llmBranch.Conversation.History.Messages.Select(m => m.Content));
            // Shared on purpose: results and metadata aggregate across branches.
            Assert.Same(context.StepResults, llmBranch.StepResults);
            Assert.Same(context.Metadata, llmBranch.Metadata);
        }

        // The branches wrote to their clones, never to the parent's conversation.
        Assert.Single(context.Conversation.History.Messages);
    }

    [Fact]
    public void CloneForBranch_returns_the_derived_type_from_a_base_reference()
    {
        PipelineContext context = new LlmPipelineContext { CorrelationId = "corr" };

        var clone = context.CloneForBranch();

        var llmClone = Assert.IsType<LlmPipelineContext>(clone);
        Assert.Equal("corr", llmClone.CorrelationId);
        Assert.NotSame(((LlmPipelineContext)context).Conversation, llmClone.Conversation);
    }

    [Fact]
    public void Llm_steps_declare_the_llm_context_through_IStep()
    {
        var (step, _) = LlmStep();

        Assert.Equal(typeof(LlmPipelineContext), step.RequiredContext);
        Assert.Null(((IStep)new EmptyStep("Plain")).RequiredContext);
    }

    [Fact]
    public async Task A_base_context_with_an_llm_step_fails_before_any_step_runs()
    {
        var (llmStep, service) = LlmStep();
        var earlierRuns = 0;
        var earlier = new CountingStep("Earlier", () => earlierRuns++);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Pipeline().ExecuteAsync("Test", [earlier, llmStep], Seed(), new PipelineContext()));

        Assert.Contains("'Ask'", ex.Message);
        Assert.Contains(nameof(LlmPipelineContext), ex.Message);
        Assert.Contains(nameof(PipelineContext), ex.Message);
        Assert.Equal(0, earlierRuns);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task The_check_reaches_llm_steps_nested_in_composite_steps()
    {
        var (llmStep, service) = LlmStep("Nested");
        var group = new GroupStep("Group", [new ParallelStep("Fan", new LoopStep("Loop", [llmStep], maxIterations: 1))]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Pipeline().ExecuteAsync("Test", [group], Seed(), new PipelineContext()));

        Assert.Contains("'Nested'", ex.Message);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task An_llm_step_chosen_at_run_time_fails_with_the_same_message_instead_of_running()
    {
        // SwitchStep picks its target at run time, so only the per-step check can see it.
        var (llmStep, service) = LlmStep("Routed");
        var router = new SwitchStep<StepResult<string>>("Router", (input, _) => (llmStep, input));

        var result = await new Pipeline().ExecuteAsync("Test", [router], Seed(), new PipelineContext());

        Assert.True(result.HasError);
        Assert.Contains("'Routed'", result.Error!.Message);
        Assert.Contains(nameof(LlmPipelineContext), result.Error.Message);
        Assert.Equal(0, service.Calls);
    }
}
