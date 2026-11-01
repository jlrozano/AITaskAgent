using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace AITaskAgent.Tests;

/// <summary>
/// <see cref="IValidatorFactory"/> (2026-10-23): validators named by a prefix that no registry lists (<c>js:</c>) are built by the factory of
/// their prefix; a validator that names a run hook (<see cref="IPipelineRunHookProvider"/>) gets it called for its step.
/// </summary>
public sealed class ValidatorFactoryTests
{
    private sealed class PrefixFactory(Func<string, bool> accept) : IValidatorFactory
    {
        public List<(string Name, string Step, bool Result)> Built { get; } = [];

        public string Prefix => "fake:";

        public IStepValidator CreateValidator(string name, StepBase step)
        {
            Built.Add((name, step.Name, false));
            return accept(name) ? new NamedValidator(name, name.EndsWith("no", StringComparison.Ordinal)) : throw new InvalidOperationException("not a fake I know.");
        }

        public IResultValidator CreateResultValidator(string name, StepBase step)
        {
            Built.Add((name, step.Name, true));
            return new NamedValidator(name, reject: false);
        }
    }

    private sealed class NamedValidator(string id, bool reject) : IStepValidator, IResultValidator
    {
        public string Id => id;

        public Task<(bool IsValid, string? Error)> ValidateAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
            => Task.FromResult(reject ? (false, $"{id} says no") : (true, (string?)null));
    }

    [Fact]
    public async Task A_prefixed_name_is_built_by_the_factory_of_its_prefix_for_both_keys()
    {
        PrefixFactory factory = new(_ => true);
        HookKit kit = new(configure: s => s.AddSingleton<IValidatorFactory>(factory));

        IStepResult result = await kit.RunAsync("""
            name: p
            steps:
              - id: A
                type: HookProbe
                validators: [fake:yes]
                resultValidators: [fake:shape]
            """);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal([("fake:yes", "A", false), ("fake:shape", "A", true)], factory.Built);
    }

    [Fact]
    public async Task A_rejection_of_a_built_validator_fails_the_step_like_any_validator()
    {
        HookKit kit = new(configure: s => s.AddSingleton<IValidatorFactory>(new PrefixFactory(_ => true)));

        IStepResult result = await kit.RunAsync("""
            name: p
            steps:
              - id: A
                type: HookProbe
                validators: [fake:no]
            """);

        Assert.True(result.HasError);
        Assert.Contains("fake:no says no", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_error_of_the_factory_is_a_build_error_naming_the_step_key_and_name()
    {
        HookKit kit = new(configure: s => s.AddSingleton<IValidatorFactory>(new PrefixFactory(_ => false)));

        InvalidOperationException error = kit.BuildError("""
            name: p
            steps:
              - id: A
                type: HookProbe
                validators: [fake:bad]
            """);

        Assert.Equal("Step 'A' (type 'HookProbe'): validator 'fake:bad': not a fake I know.", error.Message);
    }

    [Fact]
    public void Without_a_factory_a_prefixed_name_is_an_unknown_validator_as_before()
    {
        HookKit kit = new();

        InvalidOperationException error = kit.BuildError("""
            name: p
            steps:
              - id: A
                type: HookProbe
                validators: [fake:x]
            """);

        Assert.Contains("unknown step validator 'fake:x'", error.Message, StringComparison.Ordinal);
    }

    private sealed class HookedValidator(IPipelineRunHook hook) : IStepValidator, IPipelineRunHookProvider
    {
        public string Id => "hooked";

        public IPipelineRunHook RunHook => hook;

        public Task<(bool IsValid, string? Error)> ValidateAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
            => Task.FromResult((true, (string?)null));
    }

    private sealed class CountingHook : IPipelineRunHook
    {
        public List<string> Steps { get; } = [];

        public int Released { get; private set; }

        public Task<IAsyncDisposable?> RunStartingAsync(IReadOnlyList<KeyValuePair<string, IStep>> steps, PipelineContext context, CancellationToken cancellationToken)
        {
            Steps.AddRange(steps.Select(s => s.Key));
            return Task.FromResult<IAsyncDisposable?>(new Release(this));
        }

        private sealed class Release(CountingHook owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                owner.Released++;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class PlainStep(string name) : StepBase(name, typeof(IStepResult), typeof(StepResult<string>))
    {
        protected override Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
            => Task.FromResult<IStepResult>(new StepResult<string>(this, Name));
    }

    [Fact]
    public async Task A_validator_that_names_a_run_hook_gets_it_called_once_for_its_step()
    {
        CountingHook hook = new();
        PlainStep validated = new("validated");
        validated.AddValidator(new HookedValidator(hook));
        validated.AddValidator(new HookedValidator(hook));
        PlainStep plain = new("plain");
        IStep[] steps = [plain, new GroupStep("group", [validated])];

        IStepResult result = await new Pipeline().ExecuteAsync("Test", steps, new StepResult<string>(new EmptyStep("Seed"), "seed"), new PipelineContext());

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(["group/validated"], hook.Steps);
        Assert.Equal(1, hook.Released);
    }
}
