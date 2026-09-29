using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;

namespace AITaskAgent.Tests;

/// <summary>
/// <see cref="ParallelStep.MaxDegreeOfParallelism"/> caps how many branches run at once (nine auditors used to hit the
/// same LLM deployment together). Unset, the step keeps what it always did: one branch per processor.
/// </summary>
public class ParallelStepConcurrencyTests
{
    /// <summary>Counts the branches running at the same time, and the most seen at once.</summary>
    private sealed class ConcurrencyProbe
    {
        private int _running;
        private int _max;

        public int Max => Volatile.Read(ref _max);

        public async Task RunAsync(CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _running);
            int seen;
            while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen)
            {
            }
            await Task.Delay(150, ct);
            Interlocked.Decrement(ref _running);
        }
    }

    private sealed class ProbeStep(string name, ConcurrencyProbe probe)
        : StepBase(name, typeof(IStepResult), typeof(StepResult<string>))
    {
        protected override async Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt,
            IStepResult? lastStepResult, CancellationToken cancellationToken)
        {
            await probe.RunAsync(cancellationToken);
            return new StepResult<string>(this, Name);
        }
    }

    private static async Task<(IStepResult Result, int Max)> RunAsync(int branches, int? maxDegree)
    {
        var probe = new ConcurrencyProbe();
        var steps = Enumerable.Range(1, branches).Select(i => (IStep)new ProbeStep($"Branch{i}", probe)).ToArray();
        var parallel = maxDegree is { } max
            ? new ParallelStep("Fan", steps) { MaxDegreeOfParallelism = max }
            : new ParallelStep("Fan", steps);

        var result = await new Pipeline().ExecuteAsync(
            "Test", [parallel], new StepResult<string>(new EmptyStep("Seed"), "seed"), new PipelineContext());
        return (result, probe.Max);
    }

    [Fact]
    public async Task The_limit_caps_the_branches_running_at_once()
    {
        var (result, max) = await RunAsync(branches: 6, maxDegree: 2);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(2, max);
        Assert.Equal(6, Assert.IsType<ParallelResult>(result).Value.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Without_a_limit_the_branches_run_one_per_processor(int? maxDegree)
    {
        // Tantas ramas como procesadores (al menos dos): sin límite propio arrancan todas a la vez, como antes.
        var branches = Math.Max(2, Math.Min(Environment.ProcessorCount, 8));

        var (result, max) = await RunAsync(branches, maxDegree);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(Math.Min(branches, Environment.ProcessorCount), max);
    }

    [Fact]
    public void The_default_is_no_limit_of_its_own()
    {
        Assert.Equal(0, new ParallelStep().MaxDegreeOfParallelism);
    }
}
