using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Steps;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using NJsonSchema;
using System.ComponentModel.DataAnnotations;

namespace AITaskAgent.Tests;

/// <summary>YAML keys <c>hooks:</c>, <c>validators:</c> and <c>resultValidators:</c> bound against the hook registry.</summary>
public sealed class HookBindingTests
{
    private static Func<ValidateArgs, Task<(bool IsValid, string? Error)>> Validator(bool ok, string? error = null)
        => _ => Task.FromResult((ok, error));

    private static Func<ResultValidateArgs, Task<(bool IsValid, string? Error)>> ResultValidator(bool ok, string? error = null)
        => _ => Task.FromResult((ok, error));

    private const string Probe = """
        name: p
        steps:
          - id: A
            type: HookProbe
        """;

    // ── hooks ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_named_hook_is_bound_to_its_slot_and_runs()
    {
        HookKit kit = new();
        int calls = 0;
        kit.Hooks.Add("Audit.Tag", HookKind.Hook, (HookAround<PostProcessArgs, IStepResult>)((args, next) => { calls++; return next(); }));

        IStepResult result = await kit.RunAsync("""
            name: p
            steps:
              - id: A
                type: HookProbe
                hooks:
                  postProcess: Audit.Tag
            """);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void An_unknown_hook_name_lists_the_available_ones()
    {
        HookKit kit = new();
        kit.Hooks.Add("Known.One", HookKind.Hook, (Func<FinalizeArgs, Task>)(_ => Task.CompletedTask));

        InvalidOperationException error = kit.BuildError("""
            name: p
            steps:
              - id: A
                type: HookProbe
                hooks:
                  onFinalize: Nope
            """);

        Assert.Contains("unknown hook 'Nope'", error.Message);
        Assert.Contains("Known.One", error.Message);
        Assert.Contains("(line", error.Message);
    }

    [Fact]
    public void A_hook_of_the_wrong_kind_says_so()
    {
        HookKit kit = new();
        kit.Hooks.Add("Check", HookKind.StepValidator, Validator(true));

        InvalidOperationException error = kit.BuildError("""
            name: p
            steps:
              - id: A
                type: HookProbe
                hooks:
                  onFinalize: Check
            """);

        Assert.Contains("'Check' is a step validator, not", error.Message);
    }

    [Fact]
    public void A_hook_with_the_wrong_signature_shows_both_signatures()
    {
        HookKit kit = new();
        kit.Hooks.Add("Wrong", HookKind.Hook, (Func<PostProcessArgs, Task>)(_ => Task.CompletedTask));

        InvalidOperationException error = kit.BuildError("""
            name: p
            steps:
              - id: A
                type: HookProbe
                hooks:
                  onFinalize: Wrong
            """);

        Assert.Contains("signature of 'Wrong'", error.Message);
        Assert.Contains("Func<PostProcessArgs, Task>", error.Message);
        Assert.Contains("Func<FinalizeArgs, Task>", error.Message);
    }

    [Fact]
    public void An_unknown_slot_lists_the_valid_ones_and_llm_slots_are_not_valid_on_other_steps()
    {
        HookKit kit = new();
        kit.Hooks.Add("X", HookKind.Hook, (Func<FinalizeArgs, Task>)(_ => Task.CompletedTask));

        InvalidOperationException error = kit.BuildError("""
            name: p
            steps:
              - id: A
                type: HookProbe
                hooks:
                  buildUserMessage: X
            """);

        Assert.Contains("unknown hook slot 'buildUserMessage'", error.Message);
        Assert.Contains("onFinalize", error.Message);
        Assert.DoesNotContain("onToolLimit", error.Message);
    }

    [Fact]
    public void Hook_keys_are_rejected_on_a_step_that_is_not_a_StepBase()
    {
        HookKit kit = new();
        kit.Registry.Add(typeof(NotAStepBase));

        InvalidOperationException error = kit.BuildError("""
            name: p
            steps:
              - id: A
                type: NotAStepBase
                validators: [x]
            """);

        Assert.Contains("does not derive from StepBase", error.Message);
    }

    [AITaskAgent.Core.Attributes.StepTag("NotAStepBase")]
    public sealed class NotAStepBase : IStep
    {
        public string Name { get; set; } = "NotAStepBase";

        public IResume? Resume => null;

        public Type InputType => typeof(IStepResult);

        public Type OutputType => typeof(IStepResult);

        public Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
            => Task.FromResult(input);
    }

    // ── validators ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_registered_validator_runs_on_the_step_and_a_rejection_fails_it()
    {
        HookKit kit = new();
        kit.Hooks.Add("Len.Limit", HookKind.StepValidator, Validator(false, "too long"));

        IStepResult result = await kit.RunAsync("""
            name: p
            steps:
              - id: A
                type: HookProbe
                maxRetries: 1
                validators: [Len.Limit]
            """);

        Assert.True(result.HasError);
        Assert.Equal("too long", result.Error!.Message);
    }

    [Fact]
    public async Task A_validator_service_is_found_by_its_Id()
    {
        HookKit kit = new(configure: s => s.AddSingleton<IStepValidator>(new FixedValidator("From.Di", false, "di says no")));

        IStepResult result = await kit.RunAsync("""
            name: p
            steps:
              - id: A
                type: HookProbe
                maxRetries: 1
                validators: [From.Di]
            """);

        Assert.Equal("di says no", result.Error!.Message);
    }

    [Fact]
    public void The_same_validator_name_in_the_registry_and_in_DI_is_a_build_error()
    {
        HookKit kit = new(configure: s => s.AddSingleton<IStepValidator>(new FixedValidator("Dup", true, null)));
        kit.Hooks.Add("Dup", HookKind.StepValidator, Validator(true));

        InvalidOperationException error = kit.BuildError("""
            name: p
            steps:
              - id: A
                type: HookProbe
                validators: [Dup]
            """);

        Assert.Contains("defined more than once", error.Message);
    }

    [Fact]
    public void Registering_the_same_name_twice_in_the_registry_throws()
    {
        HookRegistry registry = new();
        registry.Add("N", HookKind.Hook, (Func<FinalizeArgs, Task>)(_ => Task.CompletedTask));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => registry.Add("N", HookKind.StepValidator, Validator(true), source: "unit:pkg"));

        Assert.Contains("already registered", error.Message);
        Assert.Contains("unit:pkg", error.Message);
    }

    [Fact]
    public void An_unknown_validator_is_a_build_error_with_the_available_names()
    {
        HookKit kit = new();
        kit.Hooks.Add("Only", HookKind.StepValidator, Validator(true));

        InvalidOperationException error = kit.BuildError("""
            name: p
            steps:
              - id: A
                type: HookProbe
                validators: [Missing]
            """);

        Assert.Contains("unknown step validator 'Missing'", error.Message);
        Assert.Contains("Only", error.Message);
    }

    // ── result validators ────────────────────────────────────────────────────

    [Fact]
    public async Task A_registered_result_validator_decides_the_attempt()
    {
        HookKit kit = new();
        int runs = 0;
        kit.Hooks.Add("Check.Shape", HookKind.ResultValidator, (Func<ResultValidateArgs, Task<(bool IsValid, string? Error)>>)(a =>
        {
            runs++;
            return Task.FromResult(a.As<JObject>()!.ContainsKey("needed") ? (true, (string?)null) : (false, "missing 'needed'"));
        }));

        IStepResult ok = await kit.RunAsync("""
            name: p
            steps:
              - id: A
                type: HookProbe
                output: '{"needed":1}'
                resultValidators: [Check.Shape]
            """);
        IStepResult bad = await kit.RunAsync("""
            name: p
            steps:
              - id: A
                type: HookProbe
                maxRetries: 2
                resultValidators: [Check.Shape]
            """);

        Assert.False(ok.HasError);
        Assert.Equal("missing 'needed'", bad.Error!.Message);
        Assert.Equal(3, runs);
    }

    [Fact]
    public async Task The_schema_prefix_validates_the_value_against_a_registered_schema()
    {
        HookKit kit = new();
        kit.Schemas.Add("Finding", await JsonSchema.FromJsonAsync("""{"type":"object","required":["title"],"properties":{"title":{"type":"string"}}}"""));

        IStepResult bad = await kit.RunAsync("""
            name: p
            steps:
              - id: A
                type: HookProbe
                maxRetries: 1
                resultValidators: ["schema:Finding"]
            """);
        IStepResult ok = await kit.RunAsync("""
            name: p
            steps:
              - id: A
                type: HookProbe
                output: '{"title":"x"}'
                resultValidators: ["schema:Finding"]
            """);

        Assert.True(bad.HasError);
        Assert.Contains("does not match the JSON schema", bad.Error!.Message);
        Assert.False(ok.HasError, ok.Error?.Message);
    }

    [Fact]
    public void A_schema_validator_naming_an_unregistered_schema_is_a_build_error()
    {
        InvalidOperationException error = new HookKit().BuildError("""
            name: p
            steps:
              - id: A
                type: HookProbe
                resultValidators: ["schema:Nope"]
            """);

        Assert.Contains("not registered", error.Message);
    }

    [Fact]
    public void A_validator_cannot_be_used_in_the_wrong_list()
    {
        HookKit kit = new();
        kit.Hooks.Add("Semantic", HookKind.StepValidator, Validator(true));

        InvalidOperationException error = kit.BuildError("""
            name: p
            steps:
              - id: A
                type: HookProbe
                resultValidators: [Semantic]
            """);

        Assert.Contains("'Semantic' is a step validator, not a result validator", error.Message);
    }

    [Fact]
    public async Task Repeating_the_outputSchema_in_a_schema_validator_logs_a_warning()
    {
        HookKit kit = new();
        kit.Schemas.Add("Answer", await JsonSchema.FromJsonAsync("""{"type":"object"}"""));

        kit.Factory.CreateFromYaml("""
            name: p
            steps:
              - id: A
                type: LlmStep
                outputSchema: Answer
                resultValidators: ["schema:Answer"]
            """);

        Assert.Contains(kit.LoggerFactory.Warnings, w => w.Contains("repeats its outputSchema"));
    }

    // ── R3 ───────────────────────────────────────────────────────────────────

    [Fact]
    public void R3_warns_when_an_override_outside_the_framework_may_skip_base()
    {
        IReadOnlyList<string> warnings = HookSlots.FindOverrideWarnings(typeof(CustomPost), ["postProcess"]);

        string warning = Assert.Single(warnings);
        Assert.Contains("postProcess", warning);
        Assert.Contains("calls base", warning);
    }

    [Fact]
    public void R3_stays_quiet_for_framework_steps_and_unhooked_overrides()
    {
        Assert.Empty(HookSlots.FindOverrideWarnings(typeof(HookProbeStep), ["postProcess", "onFinalize"]));
        Assert.Empty(HookSlots.FindOverrideWarnings(typeof(ExecStep), ["enrichStarted"]));
        Assert.Empty(HookSlots.FindOverrideWarnings(typeof(CustomPost), ["onFinalize"]));
    }

    [Fact]
    public void The_binder_logs_the_R3_warning_at_build_time()
    {
        HookKit kit = new();
        kit.Registry.Add(typeof(CustomPost));
        kit.Hooks.Add("P", HookKind.Hook, (HookAround<PostProcessArgs, IStepResult>)((_, next) => next()));

        kit.Factory.CreateFromYaml("""
            name: p
            steps:
              - id: A
                type: CustomPost
                hooks:
                  postProcess: P
            """);

        Assert.Contains(kit.LoggerFactory.Warnings, w => w.Contains("runs only if") && w.Contains("calls base"));
    }

    [AITaskAgent.Core.Attributes.StepTag("CustomPost")]
    public sealed class CustomPost : HookProbeStep
    {
        protected override Task<IStepResult> PostProcessAsync(IStepResult input, IStepResult result, PipelineContext context, int attempt, CancellationToken cancellationToken)
            => Task.FromResult(result);
    }

    // ── catalog data ─────────────────────────────────────────────────────────

    [Fact]
    public void Slots_of_a_plain_step_and_of_an_LLM_step_differ()
    {
        string[] plain = [.. HookSlots.For(typeof(HookProbeStep)).Select(s => s.Name)];
        string[] llm = [.. HookSlots.For(typeof(AITaskAgent.YAML.Steps.YamlLlmStep)).Select(s => s.Name)];

        Assert.Contains("postProcess", plain);
        Assert.DoesNotContain("onToolLimit", plain);
        Assert.Contains("onToolLimit", llm);
        Assert.Contains("buildUserMessage", llm);
        Assert.Contains("postProcess", llm);
        Assert.Empty(HookSlots.For(typeof(NotAStepBase)));
    }

    [Fact]
    public void The_response_schema_builder_is_public_and_strict()
    {
        JsonSchema schema = ResponseSchemaBuilder.Build<Sample>()!;

        Assert.False(schema.AllowAdditionalProperties);
        Assert.Contains("title", schema.RequiredProperties);
        Assert.Equal("Short title", schema.Properties["title"].Description);
        Assert.Null(ResponseSchemaBuilder.Build<string>());
    }

    public sealed class Sample
    {
        [System.ComponentModel.Description("Short title")]
        [Required]
        public string Title { get; set; } = "";

        public int Severity { get; set; }
    }

    private sealed class FixedValidator(string id, bool ok, string? error) : IStepValidator
    {
        public string Id => id;

        public Task<(bool IsValid, string? Error)> ValidateAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
            => Task.FromResult((ok, error));
    }
}
