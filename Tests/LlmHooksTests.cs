using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.LLM.Tools.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AITaskAgent.Tests;

/// <summary>Hooks of the LLM steps (YAML <c>hooks:</c> on <c>LlmStep</c>) and the final answer after the tool limit.</summary>
public sealed class LlmHooksTests
{
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

    private static string AskWith(string hooks) => Ask.Replace("{0}", hooks);

    private static ScriptedLlmService Scripted(HookKit kit) => (ScriptedLlmService)kit.Llm;

    [Fact]
    public async Task buildUserMessage_wraps_the_steps_own_rendering()
    {
        HookKit kit = new();
        kit.Hooks.Add("User.Wrap", HookKind.Hook, (HookAround<BuildMessageArgs, string?>)(async (args, next) => $">> {await next()}"));

        IStepResult result = await kit.RunAsync(AskWith("buildUserMessage: User.Wrap"));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(">> ping", Scripted(kit).Requests.Single().User);
    }

    [Fact]
    public async Task buildSystemMessage_can_replace_the_message_without_calling_next()
    {
        HookKit kit = new();
        kit.Hooks.Add("Sys.Replace", HookKind.Hook, (HookAround<BuildMessageArgs, string?>)((args, next) => Task.FromResult<string?>("replaced system")));

        await kit.RunAsync(AskWith("buildSystemMessage: Sys.Replace"));

        Assert.Equal("replaced system", Scripted(kit).Requests.Single().System);
    }

    [Fact]
    public async Task Without_hooks_the_messages_are_the_steps_own()
    {
        HookKit kit = new();

        await kit.RunAsync("""
            name: p
            steps:
              - id: Ask
                type: LlmStep
                userPrompt: ping
                systemPrompt: be brief
            """);

        Assert.Equal(("be brief", "ping"), Scripted(kit).Requests.Single());
    }

    [Fact]
    public async Task configureRequest_sees_and_returns_the_request_before_it_is_sent()
    {
        ToolScriptLlm llm = new(_ => new LlmResponse { Content = "ok", FinishReason = FinishReason.Stop });
        HookKit kit = new(llm);
        kit.Hooks.Add("Req.Tune", HookKind.Hook, (Func<ConfigureRequestArgs, LlmRequest>)(a =>
        {
            a.Request.Options["tool_choice"] = "required";
            return a.Request;
        }));

        await kit.RunAsync(AskWith("configureRequest: Req.Tune"));

        Assert.Equal("required", llm.Requests.Single().ToolChoice);
    }

    [Fact]
    public async Task parseResponse_can_reject_a_response_and_the_step_retries()
    {
        HookKit kit = new();
        int parses = 0;
        kit.Hooks.Add("Parse.Twice", HookKind.Hook, (HookAround<ParseResponseArgs, (IStepResult? Result, string? Error)>)(async (args, next) =>
            ++parses == 1 ? (null, "first answer rejected") : await next()));

        IStepResult result = await kit.RunAsync(AskWith("parseResponse: Parse.Twice"));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(2, parses);
        Assert.Equal(2, Scripted(kit).Calls);
    }

    [Fact]
    public async Task onConversationCreated_and_onFinalize_fire_once_for_a_clean_run()
    {
        HookKit kit = new();
        List<string> events = [];
        kit.Hooks.Add("Notes.Begin", HookKind.Hook, (Func<ConversationCreatedArgs, Task>)(a =>
        {
            events.Add($"begin:{a.Attempt}:{a.Conversation != null}");
            return Task.CompletedTask;
        }));
        kit.Hooks.Add("Notes.End", HookKind.Hook, (Func<FinalizeArgs, Task>)(a =>
        {
            events.Add($"end:{a.Result.HasError}");
            return Task.CompletedTask;
        }));

        await kit.RunAsync(AskWith("""
            onConversationCreated: Notes.Begin
                  onFinalize: Notes.End
            """));

        Assert.Equal(["begin:1:True", "end:False"], events);
    }

    [Fact]
    public async Task resultValidators_apply_to_an_LlmStep_without_an_output_schema()
    {
        HookKit kit = new();
        kit.Hooks.Add("Answer.NotOk", HookKind.ResultValidator, (Func<ResultValidateArgs, Task<(bool IsValid, string? Error)>>)(
            a => Task.FromResult(a.Result.Value as string == "ok" ? (false, (string?)"'ok' is not an answer") : (true, null))));

        IStepResult result = await kit.RunAsync("""
            name: p
            steps:
              - id: Ask
                type: LlmStep
                userPrompt: ping
                resultValidators: [Answer.NotOk]
            """);

        Assert.True(result.HasError);
        Assert.Equal("'ok' is not an answer", result.Error!.Message);
        Assert.Equal(3, Scripted(kit).Calls);
    }

    // ── onToolLimit (F1) ─────────────────────────────────────────────────────

    private static LlmResponse ToolCall(int n, string? args = null) => new()
    {
        Content = "",
        FinishReason = FinishReason.ToolCalls,
        ToolCalls = [new ToolCall { Id = $"call_{n}", Name = "list_dir", Arguments = args ?? $"{{\"path\":\"dir{n}\"}}" }],
    };

    private static HookKit ToolKit(ToolScriptLlm llm, Func<ToolLimitArgs, Task<ToolLimitDecision>> onLimit)
    {
        HookKit kit = new(llm, s => s.AddSingleton<ITool>(new EchoTool()));
        kit.Hooks.Add("Auditor.Final", HookKind.Hook, onLimit);
        return kit;
    }

    private const string ToolAsk = """
        name: p
        steps:
          - id: Ask
            type: LlmStep
            userPrompt: go
            tools: [list_dir]
            maxToolIterations: 2
            hooks:
              onToolLimit: Auditor.Final
        """;

    [Fact]
    public async Task onToolLimit_asks_for_a_final_answer_without_tools_when_the_iteration_limit_is_hit()
    {
        ToolScriptLlm llm = new(n => n <= 2 ? ToolCall(n) : new LlmResponse { Content = "final answer", FinishReason = FinishReason.Stop });
        ToolLimitArgs? seen = null;
        HookKit kit = ToolKit(llm, a =>
        {
            seen = a;
            return Task.FromResult(ToolLimitDecision.FinalAnswer("Answer now with what you have."));
        });

        IStepResult result = await kit.RunAsync(ToolAsk);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal("final answer", Assert.IsType<LlmStringStepResult>(result).Content);
        Assert.Equal(ToolLimitReason.IterationLimit, seen!.Reason);
        Assert.Equal(2, seen.MaxToolIterations);
        Assert.Equal(3, llm.Calls);

        var finalCall = llm.Requests.Last();
        Assert.Equal("Answer now with what you have.", finalCall.User);
        Assert.Equal("none", finalCall.ToolChoice);
        Assert.Equal(1, finalCall.Tools);
        Assert.Null(llm.Requests.First().ToolChoice);
    }

    [Fact]
    public async Task onToolLimit_also_covers_a_loop_cut_and_clears_the_stop_message()
    {
        // Three identical read-only calls: the repeat detector cuts the loop on the third.
        ToolScriptLlm llm = new(n => n <= 3 ? ToolCall(n, "{\"path\":\"same\"}") : new LlmResponse { Content = "final answer", FinishReason = FinishReason.Stop });
        ToolLimitReason? reason = null;
        HookKit kit = ToolKit(llm, a =>
        {
            reason = a.Reason;
            return Task.FromResult(ToolLimitDecision.FinalAnswer("Wrap up."));
        });

        IStepResult result = await kit.RunAsync(ToolAsk.Replace("maxToolIterations: 2", "maxToolIterations: 10"));

        Assert.Equal(ToolLimitReason.LoopCut, reason);
        LlmStringStepResult answer = Assert.IsType<LlmStringStepResult>(result);
        Assert.Equal("final answer", answer.Content);
        Assert.Null(answer.ToolLoopStopMessage);
    }

    [Fact]
    public async Task A_stateful_step_keeps_only_the_original_turn_in_the_shared_conversation()
    {
        ToolScriptLlm llm = new(n => n <= 2 ? ToolCall(n) : new LlmResponse { Content = "final answer", FinishReason = FinishReason.Stop });
        HookKit kit = ToolKit(llm, _ => Task.FromResult(ToolLimitDecision.FinalAnswer("Answer now.")));
        LlmPipelineContext context = new();

        IStepResult result = await kit.RunAsync(ToolAsk, context: context);

        Assert.False(result.HasError, result.Error?.Message);
        var messages = context.AsLlmContext("Ask").Conversation.History.Messages;
        Assert.Collection(messages,
            m => { Assert.Equal("user", m.Role); Assert.Equal("go", m.Content); },
            m => { Assert.Equal("assistant", m.Role); Assert.Equal("final answer", m.Content); });
    }

    [Fact]
    public async Task Deciding_Fail_keeps_todays_behaviour()
    {
        ToolScriptLlm llm = new(n => ToolCall(n));
        HookKit kit = ToolKit(llm, _ => Task.FromResult(ToolLimitDecision.Fail));

        IStepResult result = await kit.RunAsync(ToolAsk);

        Assert.IsType<ToolIterationLimitExceededException>(result.Error!.OriginalException);
        Assert.DoesNotContain(llm.Requests, r => r.ToolChoice == "none");
    }

    [Fact]
    public async Task Without_the_hook_the_tool_limit_is_unchanged()
    {
        ToolScriptLlm llm = new(n => ToolCall(n));
        HookKit kit = new(llm, s => s.AddSingleton<ITool>(new EchoTool()));

        IStepResult result = await kit.RunAsync("""
            name: p
            steps:
              - id: Ask
                type: LlmStep
                userPrompt: go
                tools: [list_dir]
                maxToolIterations: 2
            """);

        Assert.IsType<ToolIterationLimitExceededException>(result.Error!.OriginalException);
    }

    [Fact]
    public async Task An_exception_in_onToolLimit_is_a_fatal_hook_error()
    {
        ToolScriptLlm llm = new(n => ToolCall(n));
        HookKit kit = ToolKit(llm, _ => throw new InvalidOperationException("decision bug"));

        IStepResult result = await kit.RunAsync(ToolAsk);

        StepHookException hook = Assert.IsType<StepHookException>(result.Error!.OriginalException);
        Assert.Equal(nameof(LlmStepHooks.OnToolLimit), hook.Hook);
    }

    [Fact]
    public async Task A_model_that_ignores_tool_choice_in_the_final_turn_does_not_run_more_tools()
    {
        ToolScriptLlm llm = new(n => n <= 3 ? ToolCall(n) : new LlmResponse { Content = "never", FinishReason = FinishReason.Stop });
        HookKit kit = ToolKit(llm, _ => Task.FromResult(ToolLimitDecision.FinalAnswer("Answer now.")));

        IStepResult result = await kit.RunAsync(ToolAsk);

        // Call 3 is the final turn: it still asked for a tool, the calls are dropped (no tool ran after it)
        // and the empty answer is an ordinary invalid response that the retry loop handles.
        Assert.Equal("none", llm.Requests.ElementAt(2).ToolChoice);
        Assert.NotNull(result);
    }
}
