using AITaskAgent.Core.Execution;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using Newtonsoft.Json.Linq;

namespace AITaskAgent.Tests;

/// <summary>
/// <see cref="SuspendStep"/> in YAML: the run stops with an <see cref="AwaitingInputResult"/>, executing
/// it again with the same context and the response as input resumes it, the steps that had finished
/// are not run again, and the response is the step's result. At the root and inside Group, Loop and
/// Switch. No real LLM: <see cref="ScriptedLlmService"/>.
/// </summary>
public sealed class SuspendStepTests
{
    private readonly string _id = Guid.NewGuid().ToString("N");

    private string Key(string name) => $"{_id}-{name}";

    private string Yaml(string yaml) => yaml.Replace("KEY-", _id + "-", StringComparison.Ordinal);

    private static YamlTestKit Kit(Func<int, string>? respond = null)
    {
        YamlTestKit kit = new(respond);
        kit.Registry.Add(typeof(CountingStep));
        return kit;
    }

    [Fact]
    public async Task At_the_root_it_suspends_and_resumes_with_the_response_as_its_result()
    {
        var kit = Kit(_ => "done");
        var pipeline = kit.Factory.CreateFromYaml(Yaml("""
            name: approval
            steps:
              - id: before
                type: Counter
                key: KEY-before
                output: '{ "order": { "id": 7, "total": 12.5 } }'
              - id: ask
                type: SuspendStep
                dependsOn: [before]
                message: "Confirm order {{input.order.id}}"
                dataToReview: input.order
                marker: awaiting-approval
              - id: answer
                type: LlmStep
                dependsOn: [ask]
                userPrompt: "Approved: {{input.approved}}"
            """));
        var context = YamlTestKit.NewContext();

        var first = await pipeline.ExecuteAsync(new JObject(), context);

        var waiting = Assert.IsType<AwaitingInputResult>(first);
        Assert.False(first.HasError, first.Error?.Message);
        Assert.Equal("awaiting-approval", first.SuspendInfo);
        Assert.Equal("Confirm order 7", waiting.Message);
        Assert.Null(waiting.RejectionReason);
        Assert.True(JToken.DeepEquals(JObject.Parse("""{ "id": 7, "total": 12.5 }"""), (JToken?)waiting.DataToReview));
        Assert.True(PipelineResume.IsSuspended(context));
        Assert.Equal("ask", PipelineResume.GetSuspendedStepPath(context));
        Assert.Equal(1, CountingStep.Count(Key("before")));
        Assert.Equal(0, kit.Llm.Calls);

        var resumed = await pipeline.ExecuteAsync(JObject.Parse("""{ "approved": "yes" }"""), context);

        Assert.False(resumed.HasError, resumed.Error?.Message);
        Assert.Null(resumed.SuspendInfo);
        Assert.Equal("done", resumed.Value);
        Assert.Equal(1, CountingStep.Count(Key("before")));
        Assert.Equal("Approved: yes", Assert.Single(kit.Llm.Requests).User);
        Assert.False(PipelineResume.IsSuspended(context));
        Assert.Null(PipelineResume.GetSuspendedStepPath(context));
        var answer = Assert.IsType<JsonStepResult>(context.StepResults["ask"]);
        Assert.Equal("yes", answer.Value!["approved"]!.Value<string>());
    }

    [Fact]
    public async Task A_text_response_reaches_the_next_step_as_text()
    {
        var kit = Kit();
        var pipeline = kit.Factory.CreateFromYaml(Yaml("""
            name: text
            steps:
              - { id: ask, type: SuspendStep, message: Name? }
              - { id: echo, type: LlmStep, dependsOn: [ask], userPrompt: "Hello {{input}}" }
            """));
        var context = YamlTestKit.NewContext();

        var first = await pipeline.ExecuteAsync("ignored", context);
        Assert.Equal(SuspendStep.DefaultMarker, first.SuspendInfo);
        Assert.Equal("Name?", first.Value);

        var resumed = await pipeline.ExecuteAsync("Ana", context);

        Assert.False(resumed.HasError, resumed.Error?.Message);
        Assert.Equal("Hello Ana", Assert.Single(kit.Llm.Requests).User);
    }

    [Fact]
    public async Task A_response_that_does_not_match_the_output_schema_suspends_again_with_the_reason()
    {
        var kit = Kit();
        await kit.Schemas.AddAsync("Approval", """
            { "type": "object", "properties": { "approved": { "type": "boolean" } }, "required": ["approved"] }
            """);
        var pipeline = kit.Factory.CreateFromYaml(Yaml("""
            name: schema
            steps:
              - { id: before, type: Counter, key: KEY-before }
              - { id: ask, type: SuspendStep, dependsOn: [before], outputSchema: Approval }
              - { id: after, type: Counter, key: KEY-after, dependsOn: [ask] }
            """));
        var context = YamlTestKit.NewContext();

        await pipeline.ExecuteAsync(new JObject(), context);
        var rejected = await pipeline.ExecuteAsync(JObject.Parse("""{ "approved": "maybe" }"""), context);

        var waiting = Assert.IsType<AwaitingInputResult>(rejected);
        Assert.False(rejected.HasError);
        Assert.NotNull(waiting.RejectionReason);
        Assert.Contains("approved", waiting.RejectionReason);
        Assert.True(PipelineResume.IsSuspended(context));
        Assert.Equal(0, CountingStep.Count(Key("after")));

        var accepted = await pipeline.ExecuteAsync(JObject.Parse("""{ "approved": true }"""), context);

        Assert.False(accepted.HasError, accepted.Error?.Message);
        Assert.Null(accepted.SuspendInfo);
        Assert.True(((JToken)accepted.Value!)["approved"]!.Value<bool>());
        Assert.Equal(1, CountingStep.Count(Key("before")));
        Assert.Equal(1, CountingStep.Count(Key("after")));
    }

    [Fact]
    public async Task Inside_a_group_the_finished_children_are_not_run_again()
    {
        var kit = Kit();
        var pipeline = kit.Factory.CreateFromYaml(Yaml("""
            name: group
            steps:
              - id: grp
                type: GroupStep
                steps:
                  - { id: g1, type: Counter, key: KEY-g1 }
                  - { id: ask, type: SuspendStep }
                  - { id: g2, type: Counter, key: KEY-g2 }
              - { id: tail, type: Counter, key: KEY-tail, dependsOn: [grp] }
            """));
        var context = YamlTestKit.NewContext();

        var first = await pipeline.ExecuteAsync(new JObject(), context);
        Assert.NotNull(first.SuspendInfo);
        Assert.Equal("grp/ask", PipelineResume.GetSuspendedStepPath(context));

        var resumed = await pipeline.ExecuteAsync(JObject.Parse("""{ "value": 1 }"""), context);

        Assert.False(resumed.HasError, resumed.Error?.Message);
        Assert.Null(resumed.SuspendInfo);
        Assert.Equal(1, ((JToken)resumed.Value!)["value"]!.Value<int>());
        Assert.Equal(1, CountingStep.Count(Key("g1")));
        Assert.Equal(1, CountingStep.Count(Key("g2")));
        Assert.Equal(1, CountingStep.Count(Key("tail")));
    }

    [Fact]
    public async Task Inside_a_loop_each_iteration_suspends_under_its_own_path_and_resumes_there()
    {
        var kit = Kit();
        var pipeline = kit.Factory.CreateFromYaml(Yaml("""
            name: loop
            steps:
              - id: loop
                type: LoopStep
                maxIterations: 5
                steps:
                  - { id: work, type: Counter, key: KEY-work }
                  - { id: ask, type: SuspendStep, message: Continue? }
                  - { id: stop, type: BreakStep, when: input.done }
              - { id: tail, type: Counter, key: KEY-tail, dependsOn: [loop] }
            """));
        var context = YamlTestKit.NewContext();

        var first = await pipeline.ExecuteAsync(new JObject(), context);
        Assert.NotNull(first.SuspendInfo);
        Assert.Equal("loop/Iter_1/ask", PipelineResume.GetSuspendedStepPath(context));
        Assert.Equal(1, CountingStep.Count(Key("work")));

        var second = await pipeline.ExecuteAsync(JObject.Parse("""{ "done": false }"""), context);
        Assert.NotNull(second.SuspendInfo);
        Assert.Equal("loop/Iter_2/ask", PipelineResume.GetSuspendedStepPath(context));
        Assert.Equal(2, CountingStep.Count(Key("work")));

        var last = await pipeline.ExecuteAsync(JObject.Parse("""{ "done": true }"""), context);

        Assert.False(last.HasError, last.Error?.Message);
        Assert.Null(last.SuspendInfo);
        Assert.Equal(2, CountingStep.Count(Key("work")));
        Assert.Equal(1, CountingStep.Count(Key("tail")));
        Assert.False(PipelineResume.IsSuspended(context));
    }

    [Fact]
    public async Task Inside_a_switch_case_it_resumes_in_the_case_that_suspended()
    {
        var kit = Kit();
        var pipeline = kit.Factory.CreateFromYaml(Yaml("""
            name: switch
            steps:
              - { id: before, type: Counter, key: KEY-before, output: '{ "kind": "ask" }' }
              - id: route
                type: SwitchStep
                dependsOn: [before]
                on: input.kind
                cases:
                  ask:
                    - { id: ask, type: SuspendStep }
                    - { id: afterAsk, type: Counter, key: KEY-afterAsk }
                default:
                  - { id: other, type: Counter, key: KEY-other }
            """));
        var context = YamlTestKit.NewContext();

        var first = await pipeline.ExecuteAsync(new JObject(), context);
        Assert.NotNull(first.SuspendInfo);
        Assert.Equal("route/route[ask]/ask", PipelineResume.GetSuspendedStepPath(context));

        var resumed = await pipeline.ExecuteAsync(JObject.Parse("""{ "x": 1 }"""), context);

        Assert.False(resumed.HasError, resumed.Error?.Message);
        Assert.Equal(1, ((JToken)resumed.Value!)["x"]!.Value<int>());
        Assert.Equal(1, CountingStep.Count(Key("before")));
        Assert.Equal(1, CountingStep.Count(Key("afterAsk")));
        Assert.Equal(0, CountingStep.Count(Key("other")));
    }

    [Fact]
    public void An_empty_marker_is_rejected_when_the_pipeline_is_built()
    {
        var kit = Kit();
        var ex = Assert.Throws<InvalidOperationException>(() => kit.Factory.CreateFromYaml("""
            name: bad
            steps:
              - { id: ask, type: SuspendStep, marker: "" }
            """));
        Assert.Contains("marker", ex.Message);
    }
}
