using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;

namespace AITaskAgent.Tests;

/// <summary>
/// <see cref="ParallelStep.FailFast"/>: <c>true</c> (default) keeps the aggregate error; <c>false</c> lets the step succeed while
/// every failed branch keeps its own error in <see cref="ParallelResult.Value"/> and in its branch event.
/// </summary>
public class ParallelStepFailFastTests
{
    private sealed class BranchStep(string name, Func<IStep, IStepResult> body)
        : StepBase(name, typeof(IStepResult), typeof(StepResult<string>))
    {
        public BranchStep(string name) : this(name, _ => throw new InvalidOperationException("unused"))
        {
        }

        protected override Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
            => Task.FromResult(body(this));
    }

    private static IStep Ok(string name) => new BranchStep(name, s => new StepResult<string>(s, $"{name}-done")) { MaxRetries = 1 };

    // Returns an error result with an exception: not retried.
    private static IStep Fails(string name) => new BranchStep(name, s => ErrorStepResult.FromException(s, new InvalidOperationException($"{name} broke"))) { MaxRetries = 1 };

    private static IStep Throws(string name) => new BranchStep(name, _ => throw new InvalidOperationException($"{name} threw")) { MaxRetries = 1 };

    private static async Task<(IStepResult Result, List<BranchCompletedEvent> Events)> RunAsync(ParallelStep step)
    {
        ConcurrentBag<BranchCompletedEvent> events = [];
        EventChannel channel = new();
        using (channel.Subscribe((e, _) =>
        {
            if (e is BranchCompletedEvent completed)
            {
                events.Add(completed);
            }

            return Task.CompletedTask;
        }))
        {
            IStepResult result = await new Pipeline().ExecuteAsync(
                "Test", [step], new StepResult<string>(new EmptyStep("Seed"), "seed"), new PipelineContext(channel));
            await channel.DisposeAsync(); // delivers what is still buffered
            return (result, [.. events]);
        }
    }

    [Fact]
    public void FailFast_defaults_to_true()
        => Assert.True(new ParallelStep("Fan").FailFast);

    [Fact]
    public async Task With_failFast_true_a_failed_branch_makes_the_step_fail_as_before()
    {
        var (result, _) = await RunAsync(new ParallelStep("Fan", Ok("A"), Fails("B")));

        Assert.True(result.HasError);
        Assert.Contains("Step B error", result.Error!.Message);
        var parallel = Assert.IsType<ParallelResult>(result);
        Assert.False(parallel.Value["A"].HasError);
        Assert.True(parallel.Value["B"].HasError);
    }

    [Fact]
    public async Task With_failFast_false_the_step_succeeds_and_each_failed_branch_keeps_its_error()
    {
        var (result, events) = await RunAsync(new ParallelStep("Fan", Ok("A"), Fails("B"), Throws("C"), Ok("D")) { FailFast = false });

        Assert.False(result.HasError, result.Error?.Message);
        var parallel = Assert.IsType<ParallelResult>(result);
        Assert.Equal(["A", "B", "C", "D"], parallel.Value.Keys.Order());
        Assert.Equal("A-done", parallel.Value["A"].Value);
        Assert.Equal("D-done", parallel.Value["D"].Value);
        Assert.Equal("B broke", parallel.Value["B"].Error!.Message);
        Assert.Contains("C threw", parallel.Value["C"].Error!.Message);
        Assert.Equal(["B", "C"], parallel.Value.Where(kv => kv.Value.HasError).Select(kv => kv.Key).Order());

        Assert.Equal(["A", "D"], events.Where(e => e.Success).Select(e => e.BranchName).Order());
        Assert.Equal(["B", "C"], events.Where(e => !e.Success).Select(e => e.BranchName).Order());
        Assert.All(events.Where(e => !e.Success), e => Assert.False(string.IsNullOrEmpty(e.ErrorMessage)));
    }

    [Fact]
    public async Task With_failFast_false_even_a_total_failure_is_not_an_aggregate_error()
    {
        var (result, _) = await RunAsync(new ParallelStep("Fan", Fails("A"), Fails("B")) { FailFast = false });

        Assert.False(result.HasError);
        Assert.All(Assert.IsType<ParallelResult>(result).Value.Values, r => Assert.True(r.HasError));
    }

    [Fact]
    public async Task It_is_declared_in_YAML_and_defaults_to_true()
    {
        static string Yaml(string name, string failFast) => $$"""
            name: {{name}}
            steps:
              - id: fan
                type: ParallelStep
            {{failFast}}
                branches:
                  - { id: good, type: Probe, label: good }
                  - { id: bad, type: ExecStep, executable: 'no-such-file-execstep.exe' }
            """;
        YamlTestKit kit = new();

        var tolerant = kit.Factory.CreateFromYaml(Yaml("tolerant", "    failFast: false"));
        var strict = kit.Factory.CreateFromYaml(Yaml("strict", ""));

        IStepResult ok = await tolerant.ExecuteAsync(new JObject(), YamlTestKit.NewContext());
        IStepResult failed = await strict.ExecuteAsync(new JObject(), YamlTestKit.NewContext());

        Assert.False(ok.HasError, ok.Error?.Message);
        var parallel = Assert.IsType<ParallelResult>(ok);
        Assert.False(parallel.Value["good"].HasError);
        Assert.True(parallel.Value["bad"].HasError);
        Assert.True(failed.HasError);
    }
}
