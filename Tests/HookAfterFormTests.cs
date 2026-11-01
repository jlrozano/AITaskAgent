using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Steps;
using AITaskAgent.YAML.Steps;
using Newtonsoft.Json.Linq;

namespace AITaskAgent.Tests;

/// <summary>
/// The "after" form of the around slots (architecture §14.6, decided 2026-10-23): <c>Func&lt;TArgs, Task&gt;</c> runs at the end of the
/// virtual method with the step's own value in the arguments, changes it there, and is bound from YAML beside the around form.
/// </summary>
public sealed class HookAfterFormTests
{
    private const string Probe = """
        name: p
        steps:
          - id: Probe
            type: HookProbe
            hooks:
              postProcess: {0}
        """;

    private const string Ask = """
        name: p
        steps:
          - id: Ask
            type: LlmStep
            userPrompt: ping
            systemPrompt: be brief
            hooks:
              {0}
        """;

    private static string ProbeWith(string hook) => Probe.Replace("{0}", hook);

    private static string AskWith(string hooks) => Ask.Replace("{0}", hooks);

    // ── Slots ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_around_slots_expose_their_after_form_and_the_signature_text_is_that_form()
    {
        Dictionary<string, HookSlot> slots = HookSlots.For(typeof(YamlLlmStep)).ToDictionary(s => s.Name);

        Assert.Equal(typeof(Func<PostProcessArgs, Task>), slots["postProcess"].AfterType);
        Assert.Equal("Func<PostProcessArgs, Task>", slots["postProcess"].Signature);
        Assert.Equal(typeof(Func<BuildMessageArgs, Task>), slots["buildUserMessage"].AfterType);
        Assert.Equal(typeof(Func<BuildMessageArgs, Task>), slots["buildSystemMessage"].AfterType);
        Assert.Equal(typeof(Func<ParseResponseArgs, Task>), slots["parseResponse"].AfterType);
        Assert.Equal([typeof(Func<ParseResponseArgs, Task>), typeof(HookAround<ParseResponseArgs, (IStepResult?, string?)>)], slots["parseResponse"].AcceptedTypes);

        // The other slots have one form, and their text is the property's type.
        Assert.Null(slots["onFinalize"].AfterType);
        Assert.Equal("Func<FinalizeArgs, Task>", slots["onFinalize"].Signature);
        Assert.All(slots.Values.Where(s => s.AfterType == null), s => Assert.False(s.DelegateType.IsGenericType && s.DelegateType.GetGenericTypeDefinition() == typeof(HookAround<,>)));
    }

    // ── postProcess ──────────────────────────────────────────────────────────

    [Fact]
    public async Task An_after_postProcess_sees_the_attempts_result_and_its_replacement_goes_on_to_validation()
    {
        HookKit kit = new();
        IStepResult? seen = null;
        kit.Hooks.Add("Post.Replace", HookKind.Hook, (Func<PostProcessArgs, Task>)(a =>
        {
            seen = a.Result;
            a.Result = new JsonStepResult(a.Step, JToken.Parse("{\"v\":2}"));
            return Task.CompletedTask;
        }));

        IStepResult result = await kit.RunAsync(ProbeWith("Post.Replace"));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(1, ((JToken)seen!.Value!)["v"]!.Value<int>());
        Assert.Equal(2, ((JToken)result.Value!)["v"]!.Value<int>());
    }

    [Fact]
    public async Task An_after_postProcess_that_leaves_Result_keeps_the_attempts_result()
    {
        HookKit kit = new();
        int calls = 0;
        kit.Hooks.Add("Post.Look", HookKind.Hook, (Func<PostProcessArgs, Task>)(_ =>
        {
            calls++;
            return Task.CompletedTask;
        }));

        IStepResult result = await kit.RunAsync(ProbeWith("Post.Look"));

        Assert.Equal(1, calls);
        Assert.Equal(1, ((JToken)result.Value!)["v"]!.Value<int>());
    }

    [Fact]
    public async Task An_after_postProcess_that_throws_ends_the_step_naming_the_hook_without_retry()
    {
        HookProbeStep step = new()
        {
            Hooks = new StepHooks { PostProcess = HookForms.After<PostProcessArgs, IStepResult>(_ => throw new InvalidOperationException("boom")) },
        };

        IStepResult result = await new AITaskAgent.Core.Execution.Pipeline().ExecuteAsync("Test", [step], new StepResult<string>(new EmptyStep("Seed"), "seed"), new PipelineContext());

        StepHookException hook = Assert.IsType<StepHookException>(result.Error!.OriginalException);
        Assert.Equal(nameof(StepHooks.PostProcess), hook.Hook);
        Assert.Contains("boom", result.Error.Message);
        Assert.Equal(1, step.Executions);
    }

    [Fact]
    public async Task An_after_postProcess_whose_result_does_not_fit_the_output_type_fails_the_step()
    {
        HookProbeStep step = new()
        {
            Hooks = new StepHooks
            {
                PostProcess = HookForms.After<PostProcessArgs, IStepResult>(a =>
                {
                    a.Result = new StepResult<string>(a.Step, "text");
                    return Task.CompletedTask;
                }),
            },
        };

        IStepResult result = await new AITaskAgent.Core.Execution.Pipeline().ExecuteAsync("Test", [step], new StepResult<string>(new EmptyStep("Seed"), "seed"), new PipelineContext());

        Assert.IsType<StepHookException>(result.Error!.OriginalException);
        Assert.Contains("not assignable", result.Error.Message);
    }

    [Fact]
    public async Task An_after_postProcess_runs_once_per_successful_attempt_before_validation()
    {
        HookKit kit = new();
        List<int> attempts = [];
        kit.Hooks.Add("Post.Count", HookKind.Hook, (Func<PostProcessArgs, Task>)(a =>
        {
            attempts.Add(a.Attempt);
            a.Result = new JsonStepResult(a.Step, new JObject { ["v"] = a.Attempt });
            return Task.CompletedTask;
        }));
        kit.Hooks.Add("Only.Second", HookKind.StepValidator, (Func<ValidateArgs, Task<(bool IsValid, string? Error)>>)(a =>
            Task.FromResult((((JToken)a.Result.Value!)["v"]!.Value<int>() == 2, (string?)"first attempt rejected"))));

        IStepResult result = await kit.RunAsync("""
            name: p
            steps:
              - id: Probe
                type: HookProbe
                validators: Only.Second
                hooks:
                  postProcess: Post.Count
            """);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal([1, 2], attempts);
    }

    // ── Binding ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_binder_takes_the_around_form_too_and_keeps_its_semantics()
    {
        HookKit kit = new();
        kit.Hooks.Add("Post.Around", HookKind.Hook, (HookAround<PostProcessArgs, IStepResult>)(async (a, next) =>
        {
            IStepResult own = await next();
            return new JsonStepResult(a.Step, new JObject { ["wrapped"] = (JToken)own.Value! });
        }));

        IStepResult result = await kit.RunAsync(ProbeWith("Post.Around"));

        Assert.Equal(1, ((JToken)result.Value!)["wrapped"]!["v"]!.Value<int>());
    }

    [Fact]
    public void A_hook_of_neither_form_is_a_build_error_that_names_both_forms()
    {
        HookKit kit = new();
        kit.Hooks.Add("Post.Wrong", HookKind.Hook, (Func<PostProcessArgs, Task<IStepResult>>)(a => Task.FromResult(a.Result)));

        InvalidOperationException error = kit.BuildError(ProbeWith("Post.Wrong"));

        Assert.Contains("Func<PostProcessArgs, Task> or HookAround<PostProcessArgs, IStepResult>", error.Message);
        Assert.Contains("Post.Wrong", error.Message);
    }

    [Fact]
    public void R3_still_warns_for_an_after_form_hook_on_an_override_outside_the_framework()
    {
        Assert.Single(HookSlots.FindOverrideWarnings(typeof(SkipsBase), ["postProcess"]));
    }

    private sealed class SkipsBase : HookProbeStep
    {
        protected override Task<IStepResult> PostProcessAsync(IStepResult input, IStepResult result, PipelineContext context, int attempt, CancellationToken cancellationToken)
            => Task.FromResult(result);
    }

    // ── LLM slots ────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_after_buildUserMessage_finds_the_rendered_message_and_changes_it()
    {
        HookKit kit = new();
        kit.Hooks.Add("User.After", HookKind.Hook, (Func<BuildMessageArgs, Task>)(a =>
        {
            a.Message = $">> {a.Message}";
            return Task.CompletedTask;
        }));
        kit.Hooks.Add("Sys.Keep", HookKind.Hook, (Func<BuildMessageArgs, Task>)(_ => Task.CompletedTask));

        IStepResult result = await kit.RunAsync(AskWith("""
            buildUserMessage: User.After
                  buildSystemMessage: Sys.Keep
            """));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(("be brief", ">> ping"), ((ScriptedLlmService)kit.Llm).Requests.Single());
    }

    [Fact]
    public async Task An_after_parseResponse_can_reject_the_built_in_result_and_the_step_retries()
    {
        HookKit kit = new();
        int calls = 0;
        kit.Hooks.Add("Parse.After", HookKind.Hook, (Func<ParseResponseArgs, Task>)(a =>
        {
            Assert.NotNull(a.Result);
            if (++calls == 1)
            {
                a.Result = null;
                a.Error = "first answer rejected";
            }

            return Task.CompletedTask;
        }));

        IStepResult result = await kit.RunAsync(AskWith("parseResponse: Parse.After"));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(2, calls);
        Assert.Equal(2, ((ScriptedLlmService)kit.Llm).Calls);
    }

    [Fact]
    public async Task An_after_buildSystemMessage_that_throws_fails_the_step_with_a_hook_error()
    {
        HookKit kit = new();
        kit.Hooks.Add("Sys.Boom", HookKind.Hook, (Func<BuildMessageArgs, Task>)(_ => throw new InvalidOperationException("no system")));

        IStepResult result = await kit.RunAsync(AskWith("buildSystemMessage: Sys.Boom"));

        Assert.True(result.HasError);
        StepHookException hook = Assert.IsType<StepHookException>(result.Error!.OriginalException);
        Assert.Equal(nameof(LlmStepHooks.BuildSystemMessage), hook.Hook);
    }

    // ── Data view (IHookData) ────────────────────────────────────────────────

    [Fact]
    public void The_data_view_reads_the_members_and_writes_back_the_writable_ones()
    {
        HookProbeStep step = new();
        PostProcessArgs args = new(step, new StepResult<string>(step, "in"), new JsonStepResult(step, JToken.Parse("{\"a\":1}")), new PipelineContext(), 2, default);

        JObject data = HookData.Read(args);

        Assert.Equal("HookProbe", data["step"]!["name"]!.Value<string>());
        Assert.Equal(2, data["attempt"]!.Value<int>());
        Assert.Equal("in", data["input"]!.Value<string>());
        Assert.Equal(1, data["result"]!["a"]!.Value<int>());
        Assert.False(data["isSuspended"]!.Value<bool>());

        IStepResult before = args.Result;
        HookData.Write(args, "result", JToken.Parse("{\"a\":5}"));
        Assert.Same(before, args.Result);
        Assert.Equal(5, ((JToken)args.Result.Value!)["a"]!.Value<int>());

        InvalidOperationException readOnly = Assert.Throws<InvalidOperationException>(() => HookData.Write(args, "attempt", 3));
        Assert.Contains("args.attempt is read-only", readOnly.Message);
    }

    [Fact]
    public void WithValue_converts_to_the_type_of_the_results_value()
    {
        HookProbeStep step = new();
        StepResult<string> text = new(step, "a");
        HookData.WithValue(text, JToken.Parse("{\"x\":1}"));
        Assert.Equal("{\"x\":1}", text.Value);
        HookData.WithValue(text, "plain");
        Assert.Equal("plain", text.Value);

        StepResult<int> number = new(step, 1);
        HookData.WithValue(number, 7);
        Assert.Equal(7, number.Value);
        Assert.Throws<InvalidOperationException>(() => HookData.WithValue(number, "not a number"));
    }

    [Fact]
    public void Every_args_type_with_an_after_form_or_a_js_shape_has_a_data_view_whose_writable_members_are_the_outcome()
    {
        Assert.Equal(["step", "attempt", "input", "result", "isSuspended"], HookData.MembersOf(typeof(PostProcessArgs))!.Select(m => m.Name));
        Assert.Equal(["result"], HookData.MembersOf(typeof(PostProcessArgs))!.Where(m => m.Writable).Select(m => m.Name));
        Assert.Equal(["message"], HookData.MembersOf(typeof(BuildMessageArgs))!.Where(m => m.Writable).Select(m => m.Name));
        Assert.Equal(["result", "error"], HookData.MembersOf(typeof(ParseResponseArgs))!.Where(m => m.Writable).Select(m => m.Name));
        Assert.Equal(["finalAnswerPrompt"], HookData.MembersOf(typeof(ToolLimitArgs))!.Where(m => m.Writable).Select(m => m.Name));
        Assert.DoesNotContain(HookData.MembersOf(typeof(FinalizeArgs))!, m => m.Writable);
        Assert.Equal(["result", "step"], HookData.MembersOf(typeof(ValidateArgs))!.Select(m => m.Name));
        Assert.Equal(["result"], HookData.MembersOf(typeof(ResultValidateArgs))!.Select(m => m.Name));

        // Since 2026-10-24 the synchronous slots have a data view too; only the conversation object stays out (onConversationCreated is C#-only).
        Assert.Equal(["displayName", "tags"], HookData.MembersOf(typeof(EnrichActivityArgs))!.Where(m => m.Writable).Select(m => m.Name));
        Assert.Equal(["additionalData", "suppressFromUser"], HookData.MembersOf(typeof(EnrichStartedArgs))!.Where(m => m.Writable).Select(m => m.Name));
        Assert.Equal(["additionalData", "suppressFromUser"], HookData.MembersOf(typeof(EnrichCompletedArgs))!.Where(m => m.Writable).Select(m => m.Name));
        Assert.Equal(["options", "model", "systemPrompt", "temperature", "topP", "maxTokens"], HookData.MembersOf(typeof(ConfigureRequestArgs))!.Where(m => m.Writable).Select(m => m.Name));
        Assert.Equal(["useHistory"], HookData.MembersOf(typeof(SelectConversationArgs))!.Where(m => m.Writable).Select(m => m.Name));
        Assert.Null(HookData.MembersOf(typeof(ConversationCreatedArgs)));
    }

    [Fact]
    public void The_tool_limit_decision_of_the_data_view_is_Fail_until_a_prompt_is_written()
    {
        ToolLimitArgs args = new(null!, new PipelineContext(), ToolLimitReason.LoopCut, "cut", 5);
        IHookOutcome<ToolLimitDecision> outcome = args;

        Assert.Same(ToolLimitDecision.Fail, outcome.Outcome);
        HookData.Write(args, "finalAnswerPrompt", "answer now");
        Assert.Equal("answer now", outcome.Outcome.FinalAnswerPrompt);
        HookData.Write(args, "finalAnswerPrompt", "  ");
        Assert.Same(ToolLimitDecision.Fail, outcome.Outcome);
    }

    [Fact]
    public void The_message_of_the_data_view_is_text()
    {
        BuildMessageArgs args = new(null!, new StepResult<string>(new EmptyStep("x"), "in"), new PipelineContext()) { Message = "own" };

        Assert.Equal("own", HookData.Read(new BuildMessageArgs(new YamlLlmStep(), args.Input, args.Context) { Message = "own" })["message"]!.Value<string>());
        HookData.Write(args, "message", JValue.CreateNull());
        Assert.Null(args.Message);
        HookData.Write(args, "message", "new");
        Assert.Equal("new", args.Message);
    }

    [Fact]
    public void A_parseResponse_result_written_as_json_is_built_like_the_built_in_one()
    {
        YamlLlmStep step = new();
        ParseResponseArgs args = new(step, LlmResult.Success([new LlmResponse { Content = "raw" }], tokensUsed: 3), new PipelineContext());

        HookData.Write(args, "result", "hello");

        Assert.Equal("hello", args.Result!.Value?.ToString());
        Assert.Equal(3, Assert.IsAssignableFrom<AITaskAgent.LLM.Results.LlmStepResult>(args.Result).TokensUsed);
        HookData.Write(args, "result", JValue.CreateNull());
        Assert.Null(args.Result);
    }
}
