using System.Collections.Concurrent;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;

namespace AITaskAgent.Tests;

/// <summary>
/// CloneForBranch used to reset the branch path to "", so a branch step was cached under
/// "{Branch}/{Branch}" whatever surrounded it. StepResults is shared with the parent, so a
/// ParallelStep inside a LoopStep served iteration 1's result to every later iteration, and two
/// ParallelSteps with a same-named branch collided. The branch path now keeps the parent prefix and
/// the parallel's own name: "{parent}/{Parallel}/{Branch}".
/// </summary>
public class BranchPathCacheTests
{
    private sealed class CountingStep(string name, ConcurrentDictionary<string, int> counts, string counterKey)
        : StepBase(name, typeof(IStepResult), typeof(StepResult<string>))
    {
        protected override Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt,
            IStepResult? lastStepResult, CancellationToken cancellationToken)
        {
            counts.AddOrUpdate(counterKey, 1, (_, n) => n + 1);
            return Task.FromResult<IStepResult>(new StepResult<string>(this, Name));
        }
    }

    private static StepResult<string> Seed() => new(new EmptyStep("Seed"), "seed");

    [Fact]
    public async Task A_parallel_step_inside_a_loop_runs_its_branches_on_every_iteration()
    {
        var counts = new ConcurrentDictionary<string, int>();
        var loop = new LoopStep("Loop",
            [new ParallelStep("Fan", new CountingStep("B", counts, "B"))],
            maxIterations: 3);
        var context = new PipelineContext();

        var result = await new Pipeline().ExecuteAsync("Test", [loop], Seed(), context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(3, counts["B"]);
        // Pins the key layout: loop iteration prefix, then the parallel, then the branch.
        Assert.True(context.StepResults.ContainsKey("Loop/Iter_1/Fan/B/B"));
        Assert.True(context.StepResults.ContainsKey("Loop/Iter_2/Fan/B/B"));
        Assert.True(context.StepResults.ContainsKey("Loop/Iter_3/Fan/B/B"));
    }

    [Fact]
    public async Task Two_parallel_steps_with_same_named_branches_do_not_collide()
    {
        var counts = new ConcurrentDictionary<string, int>();
        var first = new ParallelStep("FanA", new CountingStep("X", counts, "first"));
        var second = new ParallelStep("FanB", new CountingStep("X", counts, "second"));
        var context = new PipelineContext();

        var result = await new Pipeline().ExecuteAsync("Test", [first, second], Seed(), context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(1, counts.GetValueOrDefault("first"));
        Assert.Equal(1, counts.GetValueOrDefault("second"));
        Assert.True(context.StepResults.ContainsKey("FanA/X/X"));
        Assert.True(context.StepResults.ContainsKey("FanB/X/X"));
    }
}
