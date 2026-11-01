using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Steps;
using AITaskAgent.Observability.Events;
using AITaskAgent.Script.Configuration;
using AITaskAgent.Script.Hosting;
using AITaskAgent.Script.Isolation;
using AITaskAgent.Script.Validators;
using AITaskAgent.YAML.Execution;
using AITaskAgent.YAML.Schemas;
using AITaskAgent.YAML.Steps;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;

namespace AITaskAgent.Script.Tests;

/// <summary>
/// The slots opened to JavaScript on 2026-10-24 (architecture §18.8): the synchronous ones (<c>enrichActivityBefore/After</c>,
/// <c>enrichStarted/Completed</c>, <c>configureRequest</c>), which wait for the worker with a bounded blocking wait, and the new
/// <c>selectConversation</c>. Facades read and written, patches applied to the real objects, the failure rule of each slot, limits, a killed
/// worker and a caller whose synchronization context never runs anything. No LLM.
/// </summary>
[Collection("ScriptWorker")]
public sealed class ScriptSyncHookTests
{
    private static (IHookFactory Factory, IServiceProvider Services) Alone(string source, JsValidatorOptions? options = null)
    {
        ServiceCollection collection = new();
        collection.AddScriptExtension().AddScriptLibrary(new ScriptLibrary("llm", source));
        if (options != null)
        {
            collection.ConfigureJsValidators(options);
        }

        ServiceProvider services = collection.BuildServiceProvider();
        return (services.GetServices<IHookFactory>().Single(f => f.Prefix == "js:"), services);
    }

    private static HookSlot Slot(string name) => HookSlots.For(typeof(YamlLlmStep)).Single(s => s.Name == name);

    private static T Hook<T>(IHookFactory factory, string name, string slot, StepBase? step = null) where T : Delegate
        => (T)factory.CreateHook(name, step ?? new YamlLlmStep(), Slot(slot));

    private sealed class ActivityScope : IDisposable
    {
        private readonly ActivitySource _source = new("js-hook-tests-" + Guid.NewGuid().ToString("N"));
        private readonly ActivityListener _listener;

        public ActivityScope()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = s => s == _source,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            };
            ActivitySource.AddActivityListener(_listener);
            Activity = _source.StartActivity("step.run")!;
        }

        public Activity Activity { get; }

        public void Dispose()
        {
            Activity.Dispose();
            _listener.Dispose();
            _source.Dispose();
        }
    }

    // ── enrichActivityBefore / After ─────────────────────────────────────────

    [Fact]
    public void An_enrichActivity_hook_writes_tags_and_display_name_to_the_real_activity_and_cannot_touch_the_ids()
    {
        (IHookFactory factory, IServiceProvider services) = Alone("""
            exports.tag = function (args) {
              if (!args.activity || args.activity.operationName !== 'step.run') throw new Error('no activity');
              args.tags['order.id'] = args.input.order;
              args.tags.kind = 'js';
              delete args.tags.old;
              args.displayName = 'step ' + args.step.name;
              args.vars.span = args.activity.spanId;
            };
            exports.ids = function (args) { args.activity.traceId = 'x'; };
            """);
        using ActivityScope scope = new();
        scope.Activity.SetTag("old", "gone");
        JsonOutStep step = new();
        PipelineContext context = new(null, services);
        Action<EnrichActivityArgs> tag = Hook<Action<EnrichActivityArgs>>(factory, "js:llm.tag", "enrichActivityBefore", step);

        tag(new EnrichActivityArgs(step, scope.Activity, new JsonStepResult(step, new JObject { ["order"] = 7 }), null, context));

        Dictionary<string, object?> tags = scope.Activity.TagObjects.ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal(7L, Convert.ToInt64(tags["order.id"]));
        Assert.Equal("js", tags["kind"]);
        Assert.False(tags.ContainsKey("old"));
        Assert.Equal("step JsonOut", scope.Activity.DisplayName);
        Assert.Equal(scope.Activity.SpanId.ToHexString(), ((JToken)context.Metadata["span"]!).Value<string>());

        Action<EnrichActivityArgs> ids = Hook<Action<EnrichActivityArgs>>(factory, "js:llm.ids", "enrichActivityAfter", step);
        StepHookException error = Assert.Throws<StepHookException>(() => ids(new EnrichActivityArgs(step, scope.Activity, null, new JsonStepResult(step, new JValue(1)), context)));
        Assert.Contains("args.activity.traceId is read-only", error.InnerException!.Message, StringComparison.Ordinal);
    }

    // ── enrichStarted / enrichCompleted ──────────────────────────────────────

    [Fact]
    public void Event_hooks_rebuild_the_event_with_what_they_wrote()
    {
        (IHookFactory factory, IServiceProvider services) = Alone("""
            exports.started = function (args) {
              args.additionalData = args.additionalData || {};
              args.additionalData.attempt = args.event.attemptNumber;
              args.additionalData.order = args.input.order;
              args.suppressFromUser = true;
            };
            exports.completed = function (args) {
              args.additionalData.result = args.result;
              args.additionalData.ok = args.event.success && !args.hasError;
            };
            exports.name = function (args) { args.event.stepName = 'x'; };
            """);
        JsonOutStep step = new();
        PipelineContext context = new(null, services);
        StepStartedEvent started = new() { StepName = "JsonOut", AttemptNumber = 3 };

        StepStartedEvent rebuilt = Hook<Func<EnrichStartedArgs, StepStartedEvent>>(factory, "js:llm.started", "enrichStarted", step)
            (new EnrichStartedArgs(step, started, new JsonStepResult(step, new JObject { ["order"] = 7 }), context));
        StepCompletedEvent completed = new() { StepName = "JsonOut", Success = true, AdditionalData = { ["tokens"] = 5 } };
        IStepCompletedEvent done = Hook<Func<EnrichCompletedArgs, IStepCompletedEvent>>(factory, "js:llm.completed", "enrichCompleted", step)
            (new EnrichCompletedArgs(step, completed, new JsonStepResult(step, new JArray(1, 2)), context));

        Assert.Equal(3L, Convert.ToInt64(rebuilt.AdditionalData!["attempt"]));
        Assert.Equal(7L, Convert.ToInt64(rebuilt.AdditionalData["order"]));
        Assert.True(rebuilt.SuppressFromUser);
        Assert.Equal(started.Timestamp, rebuilt.Timestamp);
        StepCompletedEvent doneEvent = Assert.IsType<StepCompletedEvent>(done);
        Assert.Equal(5, doneEvent.AdditionalData["tokens"]);
        Assert.Equal("[1,2]", ((JToken)doneEvent.AdditionalData["result"]!).ToString(Formatting.None));
        Assert.Equal(true, doneEvent.AdditionalData["ok"]);
        Assert.Throws<StepHookException>(() => Hook<Func<EnrichStartedArgs, StepStartedEvent>>(factory, "js:llm.name", "enrichStarted", step)
            (new EnrichStartedArgs(step, started, new JsonStepResult(step, new JObject()), context)));
    }

    // ── configureRequest ─────────────────────────────────────────────────────

    [Fact]
    public void A_configureRequest_hook_changes_the_request_and_its_typed_members_win_over_the_options()
    {
        (IHookFactory factory, IServiceProvider services) = Alone("""
            exports.tune = function (args) {
              if (args.messages[0].content !== 'hello' || args.tools[0] !== 'list_dir') throw new Error('request data');
              args.temperature = 0.1;
              args.options.Temperature = 0.9;
              args.options.tool_choice = 'required';
              args.model = args.model + '-mini';
              args.systemPrompt = args.systemPrompt + ' Be brief.';
            };
            exports.bad = function (args) { args.messages.push({ role: 'user', content: 'x' }); };
            """);
        ConversationContext conversation = new();
        conversation.AddUserMessage("hello");
        LlmRequest original = new()
        {
            Conversation = conversation,
            Model = "gpt",
            SystemPrompt = "You help.",
            Temperature = 0.5f,
            Tools = [new ToolDefinition { Name = "list_dir", Description = "d", ParametersJsonSchema = "{}" }],
        };
        YamlLlmStep step = new();
        PipelineContext context = new(null, services);

        LlmRequest sent = Hook<Func<ConfigureRequestArgs, LlmRequest>>(factory, "js:llm.tune", "configureRequest", step)(new ConfigureRequestArgs(step, original, context));

        Assert.Equal("gpt-mini", sent.Model);
        Assert.Equal("You help. Be brief.", sent.SystemPrompt);
        Assert.Equal(0.1f, sent.Temperature);
        Assert.Equal("required", sent.GetOption<string>("tool_choice"));
        Assert.Same(conversation, sent.Conversation);
        Assert.Equal("gpt", original.Model);
        Assert.Equal(0.5f, original.Temperature);
        StepHookException error = Assert.Throws<StepHookException>(() =>
            Hook<Func<ConfigureRequestArgs, LlmRequest>>(factory, "js:llm.bad", "configureRequest", step)(new ConfigureRequestArgs(step, original, context)));
        Assert.Contains("args.messages", error.InnerException!.Message, StringComparison.Ordinal);
    }

    // ── selectConversation ───────────────────────────────────────────────────

    [Fact]
    public async Task A_selectConversation_hook_reads_the_history_and_the_input_and_decides_through_useHistory()
    {
        (IHookFactory factory, IServiceProvider services) = Alone("""
            exports.pick = function (args) {
              if (args.useHistory !== true || args.attempt !== 1 || args.step.name.indexOf('YamlLlmStep') !== 0) throw new Error('defaults');
              args.useHistory = !!args.input.followUp && args.history.length > 0;
              args.vars.seen = args.history.map(function (m) { return m.role; });
            };
            exports.history = function (args) { args.history.push({ role: 'user', content: 'x' }); };
            exports.notBool = function (args) { args.useHistory = 'yes'; };
            """);
        YamlLlmStep step = new();
        PipelineContext context = new(null, services);
        List<Message> history = [new() { Role = "user", Content = "q" }, new() { Role = "assistant", Content = "a" }];
        Func<SelectConversationArgs, Task> pick = Hook<Func<SelectConversationArgs, Task>>(factory, "js:llm.pick", "selectConversation", step);

        SelectConversationArgs followUp = new(step, new JsonStepResult(step, new JObject { ["followUp"] = true }), context, history, 1, CancellationToken.None) { UseHistory = true };
        SelectConversationArgs fresh = new(step, new JsonStepResult(step, new JObject()), context, history, 1, CancellationToken.None) { UseHistory = true };
        await pick(followUp);
        await pick(fresh);

        Assert.True(followUp.UseHistory);
        Assert.False(fresh.UseHistory);
        Assert.Equal("""["user","assistant"]""", ((JToken)context.Metadata["seen"]!).ToString(Formatting.None));
        StepHookException readOnly = await Assert.ThrowsAsync<StepHookException>(() =>
            Hook<Func<SelectConversationArgs, Task>>(factory, "js:llm.history", "selectConversation", step)(followUp));
        Assert.Contains("args.history", readOnly.InnerException!.Message, StringComparison.Ordinal);
        StepHookException notBool = await Assert.ThrowsAsync<StepHookException>(() =>
            Hook<Func<SelectConversationArgs, Task>>(factory, "js:llm.notBool", "selectConversation", step)(followUp));
        Assert.Contains("args.useHistory takes true or false", notBool.InnerException!.Message, StringComparison.Ordinal);
    }

    // ── Limits, a killed worker, the bounded wait, no deadlock ───────────────

    [Fact]
    public void A_synchronous_hook_that_runs_away_is_killed_at_its_time_limit_and_fails_with_a_hook_exception_instead_of_hanging()
    {
        (IHookFactory factory, IServiceProvider services) = Alone("exports.loop = function () { for (;;) { } };", new JsValidatorOptions { TimeoutMs = 500 });
        YamlLlmStep step = new();
        Func<ConfigureRequestArgs, LlmRequest> hook = Hook<Func<ConfigureRequestArgs, LlmRequest>>(factory, "js:llm.loop", "configureRequest", step);
        LlmRequest request = new() { Conversation = new ConversationContext(), Model = "m" };
        Stopwatch watch = Stopwatch.StartNew();

        StepHookException error = Assert.Throws<StepHookException>(() => hook(new ConfigureRequestArgs(step, request, new PipelineContext(null, services))));

        Assert.Equal("js:llm.loop", error.Hook);
        Assert.Contains("the script exceeded the time limit of 500 ms", error.InnerException!.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }

    [Fact]
    public void A_synchronous_hook_whose_worker_dies_fails_with_a_hook_exception()
    {
        (IHookFactory factory, IServiceProvider services) = Alone(
            "exports.boom = function () { var keep = []; for (;;) { keep.push(new Array(1000000).fill(1.5)); } };",
            new JsValidatorOptions { TimeoutMs = 20_000, MaxHeapMb = 8 });
        JsonOutStep step = new();
        Action<EnrichActivityArgs> hook = Hook<Action<EnrichActivityArgs>>(factory, "js:llm.boom", "enrichActivityBefore", step);
        Stopwatch watch = Stopwatch.StartNew();

        StepHookException error = Assert.Throws<StepHookException>(() => hook(new EnrichActivityArgs(step, null, new JsonStepResult(step, new JValue(1)), null, new PipelineContext(null, services))));

        Assert.Equal("js:llm.boom", error.Hook);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
    }

    [Fact]
    public void The_bounded_wait_of_a_synchronous_slot_cancels_the_call_and_fails_when_it_expires()
    {
        (IHookFactory factory, IServiceProvider services) = Alone("exports.loop = function () { for (;;) { } };", new JsValidatorOptions { TimeoutMs = 30_000 });
        JsonOutStep step = new();
        Action<EnrichActivityArgs> hook = Hook<Action<EnrichActivityArgs>>(factory, "js:llm.loop", "enrichActivityBefore", step);
        Stopwatch watch = Stopwatch.StartNew();
        JsHook.SyncWaitOverrideMs = 1_500;
        try
        {
            StepHookException error = Assert.Throws<StepHookException>(() => hook(new EnrichActivityArgs(step, null, new JsonStepResult(step, new JValue(1)), null, new PipelineContext(null, services))));

            Assert.Contains("did not answer within 1500 ms", error.InnerException!.Message, StringComparison.Ordinal);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        }
        finally
        {
            JsHook.SyncWaitOverrideMs = null;
        }
    }

    /// <summary>A synchronization context that never runs what is posted to it: anything that captured it would never finish.</summary>
    private sealed class NeverRunsContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new InvalidOperationException("Send on a blocked context.");
    }

    [Fact]
    public void A_synchronous_hook_called_from_a_thread_whose_context_never_runs_does_not_deadlock()
    {
        (IHookFactory factory, IServiceProvider services) = Alone("exports.tag = function (args) { args.tags.ok = true; };");
        JsonOutStep step = new();
        Action<EnrichActivityArgs> hook = Hook<Action<EnrichActivityArgs>>(factory, "js:llm.tag", "enrichActivityBefore", step);
        using ActivityScope scope = new();
        Exception? failure = null;

        Thread thread = new(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NeverRunsContext());
            try
            {
                hook(new EnrichActivityArgs(step, scope.Activity, new JsonStepResult(step, new JValue(1)), null, new PipelineContext(null, services)));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the hook hung");
        Assert.Null(failure);
        Assert.Equal(true, scope.Activity.GetTagItem("ok"));
    }

    // ── In a run: the enrichment failure rule ────────────────────────────────

    [Fact]
    public async Task In_a_run_a_failing_enrichment_hook_is_logged_and_ignored_and_the_next_hook_runs_on_a_respawned_worker()
    {
        const string source = """
            exports.loop = function () { for (;;) { } };
            exports.started = function (args) { args.vars.started = args.event.stepName; args.additionalData = { js: true }; };
            exports.mark = function (args) { args.vars.finished = args.step.name; };
            """;
        ServiceCollection collection = new();
        collection.AddScriptExtension().AddScriptLibrary(new ScriptLibrary("w", source)).ConfigureJsValidators(new JsValidatorOptions { TimeoutMs = 500 });
        ServiceProvider services = collection.BuildServiceProvider();
        services.GetRequiredService<IStepRegistry>().Add(typeof(JsonOutStep), "JsonOut");
        YamlPipelineFactory factory = new(services, services.GetRequiredService<IStepRegistry>(), new SchemaRegistry(), new Pipeline());
        YamlPipeline pipeline = factory.CreateFromYaml("""
            name: enrich
            steps:
              - id: out
                type: JsonOut
                json: '1'
                hooks:
                  enrichStarted: js:w.started
                  enrichActivityBefore: js:w.loop
                  onFinalize: js:w.mark
            """);
        PipelineContext context = new(null, services) { CorrelationId = $"jssync-run-{Guid.NewGuid():N}" };

        IStepResult result = await pipeline.ExecuteAsync(new JObject(), context).WaitAsync(TimeSpan.FromSeconds(90));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal("out", ((JToken)context.Metadata["started"]!).Value<string>());
        Assert.Equal("out", ((JToken)context.Metadata["finished"]!).Value<string>());
        Assert.Equal(2, ScriptWorkerClient.StartedWorkers(context.CorrelationId));
        Assert.Equal(0, ScriptWorkerClient.ActiveWorkers(context.CorrelationId));
    }

    /// <summary>A step that returns the JSON of its <see cref="Json"/> property.</summary>
    [AITaskAgent.Core.Attributes.StepTag("JsonOut")]
    public sealed class JsonOutStep : StepBase
    {
        public JsonOutStep() : base("JsonOut", typeof(IStepResult), typeof(JsonStepResult))
        {
            MaxRetries = 1;
        }

        public string Json { get; set; } = "null";

        protected override Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
            => Task.FromResult<IStepResult>(new JsonStepResult(this, JToken.Parse(Json)));
    }
}
