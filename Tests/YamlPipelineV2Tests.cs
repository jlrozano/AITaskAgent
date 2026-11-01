using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.YAML.Steps;
using Newtonsoft.Json.Linq;

namespace AITaskAgent.Tests;

/// <summary>
/// YAML v2: steps are mappings with <c>id</c> + <c>type</c> + flat configuration, validated against the
/// registry's description, created by <c>IStepRegistry.Create</c>, with nested Group/Parallel/Loop/Switch.
/// The LLM is always a <see cref="ScriptedLlmService"/> double.
/// </summary>
public sealed class YamlPipelineV2Tests
{
    private const string AnswerSchema = """
        { "type": "object", "title": "Answer", "properties": { "answer": { "type": "string" } }, "required": ["answer"] }
        """;

    private const string DoneSchema = """
        { "type": "object", "properties": { "done": { "type": "boolean" } }, "required": ["done"] }
        """;

    // ── Loading ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_simple_v2_pipeline_loads_orders_and_runs()
    {
        var kit = new YamlTestKit(call => call == 1 ? """{ "answer": "42" }""" : "final text");
        await kit.Schemas.AddAsync("Answer", AnswerSchema);

        var pipeline = kit.Factory.CreateFromYaml("""
            name: simple
            steps:
              - id: second
                type: LlmStep
                dependsOn: [first]
                inputSchema: Answer
                userPrompt: "The answer was {{input.answer}}"
              - id: first
                type: LlmStep
                outputSchema: Answer
                maxRetries: 2
                userPrompt: Ask
            """);

        Assert.Equal("simple", pipeline.Name);
        Assert.Equal(["first", "second"], pipeline.Steps.Select(s => s.Name));
        var first = Assert.IsType<YamlLlmStep>(pipeline.Steps[0]);
        Assert.Equal(2, first.MaxRetries);
        Assert.NotNull(first.OutputJsonSchema);

        var result = await pipeline.ExecuteAsync(new JObject(), YamlTestKit.NewContext());

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal("final text", result.Value);
        Assert.Equal("The answer was 42", kit.Llm.Requests.ElementAt(1).User);
    }

    [Fact]
    public async Task Multiline_and_special_characters_reach_the_prompt_intact()
    {
        var kit = new YamlTestKit();
        var pipeline = kit.Factory.CreateFromYaml("""
            name: special
            steps:
              - id: literal
                type: LlmStep
                useConversationHistory: false
                systemPrompt: |
                  Línea 1: "comillas" y 'simples' {llave} } { \ barra
                  # no es comentario: valor
                  Emoji 🚀 y acentos áéíóú ñ
                userPrompt: >
                  Hola {{input.name}}: "x" {a} \n literal
                  sigue # aquí
              - id: quoted
                type: LlmStep
                useConversationHistory: false
                userPrompt: "Comillas \"dobles\", barra \\ y dos puntos: {x} # no comentario"
              - id: single
                type: LlmStep
                useConversationHistory: false
                userPrompt: 'It''s {y}: "ok" \ 🚀'
            """);

        var result = await pipeline.ExecuteAsync(JObject.Parse("""{ "name": "Ana" }"""), YamlTestKit.NewContext());

        Assert.False(result.HasError, result.Error?.Message);
        var requests = kit.Llm.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Equal("Línea 1: \"comillas\" y 'simples' {llave} } { \\ barra\n# no es comentario: valor\nEmoji 🚀 y acentos áéíóú ñ\n", requests[0].System);
        Assert.Equal("Hola Ana: \"x\" {a} \\n literal sigue # aquí\n", requests[0].User);
        Assert.Equal("Comillas \"dobles\", barra \\ y dos puntos: {x} # no comentario", requests[1].User);
        Assert.Equal("It's {y}: \"ok\" \\ 🚀", requests[2].User);
    }

    // ── Control flow ─────────────────────────────────────────────────────────

    private const string SwitchYaml = """
        name: routing
        steps:
          - id: route
            type: SwitchStep
            on: input.category
            cases:
              a:
                - { id: a1, type: Probe, label: a }
              b:
                - id: inner
                  type: SwitchStep
                  on: "{{ input.level }}"
                  cases:
                    low:
                      - { id: low1, type: Probe, label: low }
                    high:
                      - { id: high1, type: Probe, label: high }
            default:
              - { id: d1, type: Probe, label: default }
          - id: tail
            type: Probe
            label: tail
            dependsOn: [route]
        """;

    [Fact]
    public async Task A_nested_switch_picks_the_matching_branches()
    {
        var kit = new YamlTestKit();
        var pipeline = kit.Factory.CreateFromYaml(SwitchYaml);
        var context = YamlTestKit.NewContext();

        var result = await pipeline.ExecuteAsync(JObject.Parse("""{ "category": "B", "level": "high" }"""), context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(["high", "tail"], ProbeStep.Trace(context));
    }

    [Fact]
    public async Task A_switch_without_a_matching_case_runs_its_default()
    {
        var kit = new YamlTestKit();
        var pipeline = kit.Factory.CreateFromYaml(SwitchYaml);
        var context = YamlTestKit.NewContext();

        var result = await pipeline.ExecuteAsync(JObject.Parse("""{ "category": "zzz" }"""), context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(["default", "tail"], ProbeStep.Trace(context));
    }

    [Fact]
    public async Task A_loop_runs_until_its_break_condition_holds()
    {
        var kit = new YamlTestKit(call => call < 3 ? """{ "done": false }""" : """{ "done": true }""");
        await kit.Schemas.AddAsync("Done", DoneSchema);
        var pipeline = kit.Factory.CreateFromYaml("""
            name: loop
            steps:
              - id: retry
                type: LoopStep
                maxIterations: 10
                outputIterationToMetadata: iteration
                steps:
                  - id: ask
                    type: LlmStep
                    outputSchema: Done
                    userPrompt: "Iteration {{context.iteration}}"
                  - id: stop
                    type: BreakStep
                    when: input.done
            """);

        var result = await pipeline.ExecuteAsync(new JObject(), YamlTestKit.NewContext());

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(3, kit.Llm.Calls);
        Assert.Equal(["Iteration 1", "Iteration 2", "Iteration 3"], kit.Llm.Requests.Select(r => r.User));
        Assert.True(((JToken)result.Value!)["done"]!.Value<bool>());
    }

    [Fact]
    public async Task Parallel_runs_every_branch_and_a_branch_can_be_a_list_of_steps()
    {
        var kit = new YamlTestKit();
        var pipeline = kit.Factory.CreateFromYaml("""
            name: fan
            steps:
              - id: fan
                type: ParallelStep
                maxDegreeOfParallelism: 2
                branches:
                  - id: left
                    type: Probe
                    label: left
                  - id: right
                    steps:
                      - { id: right1, type: Probe, label: right1 }
                      - { id: right2, type: Probe, label: right2, output: '{"side": "right"}' }
            """);
        var context = YamlTestKit.NewContext();

        var result = await pipeline.ExecuteAsync(new JObject(), context);

        Assert.False(result.HasError, result.Error?.Message);
        var parallel = Assert.IsType<ParallelResult>(result);
        Assert.Equal(["left", "right"], parallel.Value.Keys.Order());
        Assert.Equal("right", ((JToken)parallel.Value["right"].Value!)["side"]!.Value<string>());
        var trace = ProbeStep.Trace(context);
        Assert.Equal(3, trace.Length);
        Assert.True(Array.IndexOf(trace, "right1") < Array.IndexOf(trace, "right2"));
    }

    [Fact]
    public async Task A_group_runs_its_steps_in_order_and_hands_on_the_last_result()
    {
        var kit = new YamlTestKit();
        var pipeline = kit.Factory.CreateFromYaml("""
            name: group
            steps:
              - id: g
                type: GroupStep
                steps:
                  - { id: g1, type: Probe, label: g1 }
                  - { id: g2, type: Probe, label: g2, output: '{"n": 2}' }
              - id: after
                type: Probe
                label: after
                dependsOn: [g]
            """);
        var context = YamlTestKit.NewContext();

        var result = await pipeline.ExecuteAsync(new JObject(), context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(["g1", "g2", "after"], ProbeStep.Trace(context));
        Assert.Equal(2, ((JToken)result.Value!)["n"]!.Value<int>());
    }

    // ── Output schema ────────────────────────────────────────────────────────

    [Fact]
    public async Task An_output_that_does_not_match_its_schema_fails_with_the_violations()
    {
        var kit = new YamlTestKit(_ => """{ "wrong": 1 }""");
        await kit.Schemas.AddAsync("Answer", AnswerSchema);
        var pipeline = kit.Factory.CreateFromYaml("""
            name: invalid
            steps:
              - id: ask
                type: LlmStep
                outputSchema: Answer
                userPrompt: Ask
            """);

        var result = await pipeline.ExecuteAsync(new JObject(), YamlTestKit.NewContext());

        Assert.True(result.HasError);
        Assert.Contains("does not match the JSON schema 'Answer'", result.Error!.Message);
        Assert.Contains("answer", result.Error.Message);
        Assert.Equal(3, kit.Llm.Calls); // default MaxRetries: each retry gets the violations as feedback
        Assert.Contains("\"answer\"", kit.Llm.Requests.First().System); // schema injected (provider without JSON support)
    }

    [Fact]
    public async Task An_input_that_does_not_match_the_first_inputSchema_is_rejected_before_running()
    {
        var kit = new YamlTestKit();
        await kit.Schemas.AddAsync("Answer", AnswerSchema);
        var pipeline = kit.Factory.CreateFromYaml("""
            name: guarded
            steps:
              - id: ask
                type: LlmStep
                inputSchema: Answer
            """);

        var result = await pipeline.ExecuteAsync(JObject.Parse("""{ "nope": 1 }"""), YamlTestKit.NewContext());

        Assert.True(result.HasError);
        Assert.Contains("does not match the inputSchema of step 'ask'", result.Error!.Message);
        Assert.Equal(0, kit.Llm.Calls);
    }

    // ── Build-time errors ────────────────────────────────────────────────────

    private static InvalidOperationException BuildFails(string yaml, YamlTestKit? kit = null)
        => Assert.Throws<InvalidOperationException>(() => (kit ?? new YamlTestKit()).Factory.CreateFromYaml(yaml));

    [Fact]
    public void A_dependsOn_cycle_is_detected()
    {
        var ex = BuildFails("""
            steps:
              - { id: a, type: Probe, dependsOn: [b] }
              - { id: b, type: Probe, dependsOn: [a] }
            """);

        Assert.Contains("Cycle detected", ex.Message);
        Assert.Contains("a, b", ex.Message);
    }

    [Fact]
    public void A_dependsOn_on_a_missing_step_fails()
    {
        var ex = BuildFails("""
            steps:
              - { id: a, type: Probe, dependsOn: [ghost] }
            """);

        Assert.Contains("Step 'a' depends on 'ghost' which does not exist", ex.Message);
    }

    [Fact]
    public void dependsOn_is_rejected_on_nested_steps_and_cannot_target_them()
    {
        var nestedDependsOn = BuildFails("""
            steps:
              - id: g
                type: GroupStep
                steps:
                  - { id: g1, type: Probe }
                  - { id: g2, type: Probe, dependsOn: [g1] }
            """);
        Assert.Contains("'dependsOn' is only allowed on top-level steps", nestedDependsOn.Message);

        var targetsNested = BuildFails("""
            steps:
              - id: g
                type: GroupStep
                steps:
                  - { id: g1, type: Probe }
              - { id: b, type: Probe, dependsOn: [g1] }
            """);
        Assert.Contains("nested inside 'g'", targetsNested.Message);
    }

    [Fact]
    public void An_unknown_type_lists_the_available_types()
    {
        var ex = BuildFails("""
            steps:
              - { id: a, type: Nope }
            """);

        Assert.Contains("Step 'a'", ex.Message);
        Assert.Contains("unknown type 'Nope'", ex.Message);
        Assert.Contains("LlmStep", ex.Message);
        Assert.Contains("SwitchStep", ex.Message);
    }

    [Fact]
    public void An_unknown_property_lists_the_valid_ones()
    {
        var ex = BuildFails("""
            steps:
              - id: ask
                type: LlmStep
                colour: blue
            """);

        Assert.Contains("Step 'ask' (type 'LlmStep') has unknown property 'colour'", ex.Message);
        Assert.Contains("Valid properties:", ex.Message);
        Assert.Contains("userPrompt", ex.Message);
        Assert.Contains("outputSchema", ex.Message);
    }

    [Theory]
    [InlineData("prompt", "hello")]
    // 'tools' is accepted since it names tools (YamlLlmStepToolsTests); the live Tools property stays hidden.
    [InlineData("tagHandlers", "[x]")]
    [InlineData("enrichable", "x")]
    public void An_LlmStep_rejects_the_inherited_properties_that_do_nothing_in_YAML(string property, string value)
    {
        var ex = BuildFails($"""
            steps:
              - id: ask
                type: LlmStep
                {property}: {value}
            """);

        Assert.Contains($"Step 'ask' (type 'LlmStep') has unknown property '{property}'", ex.Message);
        Assert.Contains("Valid properties:", ex.Message);
        Assert.Contains("userPrompt", ex.Message);
    }

    [Fact]
    public void An_LlmStep_keeps_the_inherited_properties_that_work_in_YAML()
    {
        var pipeline = new YamlTestKit().Factory.CreateFromYaml("""
            steps:
              - id: ask
                type: LlmStep
                maxRetries: 2
                maxToolIterations: 3
                timeout: "00:00:30"
            """);

        var step = Assert.IsType<YamlLlmStep>(pipeline.Steps[0]);
        Assert.Equal(2, step.MaxRetries);
        Assert.Equal(3, step.MaxToolIterations);
        Assert.Equal(TimeSpan.FromSeconds(30), step.Timeout);
    }

    [Fact]
    public void A_value_of_the_wrong_type_fails()
    {
        var ex = BuildFails("""
            steps:
              - id: loop
                type: LoopStep
                maxIterations: many
                steps: []
            """);

        Assert.Contains("property 'maxIterations'", ex.Message);
        Assert.Contains("expects integer", ex.Message);
    }

    [Fact]
    public void A_duplicate_id_fails_also_when_nested()
    {
        var ex = BuildFails("""
            steps:
              - id: g
                type: GroupStep
                steps:
                  - { id: g, type: Probe }
            """);

        Assert.Contains("Duplicate step id 'g'", ex.Message);
    }

    [Fact]
    public void A_schema_that_is_not_registered_fails()
    {
        var ex = BuildFails("""
            steps:
              - { id: ask, type: LlmStep, outputSchema: Missing }
            """);

        Assert.Contains("outputSchema 'Missing'", ex.Message);
        Assert.Contains("is not registered", ex.Message);
    }

    [Fact]
    public async Task Different_schemas_between_dependent_steps_are_a_mismatch()
    {
        var kit = new YamlTestKit();
        await kit.Schemas.AddAsync("Answer", AnswerSchema);
        await kit.Schemas.AddAsync("Done", DoneSchema);

        var ex = BuildFails("""
            steps:
              - { id: a, type: LlmStep, outputSchema: Answer }
              - { id: b, type: LlmStep, inputSchema: Done, dependsOn: [a] }
            """, kit);

        Assert.Contains("Schema mismatch: step 'a' outputs schema 'Answer' but step 'b' expects input schema 'Done'", ex.Message);
    }

    [Fact]
    public void Schemas_are_rejected_on_steps_that_do_not_take_them()
    {
        var kit = new YamlTestKit();
        kit.Schemas.Add("Any", new NJsonSchema.JsonSchema());

        var ex = BuildFails("""
            steps:
              - { id: p, type: Probe, outputSchema: Any }
            """, kit);

        Assert.Contains("does not accept 'inputSchema'/'outputSchema'", ex.Message);
    }

    [Fact]
    public void A_missing_llm_role_fails_while_building()
    {
        var ex = BuildFails("""
            steps:
              - { id: ask, type: LlmStep, profileName: ghost }
            """);

        Assert.Contains("Step 'ask' (type 'LlmStep')", ex.Message);
        Assert.Contains("LLM role 'ghost' is not configured. Available roles: default.", ex.Message);
    }

    [Fact]
    public void The_v1_tag_syntax_is_rejected_with_a_hint()
    {
        var ex = BuildFails("""
            steps:
              - !LlmStep
                stepId: a
            """);

        Assert.Contains("'!LlmStep'", ex.Message);
        Assert.Contains("type: LlmStep", ex.Message);
    }

    // ── Regressions from the restructure review ──────────────────────────────

    // R6: the up-front RequiredContext check must reach every case and the default of a declarative
    // switch, so an LLM step there fails before the first step runs (and before any LLM call).
    private const string LlmInCase = """
        name: switch-context
        steps:
          - id: first
            type: Probe
            label: first
          - id: route
            type: SwitchStep
            dependsOn: [first]
            on: input.kind
            cases:
              x:
                - { id: ask, type: LlmStep, userPrompt: hi }
            default:
              - { id: d, type: Probe, label: d }
        """;

    private const string LlmInDefault = """
        name: switch-context
        steps:
          - id: first
            type: Probe
            label: first
          - id: route
            type: SwitchStep
            dependsOn: [first]
            on: input.kind
            cases:
              x:
                - { id: c, type: Probe, label: c }
            default:
              - { id: ask, type: LlmStep, userPrompt: hi }
        """;

    [Theory]
    [InlineData(LlmInCase)]
    [InlineData(LlmInDefault)]
    public async Task An_llm_step_inside_a_switch_fails_with_a_base_context_before_any_step_runs(string yaml)
    {
        var kit = new YamlTestKit();
        var pipeline = kit.Factory.CreateFromYaml(yaml);
        var context = new PipelineContext();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.ExecuteAsync(JObject.Parse("""{ "kind": "x" }"""), context));

        Assert.Contains("'ask'", ex.Message);
        Assert.Contains("LlmPipelineContext", ex.Message);
        Assert.Empty(ProbeStep.Trace(context));
        Assert.Equal(0, kit.Llm.Calls);
    }

    // R5: the switch matches ignoring case and surrounding spaces, so keys equal under that rule collide.
    [Fact]
    public void Switch_cases_that_differ_only_in_case_or_surrounding_spaces_fail()
    {
        var ex = BuildFails("""
            steps:
              - id: sw
                type: SwitchStep
                on: input
                cases:
                  A: [ { id: s1, type: Probe } ]
                  'a ': [ { id: s2, type: Probe } ]
            """);

        Assert.Contains("'sw.cases' repeats the entry 'a '", ex.Message);
        Assert.Contains("same as 'A'", ex.Message);
    }

    // R7: a null value means "not set" for every property type, value types included.
    [Fact]
    public void A_null_value_keeps_the_default_also_for_value_types()
    {
        var pipeline = new YamlTestKit().Factory.CreateFromYaml("""
            name: nulls
            steps:
              - id: loop
                type: LoopStep
                maxIterations: ~
                steps:
                  - id: p
                    type: Probe
                    label: null
                    weight:
            """);

        var loop = Assert.IsType<LoopStep>(pipeline.Steps[0]);
        Assert.Equal(new LoopStep().MaxIterations, loop.MaxIterations);
        var probe = Assert.IsType<ProbeStep>(loop.Steps[0]);
        Assert.Equal("", probe.Label);
        Assert.Equal(0, probe.Weight);
    }

    // R8: an empty 'when' used to break on the first pass by accident (the empty lookup rendered as
    // literal text, which is truthy); it is a configuration error now.
    [Theory]
    [InlineData("\"\"")]
    [InlineData("'   '")]
    public void An_empty_break_condition_fails_when_building(string when)
    {
        var ex = BuildFails($"""
            steps:
              - id: loop
                type: LoopStep
                steps:
                  - id: stop
                    type: BreakStep
                    when: {when}
            """);

        Assert.Contains("'stop'", ex.Message);
        Assert.Contains("'when' is empty", ex.Message);
    }

    // R4: a self-referencing alias used to overflow the stack (process crash) and nested aliases to
    // expand for minutes; both fail fast with a message. Small legitimate aliases keep working.
    [Fact]
    public void A_self_referencing_alias_fails_with_a_clear_message()
    {
        var ex = BuildFails("""
            name: x
            steps: &s
              - id: a
                type: GroupStep
                steps: *s
            """);

        Assert.Contains("alias", ex.Message);
        Assert.Contains("line", ex.Message);
    }

    [Fact]
    public void Nested_alias_expansion_has_a_budget()
    {
        string yaml = "a: &a [x,x,x,x,x,x,x,x,x]\n";
        for (char c = 'b'; c <= 'h'; c++)
        {
            string previous = "*" + (char)(c - 1);
            yaml += $"{c}: &{c} [{string.Join(",", Enumerable.Repeat(previous, 9))}]\n";
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ex = BuildFails(yaml + "name: x\nsteps: []\n");

        Assert.Contains("more than", ex.Message);
        Assert.Contains("aliases", ex.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Small_aliases_still_work()
    {
        var pipeline = new YamlTestKit().Factory.CreateFromYaml("""
            name: aliases
            steps:
              - id: one
                type: Probe
                label: &shared same
              - id: two
                type: Probe
                label: *shared
                dependsOn: [one]
            """);
        var context = YamlTestKit.NewContext();

        var result = await pipeline.ExecuteAsync(new JObject(), context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(["same", "same"], ProbeStep.Trace(context));
    }
}
