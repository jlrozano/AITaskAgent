using System.Collections.Concurrent;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;

namespace AITaskAgent.Tests;

/// <summary>
/// ParallelStep used to build a per-branch context and then run the branches on the shared one,
/// so the isolation it set up was thrown away. Branches saw each other's step path and, worse,
/// each other's conversation.
/// </summary>
public class ParallelBranchIsolationTests
{
    /// <summary>
    /// No container. The pipeline attaches itself to the context, so composite steps find the
    /// engine through it rather than through DI. An explicit context is still needed because
    /// PipelineContextFactory.Create is a static that DI has to initialise.
    /// </summary>
    private static (Pipeline Pipeline, PipelineContext Context) Harness()
        => (new Pipeline(), new PipelineContext());

    /// <summary>Records the step path each branch observed while running.</summary>
    private sealed class PathRecordingStep(string name, ConcurrentDictionary<string, string> observed)
        : StepBase(name, typeof(IStepResult), typeof(StepResult<string>))
    {
        protected override Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt,
            IStepResult? lastStepResult, CancellationToken cancellationToken)
        {
            observed[Name] = context.CurrentStepPath;
            return Task.FromResult<IStepResult>(new StepResult<string>(this, Name));
        }
    }

    [Fact]
    public async Task Each_branch_sees_its_own_step_path()
    {
        var observed = new ConcurrentDictionary<string, string>();

        var parallel = new ParallelStep("Fan",
            new PathRecordingStep("BranchA", observed),
            new PathRecordingStep("BranchB", observed));

        var (pipeline, context) = Harness();
        var result = await pipeline.ExecuteAsync(
            "Test", [parallel], new StepResult<string>(new EmptyStep("Seed"), "seed"), context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(2, observed.Count);
        Assert.NotEqual(observed["BranchA"], observed["BranchB"]);
        Assert.Contains("BranchA", observed["BranchA"]);
        Assert.Contains("BranchB", observed["BranchB"]);
    }

    [Fact]
    public async Task Branches_do_not_share_a_conversation()
    {
        var seen = new ConcurrentBag<int>();

        var parallel = new ParallelStep("Fan",
            new ConversationWritingStep("BranchA", seen),
            new ConversationWritingStep("BranchB", seen));

        var (pipeline, context) = Harness();
        await pipeline.ExecuteAsync(
            "Test", [parallel], new StepResult<string>(new EmptyStep("Seed"), "seed"), context);

        // Each branch adds exactly one message to its own clone, so neither may observe the other's.
        Assert.Equal(2, seen.Count);
        Assert.All(seen, count => Assert.Equal(1, count));
    }

    private sealed class ConversationWritingStep(string name, ConcurrentBag<int> seen)
        : StepBase(name, typeof(IStepResult), typeof(StepResult<string>))
    {
        protected override async Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt,
            IStepResult? lastStepResult, CancellationToken cancellationToken)
        {
            context.Conversation.AddUserMessage(Name);
            await Task.Yield();
            seen.Add(context.Conversation.History.Messages.Count);
            return new StepResult<string>(this, Name);
        }
    }
}
