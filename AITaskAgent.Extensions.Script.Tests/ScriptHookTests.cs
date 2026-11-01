using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Steps;
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

namespace AITaskAgent.Script.Tests;

/// <summary>
/// JavaScript hooks (architecture §18.8): <c>hooks: { slot: js:&lt;lib&gt;.&lt;fn&gt; }</c>, run in the run's script worker with the data view of
/// the slot's arguments as facades (read-only members throw, writable members and <c>args.vars</c> come back as patches applied to the real
/// arguments and variables). No LLM.
/// </summary>
[Collection("ScriptWorker")]
public sealed class ScriptHookTests
{
    /// <summary>A step that returns the JSON of its <see cref="Json"/> property (no ScriptStep in the run).</summary>
    [StepTag("JsonOut")]
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

    private IServiceProvider? _services;

    private YamlPipelineFactory Factory(string library, string source, Action<IServiceCollection>? more = null)
    {
        ServiceCollection services = new();
        services.AddScriptExtension();
        services.AddScriptLibrary(new ScriptLibrary(library, source));
        more?.Invoke(services);
        ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStepRegistry>().Add(typeof(JsonOutStep), "JsonOut");
        _services = provider;
        return new YamlPipelineFactory(provider, provider.GetRequiredService<IStepRegistry>(), new SchemaRegistry(), new Pipeline());
    }

    private static string Out(string json, string hooks, string id = "out")
        => $"name: h\nsteps:\n  - id: {id}\n    type: JsonOut\n    json: '{json}'\n    hooks:\n{hooks}";

    private PipelineContext NewContext(string label)
        => new(null, _services ?? throw new InvalidOperationException("Call Factory first.")) { CorrelationId = $"{label}-{Guid.NewGuid():N}" };

    private static async Task<IStepResult> RunAsync(YamlPipeline pipeline, PipelineContext context)
        => await pipeline.ExecuteAsync(new JObject { ["order"] = 7 }, context).WaitAsync(TimeSpan.FromSeconds(90));

    // ── postProcess ──────────────────────────────────────────────────────────

    private const string Post = """
        exports.stamp = function (args) {
          if (args.step.name !== 'out') throw new Error('step ' + args.step.name);
          if (args.attempt !== 1) throw new Error('attempt ' + args.attempt);
          if (args.isSuspended !== false) throw new Error('suspended');
          args.result.total = args.result.lines.reduce(function (a, l) { return a + l.qty; }, 0);
          args.result.lines[0].qty = 10;
          delete args.result.drop;
          args.vars.stamped = args.result.total;
        };
        exports.replace = function (args) { args.result = { replaced: true, was: args.result.lines.length }; };
        exports.untouched = function (args) { var n = args.result.lines.length; };
        """;

    [Fact]
    public async Task A_postProcess_hook_changes_the_result_in_place_through_the_patch_and_writes_variables()
    {
        YamlPipeline pipeline = Factory("post", Post).CreateFromYaml(Out("""{"lines":[{"qty":2},{"qty":3}],"drop":1}""", "      postProcess: js:post.stamp\n"));
        PipelineContext context = NewContext("jshook-post");

        IStepResult result = await RunAsync(pipeline, context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.IsType<JsonStepResult>(result);
        Assert.Equal("""{"lines":[{"qty":10},{"qty":3}],"total":5}""", ((JToken)result.Value!).ToString(Formatting.None));
        Assert.Equal(5, ((JToken)context.Metadata["stamped"]!).Value<int>());
    }

    [Fact]
    public async Task A_postProcess_hook_can_replace_the_whole_result_value_and_an_untouched_one_keeps_the_default()
    {
        YamlPipelineFactory factory = Factory("post", Post);

        IStepResult replaced = await RunAsync(factory.CreateFromYaml(Out("""{"lines":[{"qty":2}]}""", "      postProcess: js:post.replace\n")), NewContext("jshook-replace"));
        IStepResult kept = await RunAsync(factory.CreateFromYaml(Out("""{"lines":[{"qty":2}]}""", "      postProcess: js:post.untouched\n")), NewContext("jshook-kept"));

        Assert.Equal("""{"replaced":true,"was":1}""", ((JToken)replaced.Value!).ToString(Formatting.None));
        Assert.Equal("""{"lines":[{"qty":2}]}""", ((JToken)kept.Value!).ToString(Formatting.None));
    }

    private const string Errors = """
        exports.readOnly = function (args) { args.input.x = 1; };
        exports.attempt = function (args) { args.attempt = 2; };
        exports.returns = function (args) { return args.result; };
        exports.promise = function (args) { return Promise.resolve(1); };
        exports.throws = function (args) { throw new Error('nope'); };
        exports.notJson = function (args) { args.result = function () {}; };
        """;

    [Theory]
    [InlineData("readOnly", "args.input.x is read-only")]
    [InlineData("attempt", "args.attempt is read-only")]
    [InlineData("returns", "it returned object; a JavaScript hook returns nothing and changes args.result (or args.vars)")]
    [InlineData("promise", "a Promise (a JavaScript hook is synchronous)")]
    [InlineData("throws", "nope")]
    [InlineData("notJson", "args.result only takes JSON values")]
    public async Task A_failing_hook_ends_the_step_as_a_hook_error_naming_the_function(string function, string expected)
    {
        YamlPipeline pipeline = Factory("bad", Errors).CreateFromYaml(Out("""{"a":1}""", $"      postProcess: js:bad.{function}\n"));

        IStepResult result = await RunAsync(pipeline, NewContext("jshook-" + function));

        Assert.True(result.HasError);
        StepHookException hook = Assert.IsType<StepHookException>(result.Error!.OriginalException);
        Assert.Equal($"js:bad.{function}", hook.Hook);
        Assert.Contains(expected, result.Error.Message, StringComparison.Ordinal);
    }

    // ── onFinalize ───────────────────────────────────────────────────────────

    [Fact]
    public async Task An_onFinalize_hook_reads_the_final_result_and_writes_variables()
    {
        const string source = """
            exports.done = function (args) {
              args.vars.final = { ok: !args.hasError, error: args.error, value: args.result };
            };
            exports.tryWrite = function (args) { args.result = 1; };
            """;
        YamlPipelineFactory factory = Factory("fin", source);
        PipelineContext context = NewContext("jshook-final");

        IStepResult result = await RunAsync(factory.CreateFromYaml(Out("[1,2]", "      onFinalize: js:fin.done\n")), context);
        IStepResult refused = await RunAsync(factory.CreateFromYaml(Out("[1,2]", "      onFinalize: js:fin.tryWrite\n")), NewContext("jshook-final2"));

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal("""{"ok":true,"error":null,"value":[1,2]}""", ((JToken)context.Metadata["final"]!).ToString(Formatting.None));
        Assert.True(refused.HasError);
        Assert.Contains("args.result is read-only", refused.Error!.Message, StringComparison.Ordinal);
    }

    // ── The run's worker ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_run_whose_only_javascript_is_a_hook_starts_one_worker_and_a_runaway_hook_is_killed_and_respawned()
    {
        const string source = """
            exports.loop = function () { for (;;) { } };
            exports.mark = function (args) { args.vars[args.step.name] = true; };
            """;
        YamlPipelineFactory factory = Factory("w", source, s => s.ConfigureJsValidators(new JsValidatorOptions { TimeoutMs = 500 }));
        YamlPipeline pipeline = factory.CreateFromYaml("""
            name: runaway
            steps:
              - id: p
                type: ParallelStep
                failFast: false
                branches:
                  - id: looping
                    type: JsonOut
                    json: '1'
                    hooks:
                      onFinalize: js:w.loop
                  - id: first
                    type: JsonOut
                    json: '1'
                    hooks:
                      onFinalize: js:w.mark
              - id: after
                type: JsonOut
                json: '1'
                hooks:
                  onFinalize: js:w.mark
            """);
        PipelineContext context = NewContext("jshook-runaway");

        IStepResult result = await RunAsync(pipeline, context);

        ParallelResult parallel = context.StepResults.Values.OfType<ParallelResult>().Single();
        string errors = string.Join("\n", parallel.Value.Values.Where(r => r.HasError).Select(r => r.Error!.Message));
        Assert.False(result.HasError, result.Error?.Message);
        Assert.Contains("Hook 'js:w.loop' of step 'looping' failed: the script exceeded the time limit of 500 ms", errors, StringComparison.Ordinal);
        Assert.True((bool)(JToken)context.Metadata["after"]!);
        // One worker for the run (no ScriptStep in it) and one more after the runaway call killed it.
        Assert.Equal(2, ScriptWorkerClient.StartedWorkers(context.CorrelationId));
        Assert.Equal(0, ScriptWorkerClient.ActiveWorkers(context.CorrelationId));
    }

    [Fact]
    public async Task A_hook_function_the_library_does_not_export_fails_the_run_at_start()
    {
        YamlPipeline pipeline = Factory("lib", "exports.a = function () {};").CreateFromYaml(Out("1", "      postProcess: js:lib.missing\n"));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(pipeline, NewContext("jshook-missing")));

        Assert.Contains("step 'out', hook 'postProcess' 'js:lib.missing'", error.Message, StringComparison.Ordinal);
    }

    // ── Declaration ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("postProcess: js:nolib.f", "hook 'postProcess' (line 7, column 20) 'js:nolib.f': the JavaScript library 'nolib' is not known to this host")]
    [InlineData("postProcess: js:x", "is not 'js:<library>.<function>'")]
    public void A_bad_javascript_hook_is_a_build_error_that_names_the_step_and_the_line(string hook, string expected)
    {
        YamlPipelineFactory factory = Factory("lib", "exports.a = function () {};");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => factory.CreateFromYaml(Out("1", $"      {hook}\n")));

        Assert.Contains("Step 'out' (type 'JsonOut')", error.Message, StringComparison.Ordinal);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_slot_but_onConversationCreated_takes_javascript()
    {
        string[] csharpOnly = [.. HookSlots.For(typeof(YamlLlmStep)).Where(s => JsHook.Supports(s) != null).Select(s => s.Name)];

        Assert.Equal(["onConversationCreated"], csharpOnly);
        Assert.Contains("objects with behaviour", JsHook.Supports(Slot("onConversationCreated")), StringComparison.Ordinal);
        Assert.Equal(13, HookSlots.For(typeof(YamlLlmStep)).Count);
    }

    [Fact]
    public void An_onConversationCreated_javascript_hook_is_refused_by_the_factory()
    {
        (IHookFactory factory, _) = Alone("exports.a = function () {};");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => factory.CreateHook("js:llm.a", new YamlLlmStep(), Slot("onConversationCreated")));

        Assert.Contains("the slot 'onConversationCreated' is C#-only", error.Message, StringComparison.Ordinal);
    }

    // ── LLM slots, called outside a run (a worker of their own) ──────────────

    private static (IHookFactory Factory, IServiceProvider Services) Alone(string source)
    {
        ServiceProvider services = new ServiceCollection().AddScriptExtension().AddScriptLibrary(new ScriptLibrary("llm", source)).BuildServiceProvider();
        return (services.GetServices<IHookFactory>().Single(f => f.Prefix == "js:"), services);
    }

    private static HookSlot Slot(string name) => HookSlots.For(typeof(YamlLlmStep)).Single(s => s.Name == name);

    [Fact]
    public async Task A_buildUserMessage_hook_finds_the_steps_own_message_and_changes_it()
    {
        (IHookFactory factory, IServiceProvider services) = Alone("exports.wrap = function (args) { args.message = '>> ' + args.message + ' / ' + args.input.order; };");
        YamlLlmStep step = new();
        var hook = (HookAround<BuildMessageArgs, string?>)factory.CreateHook("js:llm.wrap", step, Slot("buildUserMessage"));

        string? message = await hook(new BuildMessageArgs(step, new JsonStepResult(step, new JObject { ["order"] = 7 }), new PipelineContext(null, services)), () => Task.FromResult<string?>("ping"));

        Assert.Equal(">> ping / 7", message);
    }

    [Fact]
    public async Task A_parseResponse_hook_can_reject_the_built_in_result()
    {
        (IHookFactory factory, IServiceProvider services) = Alone("""
            exports.check = function (args) {
              if (args.response.content.indexOf('bad') >= 0) { args.result = null; args.error = 'bad answer: ' + args.response.content; }
            };
            """);
        YamlLlmStep step = new();
        var hook = (HookAround<ParseResponseArgs, (IStepResult? Result, string? Error)>)factory.CreateHook("js:llm.check", step, Slot("parseResponse"));
        LlmResult response = LlmResult.Success([new LlmResponse { Content = "bad stuff" }]);
        IStepResult builtIn = new JsonStepResult(step, new JValue("bad stuff"));

        (IStepResult? result, string? error) = await hook(new ParseResponseArgs(step, response, new PipelineContext(null, services)), () => Task.FromResult<(IStepResult?, string?)>((builtIn, null)));

        Assert.Null(result);
        Assert.Equal("bad answer: bad stuff", error);
    }

    [Fact]
    public async Task An_onToolLimit_hook_decides_through_finalAnswerPrompt()
    {
        (IHookFactory factory, IServiceProvider services) = Alone("""
            exports.decide = function (args) { if (args.reason === 'LoopCut') args.finalAnswerPrompt = 'answer with ' + args.maxToolIterations; };
            """);
        YamlLlmStep step = new();
        var hook = (Func<ToolLimitArgs, Task<ToolLimitDecision>>)factory.CreateHook("js:llm.decide", step, Slot("onToolLimit"));
        PipelineContext context = new(null, services);

        ToolLimitDecision cut = await hook(new ToolLimitArgs(step, context, ToolLimitReason.LoopCut, "cut", 4));
        ToolLimitDecision limit = await hook(new ToolLimitArgs(step, context, ToolLimitReason.IterationLimit, "limit", 4));

        Assert.Equal("answer with 4", cut.FinalAnswerPrompt);
        Assert.Same(ToolLimitDecision.Fail, limit);
    }
}
