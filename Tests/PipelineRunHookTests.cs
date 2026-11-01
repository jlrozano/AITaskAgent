using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;

namespace AITaskAgent.Tests;

/// <summary>
/// <see cref="IPipelineRunHook"/>: called once per run, before any step, with the known steps that name it and their paths;
/// what it returns is released when the run ends, whatever the outcome; an exception fails the run before any step runs.
/// </summary>
public class PipelineRunHookTests
{
    private sealed class RecordingHook(bool fail = false) : IPipelineRunHook
    {
        public List<List<string>> Calls { get; } = [];

        public int Released { get; private set; }

        public Task<IAsyncDisposable?> RunStartingAsync(
            IReadOnlyList<KeyValuePair<string, IStep>> steps, PipelineContext context, CancellationToken cancellationToken)
        {
            Calls.Add([.. steps.Select(s => s.Key)]);
            if (fail)
            {
                throw new InvalidOperationException("prepare failed");
            }

            return Task.FromResult<IAsyncDisposable?>(new Release(() => Released++));
        }

        private sealed class Release(Action onRelease) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                onRelease();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class HookedStep(string name, IPipelineRunHook hook, List<string> ran, bool error = false)
        : StepBase(name, typeof(IStepResult), typeof(StepResult<string>)), IPipelineRunHookProvider
    {
        public IPipelineRunHook RunHook => hook;

        protected override Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
        {
            lock (ran)
            {
                ran.Add(Name);
            }

            return Task.FromResult<IStepResult>(error
                ? ErrorStepResult.FromMessage(this, "boom")
                : new StepResult<string>(this, Name));
        }
    }

    private static StepResult<string> Seed() => new(new EmptyStep("Seed"), "seed");

    [Fact]
    public async Task The_hook_is_called_once_with_every_known_step_that_names_it_and_released_at_the_end()
    {
        RecordingHook hook = new();
        List<string> ran = [];
        IStep[] steps =
        [
            new HookedStep("top", hook, ran),
            new GroupStep("group", [new HookedStep("inGroup", hook, ran)]),
            new LoopStep("loop", [new HookedStep("inLoop", hook, ran)], maxIterations: 2),
            new ParallelStep("fan", new HookedStep("branch", hook, ran)),
        ];

        IStepResult result = await new Pipeline().ExecuteAsync("Test", steps, Seed(), new PipelineContext());

        Assert.False(result.HasError, result.Error?.Message);
        List<string> paths = Assert.Single(hook.Calls);
        Assert.Equal(["top", "group/inGroup", "loop/inLoop", "fan/branch"], paths);
        Assert.Equal(1, hook.Released);
        Assert.Equal(2, ran.Count(n => n == "inLoop"));
    }

    [Fact]
    public async Task What_the_hook_prepared_is_released_when_the_run_fails()
    {
        RecordingHook hook = new();
        List<string> ran = [];

        IStepResult result = await new Pipeline().ExecuteAsync("Test", [new HookedStep("bad", hook, ran, error: true)], Seed(), new PipelineContext());

        Assert.True(result.HasError);
        Assert.Equal(1, hook.Released);
    }

    [Fact]
    public async Task A_hook_that_throws_fails_the_run_before_any_step_runs()
    {
        RecordingHook hook = new(fail: true);
        List<string> ran = [];

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new Pipeline().ExecuteAsync("Test", [new HookedStep("a", hook, ran)], Seed(), new PipelineContext()));

        Assert.Equal("prepare failed", ex.Message);
        Assert.Empty(ran);
    }

    [Fact]
    public async Task Steps_that_name_no_hook_cost_nothing_and_each_run_calls_the_hook_again()
    {
        RecordingHook hook = new();
        List<string> ran = [];
        IStep[] steps = [new HookedStep("a", hook, ran)];

        await new Pipeline().ExecuteAsync("Test", steps, Seed(), new PipelineContext());
        await new Pipeline().ExecuteAsync("Test", steps, Seed(), new PipelineContext());

        Assert.Equal(2, hook.Calls.Count);
        Assert.Equal(2, hook.Released);
    }

    private sealed class PlainStep(string name) : StepBase(name, typeof(IStepResult), typeof(StepResult<string>))
    {
        protected override Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
            => Task.FromResult<IStepResult>(new StepResult<string>(this, Name));
    }

    /// <summary>The target of a hook's delegate (a JavaScript hook, say) names the run hook like a validator does.</summary>
    private sealed class HookTarget(IPipelineRunHook hook) : IPipelineRunHookProvider
    {
        public IPipelineRunHook RunHook => hook;

        public Task OnFinalize(AITaskAgent.Core.Hooks.FinalizeArgs args) => Task.CompletedTask;
    }

    [Fact]
    public async Task The_target_of_a_steps_hook_names_the_run_hook_and_the_step_is_passed_once()
    {
        RecordingHook hook = new();
        HookTarget target = new(hook);
        PlainStep plain = new("plain") { Hooks = new AITaskAgent.Core.Hooks.StepHooks { OnFinalize = target.OnFinalize } };
        PlainStep twice = new("twice")
        {
            Hooks = new AITaskAgent.Core.Hooks.StepHooks
            {
                OnFinalize = target.OnFinalize,
                PostProcess = AITaskAgent.Core.Hooks.HookForms.After<AITaskAgent.Core.Hooks.PostProcessArgs, IStepResult>(_ => Task.CompletedTask),
            },
        };

        IStepResult result = await new Pipeline().ExecuteAsync("Test", [plain, twice, new PlainStep("none")], Seed(), new PipelineContext());

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(["plain", "twice"], Assert.Single(hook.Calls));
        Assert.Equal(1, hook.Released);
    }
}
