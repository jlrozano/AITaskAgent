using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;

namespace AITaskAgent.Tests;

/// <summary>
/// Results are cached per step so that resuming a suspended run can fast-forward over work already
/// done. This pins that fast-forward: re-running with the same context must not execute anything
/// twice, children injected through NextSteps included.
/// </summary>
/// <remarks>
/// Note this does not isolate the <c>NextSteps.Clear()</c> on the cache path. Because a result is
/// only cached once the step and all its children have succeeded, a cached parent implies cached
/// children, and those are skipped on their own account. The Clear is defensive: it matters when a
/// child is absent from the cache, which is the suspend-and-resume case, and that needs a test
/// exercising an actual suspension.
/// </remarks>
public class PipelineCacheTests
{
    /// <summary>Counts executions and, on the first run, injects a child step.</summary>
    private sealed class ParentStep(string name, Counter counter, IStep child)
        : StepBase(name, typeof(IStepResult), typeof(StepResult<string>))
    {
        protected override Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt,
            IStepResult? lastStepResult, CancellationToken cancellationToken)
        {
            counter.Parent++;
            var result = new StepResult<string>(this, "done");
            result.NextSteps.Add(child);
            return Task.FromResult<IStepResult>(result);
        }
    }

    private sealed class ChildStep(string name, Counter counter)
        : StepBase(name, typeof(IStepResult), typeof(StepResult<string>))
    {
        protected override Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt,
            IStepResult? lastStepResult, CancellationToken cancellationToken)
        {
            counter.Child++;
            return Task.FromResult<IStepResult>(new StepResult<string>(this, "child"));
        }
    }

    private sealed class Counter
    {
        public int Parent;
        public int Child;
    }

    [Fact]
    public async Task Re_running_with_the_same_context_executes_nothing_twice()
    {
        var counter = new Counter();
        var child = new ChildStep("Child", counter);
        var parent = new ParentStep("Parent", counter, child);

        var pipeline = new Pipeline();
        var context = new PipelineContext();
        var input = new StepResult<string>(new EmptyStep("Seed"), "seed");

        await pipeline.ExecuteAsync("Test", [parent], input, context);
        Assert.Equal(1, counter.Parent);
        Assert.Equal(1, counter.Child);

        // Same context: both results are cached, so neither step may run again.
        await pipeline.ExecuteAsync("Test", [parent], input, context);

        Assert.Equal(1, counter.Parent);
        Assert.Equal(1, counter.Child);
    }
}
