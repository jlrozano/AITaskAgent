using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.YAML.Execution;
using AITaskAgent.YAML.Schemas;
using AITaskAgent.YAML.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;

namespace AITaskAgent.Tests;

/// <summary>
/// The <c>selectConversation</c> slot (2026-10-24): dispatched at the call site after <c>GetConversationContext</c>, with the step's own choice in
/// <see cref="SelectConversationArgs.UseHistory"/>; true = the context's conversation, false = a fresh empty one. Applies to every
/// <see cref="BaseLlmStep"/> (YAML or not), runs again on a fallback round, and a hook exception is fatal.
/// </summary>
public sealed class SelectConversationHookTests
{
    /// <summary>LLM double that records the conversation object of every request and how many messages it had.</summary>
    private sealed class ConversationProbeLlm(Func<int, LlmResult>? respond = null) : ILlmService
    {
        private int _calls;

        public string Name => "probe";

        public ConcurrentQueue<(ConversationContext Conversation, string[] Messages)> Requests { get; } = new();

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _calls);
            Requests.Enqueue((request.Conversation, [.. request.Conversation.History.Messages.Select(m => $"{m.Role}:{m.Content}")]));
            return Task.FromResult(respond?.Invoke(call) ?? LlmResult.Success([new LlmResponse { Content = "ok", FinishReason = FinishReason.Stop }]));
        }

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public int EstimateTokenCount(string text) => text.Length / 4;

        public int GetMaxContextTokens(string? model = null) => 8192;
    }

    /// <summary>A plain <see cref="BaseLlmStep"/>: the base <c>GetConversationContext</c> (the context's conversation).</summary>
    private sealed class PlainLlmStep(LlmRole role) : BaseLlmStep("Plain", role, typeof(IStepResult), typeof(LlmStringStepResult), "ping");

    private static LlmPipelineContext Seeded()
    {
        LlmPipelineContext context = new();
        context.Conversation.AddUserMessage("earlier question");
        context.Conversation.AddAssistantMessage("earlier answer");
        return context;
    }

    private static (YamlPipelineFactory Factory, HookRegistry Hooks) Kit(ILlmService llm, ILlmService? fallback = null)
    {
        LlmEnvironment environment = new() { DefaultRole = "default" };
        environment.Roles["default"] = new LlmRole
        {
            Provider = new LlmProvider { Service = llm },
            Model = "primary",
            Fallbacks = fallback == null ? null : [new LlmFallback("secondary", new LlmProvider { Service = fallback })],
        };
        StepRegistry registry = new();
        registry.AddFromAssembly(typeof(StepRegistry).Assembly);
        registry.Add(typeof(YamlLlmStep));
        HookRegistry hooks = new();
        ServiceCollection services = new();
        services.AddSingleton<IOptions<LlmEnvironment>>(Options.Create(environment));
        services.AddSingleton<IHookRegistry>(hooks);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        ServiceProvider provider = services.BuildServiceProvider();
        return (new YamlPipelineFactory(provider, registry, new SchemaRegistry(), new Pipeline(loggerFactory: NullLoggerFactory.Instance)), hooks);
    }

    private static string Yaml(bool? useHistory, string? hook) =>
        "name: p\nsteps:\n  - id: Ask\n    type: LlmStep\n    userPrompt: ping\n" +
        (useHistory == null ? "" : $"    useConversationHistory: {(useHistory.Value ? "true" : "false")}\n") +
        (hook == null ? "" : $"    hooks:\n      selectConversation: {hook}\n");

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Without_the_hook_a_YamlLlmStep_keeps_its_own_choice(bool? useHistory, bool expectedHistory)
    {
        ConversationProbeLlm llm = new();
        LlmPipelineContext context = Seeded();

        IStepResult result = await Kit(llm).Factory.CreateFromYaml(Yaml(useHistory, null)).ExecuteAsync(new JObject(), context);

        Assert.False(result.HasError, result.Error?.Message);
        (ConversationContext conversation, string[] messages) = Assert.Single(llm.Requests);
        Assert.Equal(expectedHistory, ReferenceEquals(conversation, context.Conversation));
        Assert.Equal(expectedHistory ? ["user:earlier question", "assistant:earlier answer", "user:ping"] : ["user:ping"], messages);
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    public async Task The_hook_sees_the_YamlLlmSteps_own_choice_and_its_decision_is_applied(bool? useHistory, bool decision, bool expectedHistory)
    {
        ConversationProbeLlm llm = new();
        (YamlPipelineFactory factory, HookRegistry hooks) = Kit(llm);
        List<(bool Default, int Attempt, string[] History, string Step)> seen = [];
        hooks.Add("Conv.Pick", HookKind.Hook, (Func<SelectConversationArgs, Task>)(a =>
        {
            seen.Add((a.UseHistory, a.Attempt, [.. a.History.Select(m => m.Content!)], a.Step.Name));
            a.UseHistory = decision;
            return Task.CompletedTask;
        }));
        LlmPipelineContext context = Seeded();

        IStepResult result = await factory.CreateFromYaml(Yaml(useHistory, "Conv.Pick")).ExecuteAsync(new JObject(), context);

        Assert.False(result.HasError, result.Error?.Message);
        (bool seenDefault, int attempt, string[] history, string step) = Assert.Single(seen);
        Assert.Equal(useHistory ?? true, seenDefault);
        Assert.Equal(1, attempt);
        Assert.Equal(["earlier question", "earlier answer"], history);
        Assert.Equal("Ask", step);
        (ConversationContext conversation, string[] messages) = Assert.Single(llm.Requests);
        Assert.Equal(expectedHistory, ReferenceEquals(conversation, context.Conversation));
        Assert.Equal(expectedHistory ? 3 : 1, messages.Length);
        // A stateful round leaves its [user, assistant] pair in the shared conversation; a fresh one leaves it untouched.
        Assert.Equal(expectedHistory ? 4 : 2, context.Conversation.History.Messages.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_plain_BaseLlmStep_defaults_to_the_history_and_follows_the_hook(bool decision)
    {
        ConversationProbeLlm llm = new();
        PlainLlmStep step = new(new LlmRole { Provider = new LlmProvider { Service = llm }, Model = "m" });
        bool? seenDefault = null;
        step.Hooks = new LlmStepHooks
        {
            SelectConversation = a =>
            {
                seenDefault = a.UseHistory;
                a.UseHistory = decision;
                return Task.CompletedTask;
            },
        };
        LlmPipelineContext context = Seeded();

        IStepResult result = await ((IStep)step).ExecuteAsync(new LlmStringStepResult(new EmptyStep("Seed")) { Content = "x" }, context, 1, null, CancellationToken.None);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.True(seenDefault);
        (ConversationContext conversation, string[] messages) = Assert.Single(llm.Requests);
        Assert.Equal(decision, ReferenceEquals(conversation, context.Conversation));
        Assert.Equal(decision ? 3 : 1, messages.Length);
    }

    [Fact]
    public async Task A_StatelessTemplateLlmStep_offers_false_and_true_takes_the_history()
    {
        ConversationProbeLlm llm = new();
        StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult> step = new(
            "Stateless", new LlmRole { Provider = new LlmProvider { Service = llm }, Model = "m" }, templateProvider: null, promtTemplateString: "{{Input}}");
        bool? seenDefault = null;
        step.Hooks = new LlmStepHooks { SelectConversation = a => { seenDefault = a.UseHistory; a.UseHistory = true; return Task.CompletedTask; } };
        LlmPipelineContext context = Seeded();

        await ((IStep)step).ExecuteAsync(new LlmStringStepResult(new EmptyStep("Seed")) { Content = "x" }, context, 1, null, CancellationToken.None);

        Assert.False(seenDefault);
        Assert.Same(context.Conversation, Assert.Single(llm.Requests).Conversation);
    }

    [Fact]
    public async Task The_hook_runs_again_on_the_fallback_round_and_that_round_starts_clean()
    {
        ConversationProbeLlm primary = new(_ => LlmResult.Failure(new HttpRequestException("503"), true));
        ConversationProbeLlm secondary = new();
        (YamlPipelineFactory factory, HookRegistry hooks) = Kit(primary, secondary);
        int calls = 0;
        hooks.Add("Conv.Count", HookKind.Hook, (Func<SelectConversationArgs, Task>)(a =>
        {
            calls++;
            Assert.Equal(1, a.Attempt);
            a.UseHistory = true;
            return Task.CompletedTask;
        }));
        LlmPipelineContext context = Seeded();

        IStepResult result = await factory.CreateFromYaml(Yaml(null, "Conv.Count")).ExecuteAsync(new JObject(), context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(2, calls);
        Assert.NotEmpty(primary.Requests);
        // The fallback round sees the history as it was before the failed round (bookmark restored), plus its own user message.
        Assert.Equal(["user:earlier question", "assistant:earlier answer", "user:ping"], Assert.Single(secondary.Requests).Messages);
    }

    [Fact]
    public async Task An_exception_in_selectConversation_is_a_fatal_hook_error_and_no_model_is_called()
    {
        ConversationProbeLlm llm = new();
        (YamlPipelineFactory factory, HookRegistry hooks) = Kit(llm);
        hooks.Add("Conv.Bug", HookKind.Hook, (Func<SelectConversationArgs, Task>)(_ => throw new InvalidOperationException("selection bug")));
        LlmPipelineContext context = Seeded();

        IStepResult result = await factory.CreateFromYaml(Yaml(null, "Conv.Bug")).ExecuteAsync(new JObject(), context);

        StepHookException hook = Assert.IsType<StepHookException>(result.Error!.OriginalException);
        Assert.Equal(nameof(LlmStepHooks.SelectConversation), hook.Hook);
        Assert.Empty(llm.Requests);
        Assert.Equal(2, context.Conversation.History.Messages.Count);
    }

    [Fact]
    public void The_slot_is_a_call_site_slot_with_the_task_form_and_a_data_view()
    {
        HookSlot slot = HookSlots.For(typeof(YamlLlmStep)).Single(s => s.Name == "selectConversation");

        Assert.Equal(typeof(Func<SelectConversationArgs, Task>), slot.DelegateType);
        Assert.Null(slot.VirtualMethod);
        Assert.Equal(["step", "attempt", "input", "history", "useHistory"], HookData.MembersOf(typeof(SelectConversationArgs))!.Select(m => m.Name));
        Assert.Equal(["useHistory"], HookData.MembersOf(typeof(SelectConversationArgs))!.Where(m => m.Writable).Select(m => m.Name));
    }

    [Fact]
    public void The_data_view_carries_the_history_as_plain_json_and_useHistory_takes_only_booleans()
    {
        List<Message> history =
        [
            new() { Role = "user", Content = "hi" },
            new() { Role = "assistant", Content = "", ToolCalls = [new ToolCall { Id = "c1", Name = "list_dir", Arguments = "{}" }] },
            new() { Role = "tool", Content = "listing", ToolCallId = "c1", Name = "list_dir" },
        ];
        SelectConversationArgs args = new(null!, new LlmStringStepResult(new EmptyStep("Seed")) { Content = "x" }, new PipelineContext(), history, 1, CancellationToken.None);

        JArray json = (JArray)HookData.MembersOf(typeof(SelectConversationArgs))!.Single(m => m.Name == "history").Read(args)!;
        HookData.Write(args, "useHistory", false);

        Assert.Equal(
            """[{"role":"user","content":"hi"},{"role":"assistant","content":"","toolCalls":[{"id":"c1","name":"list_dir","arguments":"{}"}]},{"role":"tool","content":"listing","name":"list_dir","toolCallId":"c1"}]""",
            json.ToString(Newtonsoft.Json.Formatting.None));
        Assert.False(args.UseHistory);
        Assert.Throws<InvalidOperationException>(() => HookData.Write(args, "useHistory", "yes"));
        Assert.Throws<InvalidOperationException>(() => HookData.Write(args, "history", new JArray()));
    }
}
