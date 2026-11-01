using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Results;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using Newtonsoft.Json.Linq;

namespace AITaskAgent.Tests;

/// <summary>Generic hooks of <see cref="StepBase"/>: post-process, finalize, enrich, validators and result validators.</summary>
public sealed class StepHooksTests
{
    private static StepResult<string> Seed() => new(new EmptyStep("Seed"), "seed");

    private static Task<IStepResult> Run(IStep step, PipelineContext? context = null)
        => new Pipeline().ExecuteAsync("Test", [step], Seed(), context ?? new PipelineContext());

    // ── postProcess ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PostProcess_can_replace_the_result_and_runs_before_validation()
    {
        HookProbeStep step = new()
        {
            Hooks = new StepHooks
            {
                PostProcess = async (args, next) =>
                {
                    IStepResult original = await next();
                    return new JsonStepResult(args.Step, JToken.Parse("{\"v\":2}"));
                },
            },
        };
        step.AddResultValidator(new DelegateResultValidator("sees-replaced", "HookProbe",
            a => Task.FromResult(((a.Value as JToken)?["v"]?.Value<int>() == 2, (string?)"validator saw the original result"))));

        IStepResult result = await Run(step);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(2, ((JToken)result.Value!)["v"]!.Value<int>());
    }

    [Fact]
    public async Task PostProcess_runs_once_per_successful_attempt_and_never_for_error_results()
    {
        int calls = 0;
        HookProbeStep step = new()
        {
            FailFirst = 1,
            Hooks = new StepHooks { PostProcess = (args, next) => { calls++; return next(); } },
        };

        IStepResult result = await Run(step);

        Assert.False(result.HasError);
        Assert.Equal(2, step.Executions);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PostProcess_returning_an_incompatible_result_fails_the_step_without_retry()
    {
        HookProbeStep step = new()
        {
            Hooks = new StepHooks { PostProcess = (args, next) => Task.FromResult<IStepResult>(new StepResult<string>(args.Step, "text")) },
        };

        IStepResult result = await Run(step);

        Assert.True(result.HasError);
        Assert.IsType<StepHookException>(result.Error!.OriginalException);
        Assert.Contains("not assignable", result.Error.Message);
        Assert.Equal(1, step.Executions);
    }

    [Fact]
    public async Task PostProcess_that_throws_is_a_fatal_error_naming_the_hook()
    {
        HookProbeStep step = new()
        {
            Hooks = new StepHooks { PostProcess = (_, _) => throw new InvalidOperationException("boom") },
        };

        IStepResult result = await Run(step);

        StepHookException hook = Assert.IsType<StepHookException>(result.Error!.OriginalException);
        Assert.Equal(nameof(StepHooks.PostProcess), hook.Hook);
        Assert.Equal(1, step.Executions);
    }

    [Fact]
    public async Task An_override_of_PostProcessAsync_that_skips_base_replaces_the_hook()
    {
        int hookCalls = 0;
        SkippingOverride step = new() { Hooks = new StepHooks { PostProcess = (_, next) => { hookCalls++; return next(); } } };

        await Run(step);

        Assert.Equal(0, hookCalls);
        Assert.Equal(1, step.OverrideCalls);
    }

    private sealed class SkippingOverride : HookProbeStep
    {
        public int OverrideCalls;

        protected override Task<IStepResult> PostProcessAsync(IStepResult input, IStepResult result, PipelineContext context, int attempt, CancellationToken cancellationToken)
        {
            OverrideCalls++;
            return Task.FromResult(result);
        }
    }

    // ── onFinalize ───────────────────────────────────────────────────────────

    [Fact]
    public async Task OnFinalize_fires_once_after_the_last_attempt_with_the_final_result()
    {
        List<bool> seen = [];
        HookProbeStep step = new()
        {
            FailFirst = 2,
            Hooks = new StepHooks { OnFinalize = args => { seen.Add(args.Result.HasError); return Task.CompletedTask; } },
        };

        await Run(step);

        Assert.Equal(3, step.Executions);
        Assert.Equal([false], seen);
    }

    [Fact]
    public async Task OnFinalize_fires_for_an_exhausted_step_with_the_error_result()
    {
        List<bool> seen = [];
        HookProbeStep step = new()
        {
            FailFirst = 99,
            Hooks = new StepHooks { OnFinalize = args => { seen.Add(args.Result.HasError); return Task.CompletedTask; } },
        };

        IStepResult result = await Run(step);

        Assert.True(result.HasError);
        Assert.Equal([true], seen);
    }

    [Fact]
    public async Task OnFinalize_exception_propagates_as_StepHookException()
    {
        HookProbeStep step = new() { Hooks = new StepHooks { OnFinalize = _ => throw new InvalidOperationException("late") } };

        IStepResult result = await Run(step);

        // The pipeline turns what escapes a step into an error result that keeps the exception.
        StepHookException ex = Assert.IsType<StepHookException>(result.Error!.OriginalException);
        Assert.Equal(nameof(StepHooks.OnFinalize), ex.Hook);
    }

    // ── enrich ───────────────────────────────────────────────────────────────

    [Fact]
    public void Enrich_hooks_transform_the_events_and_swallow_their_own_exceptions()
    {
        HookProbeStep ok = new()
        {
            Hooks = new StepHooks
            {
                EnrichStarted = args => args.BaseEvent with { AdditionalData = new Dictionary<string, object> { ["tag"] = "x" } },
            },
        };
        StepStartedEvent started = new() { StepName = "HookProbe", CorrelationId = "c" };

        StepStartedEvent enriched = ((IEnrichable)ok).EnrichStartedEvent(started, Seed(), new PipelineContext());

        Assert.Equal("x", enriched.AdditionalData!["tag"]);

        HookProbeStep failing = new() { Hooks = new StepHooks { EnrichStarted = _ => throw new InvalidOperationException("tags broke") } };
        StepStartedEvent fallback = ((IEnrichable)failing).EnrichStartedEvent(started, Seed(), new PipelineContext());

        Assert.Same(started, fallback);
    }

    [Fact]
    public void EnrichActivity_hook_failures_do_not_escape()
    {
        HookProbeStep step = new() { Hooks = new StepHooks { EnrichActivityBefore = _ => throw new InvalidOperationException("x") } };

        ((IEnrichable)step).EnrichActivityBefore(null, Seed(), new PipelineContext());
    }

    // ── validators (semantic) ────────────────────────────────────────────────

    [Fact]
    public async Task A_failing_validator_retries_with_a_Semantic_validation_event()
    {
        CapturingChannel channel = new();
        int validations = 0;
        HookProbeStep step = new() { MaxRetries = 2 };
        step.AddValidator(new DelegateValidator("never-ok", step, _ => { validations++; return Task.FromResult((false, (string?)"nope")); }));

        IStepResult result = await Run(step, new PipelineContext(channel));

        Assert.True(result.HasError);
        Assert.Equal(2, validations);
        Assert.All(channel.Validations, v => Assert.Equal("Semantic", v.ValidationType, ignoreCase: true));
        Assert.Equal(2, channel.Validations.Count);
    }

    [Fact]
    public async Task A_validator_that_throws_ends_the_step_without_retry()
    {
        HookProbeStep step = new();
        step.AddValidator(new DelegateValidator("throws", step, _ => throw new InvalidOperationException("validator bug")));

        IStepResult result = await Run(step);

        Assert.True(result.HasError);
        StepHookException hook = Assert.IsType<StepHookException>(result.Error!.OriginalException);
        Assert.Equal("throws", hook.Hook);
        Assert.Equal(1, step.Executions);
    }

    // ── result validators ────────────────────────────────────────────────────

    [Fact]
    public async Task A_failing_result_validator_retries_in_the_Structural_slot_and_runs_once_per_attempt()
    {
        CapturingChannel channel = new();
        int runs = 0;
        HookProbeStep step = new() { MaxRetries = 3 };
        step.AddResultValidator(new DelegateResultValidator("needs-2", "HookProbe", a =>
        {
            runs++;
            return Task.FromResult(runs >= 2 ? (true, (string?)null) : (false, "not yet"));
        }));

        IStepResult result = await Run(step, new PipelineContext(channel));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(2, runs);
        StepValidationEvent failure = Assert.Single(channel.Validations);
        Assert.Equal("Structural", failure.ValidationType, ignoreCase: true);
        Assert.Equal("not yet", failure.ValidationError);
    }

    [Fact]
    public async Task Result_validators_run_after_the_results_own_check_and_in_declaration_order()
    {
        List<string> order = [];
        HookProbeStep step = new();
        step.AddResultValidator(new DelegateResultValidator("a", "HookProbe", _ => { order.Add("a"); return Task.FromResult((true, (string?)null)); }));
        step.AddResultValidator(new DelegateResultValidator("b", "HookProbe", _ => { order.Add("b"); return Task.FromResult((true, (string?)null)); }));

        await Run(step);

        Assert.Equal(["a", "b"], order);
    }

    [Fact]
    public async Task The_attached_validators_of_the_latest_owner_replace_those_of_an_earlier_step()
    {
        int upstream = 0;
        HookProbeStep first = new();
        first.AddResultValidator(new DelegateResultValidator("first", "HookProbe", _ => { upstream++; return Task.FromResult((true, (string?)null)); }));
        StepResult stamped = (StepResult)await ((IStep)first).ExecuteAsync(Seed(), new PipelineContext(), 1, null, default);
        Assert.Single(stamped.ResultValidators);

        // A passthrough step that has none re-stamps the very same object with an empty list.
        PassThrough pass = new();
        StepResult passed = (StepResult)await ((IStep)pass).ExecuteAsync(stamped, new PipelineContext(), 1, null, default);

        Assert.Same(stamped, passed);
        Assert.Empty(passed.ResultValidators);
        await passed.ValidateAsync(new PipelineContext());
        Assert.Equal(0, upstream);
    }

    private sealed class PassThrough() : StepBase("Pass", typeof(IStepResult), typeof(IStepResult))
    {
        protected override Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
            => Task.FromResult(input);
    }

    [Fact]
    public async Task Result_validators_are_honoured_by_every_framework_result_type()
    {
        int runs = 0;
        DelegateResultValidator counter = new("count", "x", _ => { runs++; return Task.FromResult((true, (string?)null)); });
        EmptyStep step = new("x");

        JsonStepResult json = new(step, JToken.Parse("{}"));
        LlmJsonStepResult llmJson = new(step) { Value = JToken.Parse("{}") };
        LlmStringStepResult llmString = new(step) { Content = "hi" };
        foreach (StepResult result in new StepResult[] { json, llmJson, llmString })
        {
            result.AttachResultValidators([counter]);
            Assert.True((await result.ValidateAsync(new PipelineContext())).IsValid);
        }

        Assert.Equal(3, runs);
    }

    [Fact]
    public async Task The_results_own_failure_wins_over_attached_validators()
    {
        int runs = 0;
        LlmStringStepResult empty = new(new EmptyStep("x"));
        empty.AttachResultValidators([new DelegateResultValidator("count", "x", _ => { runs++; return Task.FromResult((true, (string?)null)); })]);

        (bool isValid, string? error) = await empty.ValidateAsync(new PipelineContext());

        Assert.False(isValid);
        Assert.Contains("Content or ToolCalls", error);
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task TypedStep_results_are_stamped_too()
    {
        TypedHookProbe step = new();
        step.AddResultValidator(new DelegateResultValidator("never", "TypedHookProbe", _ => Task.FromResult((false, (string?)"typed rejected"))));
        step.MaxRetries = 1;

        IStepResult result = await new Pipeline().ExecuteAsync("Test", [step], new JsonStepResult(new EmptyStep("Seed"), JToken.Parse("{}")), new PipelineContext());

        Assert.True(result.HasError);
        Assert.Equal("typed rejected", result.Error!.Message);
    }

    [Fact]
    public async Task A_result_validator_that_throws_ends_the_step_without_retry()
    {
        HookProbeStep step = new();
        step.AddResultValidator(new DelegateResultValidator("bug", "HookProbe", _ => throw new InvalidOperationException("oops")));

        IStepResult result = await Run(step);

        Assert.IsType<StepHookException>(result.Error!.OriginalException);
        Assert.Equal(1, step.Executions);
    }

    // ── re-entrancy ──────────────────────────────────────────────────────────

    [Fact]
    public async Task One_step_instance_with_hooks_serves_concurrent_runs_without_mixing_their_state()
    {
        HookProbeStep step = new()
        {
            Hooks = new StepHooks
            {
                PostProcess = async (args, next) =>
                {
                    await Task.Delay(5);
                    args.Context.Metadata["seen"] = args.Context.CorrelationId;
                    return await next();
                },
            },
        };

        PipelineContext[] contexts = [.. Enumerable.Range(0, 24).Select(_ => new PipelineContext())];
        await Task.WhenAll(contexts.Select(c => Run(step, c)));

        Assert.All(contexts, c => Assert.Equal(c.CorrelationId, c.Metadata["seen"]));
    }

    [Fact]
    public async Task Hooks_run_inside_parallel_branches()
    {
        int calls = 0;
        HookProbeStep a = new() { Hooks = new StepHooks { PostProcess = (_, next) => { Interlocked.Increment(ref calls); return next(); } } };
        HookProbeStep b = new() { Hooks = new StepHooks { PostProcess = (_, next) => { Interlocked.Increment(ref calls); return next(); } } };
        a.Name = "A";
        b.Name = "B";
        ParallelStep parallel = new("P", a, b);

        IStepResult result = await new Pipeline().ExecuteAsync("Test", [parallel], Seed(), new PipelineContext());

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(2, calls);
    }
}
