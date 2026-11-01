using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Script.Configuration;
using AITaskAgent.YAML.Execution;
using AITaskAgent.YAML.Schemas;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace AITaskAgent.Script.Tests;

/// <summary>
/// Integration: YAML v2 pipelines made only of <c>ScriptStep</c>s (plus Group/Switch), built through the
/// step registry that <c>AddScriptExtension()</c> fills. No LLM.
/// </summary>
[Collection("ScriptWorker")]
public sealed class ScriptYamlPipelineTests
{
    private static YamlPipelineFactory NewFactory()
    {
        ServiceProvider services = new ServiceCollection().AddScriptExtension().BuildServiceProvider();
        return new YamlPipelineFactory(
            services, services.GetRequiredService<IStepRegistry>(), new SchemaRegistry(), new Pipeline());
    }

    [Fact]
    public async Task A_group_of_script_steps_chains_input_to_output()
    {
        // Same text as Samples/YamlPipelineDemo/examples/script-invoice.yaml (kept in sync by hand).
        var pipeline = NewFactory().CreateFromYaml("""
            name: invoice-totals
            steps:
              - id: prepare
                type: GroupStep
                steps:
                  - id: totals
                    type: ScriptStep
                    script: |
                      // Subtotal, tax and total per invoice; strings with ":", "#", "{}" and accents pass untouched.
                      const subtotal = input.lines.reduce((s, l) => s + l.qty * l.unitPrice, 0);
                      const tax = Math.round(subtotal * input.taxRate * 100) / 100;
                      return { customer: input.customer, note: "Cliente: " + input.customer + " #1 {ok}", subtotal, tax, total: subtotal + tax };
                  - id: label
                    type: ScriptStep
                    timeoutMs: 2000
                    script: |
                      return Object.assign({}, input, { size: input.total >= 100 ? "large" : "small" });
            """);

        var result = await pipeline.ExecuteAsync(
            JObject.Parse("""{ "customer": "Ñandú S.A.", "taxRate": 0.21, "lines": [ { "qty": 2, "unitPrice": 30 }, { "qty": 1, "unitPrice": 40 } ] }"""),
            new PipelineContext());

        Assert.False(result.HasError, result.Error?.Message);
        var json = Assert.IsType<JObject>(result.Value);
        Assert.Equal(100, (double)json["subtotal"]!);
        Assert.Equal(21, (double)json["tax"]!);
        Assert.Equal("large", (string?)json["size"]);
        Assert.Equal("Cliente: Ñandú S.A. #1 {ok}", (string?)json["note"]);
    }

    [Theory]
    [InlineData("small", "small-branch")]
    [InlineData("large", "large-branch")]
    [InlineData("other", "default-branch")]
    public async Task A_switch_routes_on_a_script_result(string size, string expected)
    {
        var pipeline = NewFactory().CreateFromYaml("""
            name: routed
            steps:
              - id: classify
                type: ScriptStep
                script: |
                  return { size: input.size };
              - id: route
                type: SwitchStep
                dependsOn: [classify]
                on: input.size
                cases:
                  small:
                    - { id: small_s, type: ScriptStep, script: "return { branch: 'small-branch' };" }
                  large:
                    - id: large_g
                      type: GroupStep
                      steps:
                        - { id: large_a, type: ScriptStep, script: "return { step: 1 };" }
                        - { id: large_b, type: ScriptStep, script: "return { branch: 'large-branch', prev: input.step };" }
                default:
                  - { id: other_s, type: ScriptStep, script: "return { branch: 'default-branch' };" }
            """);

        var result = await pipeline.ExecuteAsync(new JObject { ["size"] = size }, new PipelineContext());

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(expected, (string?)((JObject)result.Value!)["branch"]);
    }

    [Fact]
    public void An_unknown_property_is_rejected_with_the_valid_ones()
    {
        var ex = Assert.ThrowsAny<Exception>(() => NewFactory().CreateFromYaml("""
            name: bad
            steps:
              - id: s
                type: ScriptStep
                scrip: "return 1;"
            """));

        Assert.Contains("scrip", ex.Message);
        Assert.Contains("script", ex.Message);
    }

    [Fact]
    public async Task A_failing_script_fails_the_pipeline_with_the_step_id()
    {
        var pipeline = NewFactory().CreateFromYaml("""
            name: failing
            steps:
              - id: boom
                type: ScriptStep
                script: |
                  throw new Error("kaput");
            """);

        var result = await pipeline.ExecuteAsync(new JObject(), new PipelineContext());

        Assert.True(result.HasError);
        Assert.Contains("'boom'", result.Error!.Message);
        Assert.Contains("kaput", result.Error.Message);
    }

    [Fact]
    public async Task The_limits_are_configurable_in_yaml_up_to_the_ceilings()
    {
        var pipeline = NewFactory().CreateFromYaml("""
            name: limits
            steps:
              - id: capped
                type: ScriptStep
                maxHeapMb: 128
                maxStackKb: 2048
                script: "return { ok: true };"
            """);

        var step = Assert.IsType<AITaskAgent.Script.Steps.ScriptStep>(pipeline.Steps[0]);
        Assert.Equal(128, step.MaxHeapMb);
        Assert.Equal(2048, step.MaxStackKb);

        var result = await pipeline.ExecuteAsync(new JObject(), new PipelineContext());
        Assert.False(result.HasError, result.Error?.Message);
    }

    [Theory]
    [InlineData("maxHeapMb: 129", "maxHeapMb")]
    [InlineData("maxHeapMb: 4", "maxHeapMb")]
    [InlineData("maxStackKb: 4096", "maxStackKb")]
    [InlineData("maxStackKb: 8", "maxStackKb")]
    public void A_limit_beyond_the_ceilings_fails_when_the_pipeline_is_built(string property, string name)
    {
        var ex = Assert.ThrowsAny<Exception>(() => NewFactory().CreateFromYaml($"""
            name: greedy
            steps:
              - id: s
                type: ScriptStep
                {property}
                script: "return 1;"
            """));

        Assert.Contains("Step 's' (type 'ScriptStep')", ex.Message);
        Assert.Contains(name, ex.Message);
        Assert.Contains("must be between", ex.Message);
    }
}
