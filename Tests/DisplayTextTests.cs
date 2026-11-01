using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Tools.Base;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using AITaskAgent.Support.Template;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITaskAgent.Tests;

/// <summary>
/// <c>Display</c> of the start and end events of steps and tools (D-065): templates declared by the step or tool,
/// by YAML or by an <see cref="IDisplayTextProvider"/>, over the <c>input</c>/<c>context</c> model of prompts.
/// </summary>
public sealed class DisplayTextTests
{
    private sealed class DictionaryProvider(Dictionary<string, string> templates) : IDisplayTextProvider
    {
        public string? GetTemplate(DisplayKind kind, string name, DisplayPhase phase) =>
            templates.GetValueOrDefault($"{kind}:{name}:{phase}");
    }

    private sealed class ThrowingProvider : IDisplayTextProvider
    {
        public string? GetTemplate(DisplayKind kind, string name, DisplayPhase phase) => throw new InvalidOperationException("secret-value");
    }

    private sealed class EchoTool : LlmTool
    {
        public bool Fail { get; init; }
        public override string Name => "echo_tool";
        public override string Description => "test";
        protected override BinaryData ParametersSchema => BinaryData.FromString("{}");

        protected override Task<string> InternalExecuteAsync(string argumentsJson, PipelineContext context, ILogger logger, CancellationToken cancellationToken) =>
            Fail ? throw new InvalidOperationException("boom") : Task.FromResult("{\"rows\":3,\"name\":\"Sales\"}");
    }

    private static (PipelineContext Context, CapturingChannel Channel, WarningLoggerFactory Logs) NewContext(IDisplayTextProvider? provider = null)
    {
        CapturingChannel channel = new();
        WarningLoggerFactory logs = new();
        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(logs);
        if (provider is not null)
        {
            services.AddSingleton(provider);
        }

        PipelineContext context = new(channel, services.BuildServiceProvider());
        context.Metadata["Customer"] = "Acme";
        return (context, channel, logs);
    }

    private static Task<IStepResult> Run(IStep step, PipelineContext context) =>
        new Pipeline().ExecuteAsync("Test", [step], new StepResult<string>(new EmptyStep("Seed"), "seed"), context);

    private static (StepStartedEvent Start, StepCompletedEvent End) StepEvents(CapturingChannel channel) =>
        (channel.Events.OfType<StepStartedEvent>().Single(), channel.Events.OfType<StepCompletedEvent>().Single());

    [Fact]
    public async Task Step_declares_start_and_end_text_with_input_context_data_and_output()
    {
        var (context, channel, _) = NewContext();
        HookProbeStep step = new()
        {
            Output = "{\"v\":7}",
            DisplayStart = "Reading {{ input }} for {{ context.customer }} ({{ step.name }}, attempt {{ step.attempt }})",
            DisplayEnd = "Done: v={{ output.v }} ok={{ success }} in {{ durationMs ?? 0 }} ms"
        };

        IStepResult result = await Run(step, context);

        Assert.False(result.HasError, result.Error?.Message);
        var (start, end) = StepEvents(channel);
        Assert.Equal("Reading seed for Acme (HookProbe, attempt 1)", start.Display);
        Assert.StartsWith("Done: v=7 ok=True in ", end.Display);
        Assert.EndsWith(" ms", end.Display);
    }

    [Fact]
    public async Task Without_a_template_Display_is_null_and_nothing_else_changes()
    {
        var (context, channel, logs) = NewContext();

        IStepResult result = await Run(new HookProbeStep(), context);

        Assert.False(result.HasError);
        var (start, end) = StepEvents(channel);
        Assert.Null(start.Display);
        Assert.Null(end.Display);
        Assert.Empty(logs.Warnings);
    }

    [Fact]
    public async Task Failed_step_end_text_sees_error_and_success_false()
    {
        var (context, channel, _) = NewContext();
        HookProbeStep step = new() { FailFirst = 99, MaxRetries = 1, DisplayEnd = "ok={{ success }} error={{ error }}" };

        IStepResult result = await Run(step, context);

        Assert.True(result.HasError);
        StepCompletedEvent end = channel.Events.OfType<StepCompletedEvent>().Single();
        Assert.Equal("ok=False error=recoverable", end.Display);
    }

    [Fact]
    public async Task Retry_start_events_carry_their_own_attempt_in_the_text()
    {
        var (context, channel, _) = NewContext();
        HookProbeStep step = new() { FailFirst = 1, DisplayStart = "attempt {{ step.attempt }}" };

        await Run(step, context);

        Assert.Equal(["attempt 1", "attempt 2"], channel.Events.OfType<StepStartedEvent>().Select(e => e.Display));
    }

    [Fact]
    public async Task Provider_gives_templates_by_name_and_the_step_own_template_wins()
    {
        var (context, channel, _) = NewContext(new DictionaryProvider(new()
        {
            ["Step:HookProbe:Start"] = "from provider {{ step.name }}",
            ["Step:HookProbe:End"] = "provider end"
        }));
        HookProbeStep step = new() { DisplayEnd = "own end" };

        await Run(step, context);

        var (start, end) = StepEvents(channel);
        Assert.Equal("from provider HookProbe", start.Display);
        Assert.Equal("own end", end.Display);
    }

    [Fact]
    public async Task Provider_that_throws_or_template_with_include_gives_null_and_a_warning_without_values()
    {
        var (context, channel, logs) = NewContext(new ThrowingProvider());
        await Run(new HookProbeStep(), context);
        Assert.All(channel.Events.OfType<StepStartedEvent>(), e => Assert.Null(e.Display));
        Assert.NotEmpty(logs.Warnings);
        Assert.DoesNotContain(logs.Warnings, w => w.Contains("secret-value", StringComparison.Ordinal));

        var (context2, channel2, logs2) = NewContext();
        HookProbeStep step = new() { DisplayStart = "{{ include 'C:/Windows/win.ini' }}", DisplayEnd = "@[C:/Windows/win.ini]" };
        IStepResult result = await Run(step, context2);
        Assert.False(result.HasError);
        var (start, end) = StepEvents(channel2);
        Assert.Null(start.Display);
        Assert.Null(end.Display);
        Assert.NotEmpty(logs2.Warnings);
    }

    [Fact]
    public async Task Text_is_cut_to_the_limit_and_an_empty_render_is_null()
    {
        var (context, channel, _) = NewContext();
        HookProbeStep step = new() { DisplayStart = new string('x', 900), DisplayEnd = "{{ missing }}" };

        await Run(step, context);

        var (start, end) = StepEvents(channel);
        Assert.Equal(DisplayTextRenderer.MaxLength, start.Display!.Length);
        Assert.EndsWith("\u2026", start.Display);
        Assert.Null(end.Display);
    }

    [Fact]
    public async Task Tool_start_and_end_text_use_args_result_and_context()
    {
        var (context, channel, _) = NewContext();
        EchoTool tool = new()
        {
            DisplayStart = "Querying {{ args.table }} for {{ context.customer }}",
            DisplayEnd = "{{ tool }}: {{ result.rows }} rows of {{ result.name }} (success {{ success }})"
        };

        string result = await tool.ExecuteAsync("{\"table\":\"sales\"}", context, "StepA", NullLogger.Instance);

        Assert.Contains("rows", result);
        Assert.Equal("Querying sales for Acme", channel.Events.OfType<ToolStartedEvent>().Single().Display);
        Assert.Equal("echo_tool: 3 rows of Sales (success True)", channel.Events.OfType<ToolCompletedEvent>().Single().Display);
    }

    [Fact]
    public async Task Tool_args_that_are_not_json_stay_text_and_a_failed_call_exposes_the_error()
    {
        var (context, channel, _) = NewContext();
        EchoTool tool = new() { Fail = true, DisplayStart = "args={{ args }}", DisplayEnd = "failed={{ error }} {{ success }}" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ExecuteAsync("plain text", context, "StepA", NullLogger.Instance));

        Assert.Equal("args=plain text", channel.Events.OfType<ToolStartedEvent>().Single().Display);
        Assert.Equal("failed=boom False", channel.Events.OfType<ToolCompletedEvent>().Single().Display);
    }

    [Fact]
    public async Task Tool_without_template_uses_the_provider_and_otherwise_stays_null()
    {
        var (context, channel, _) = NewContext(new DictionaryProvider(new() { ["Tool:echo_tool:Start"] = "provider {{ args.a }}" }));

        await new EchoTool().ExecuteAsync("{\"a\":1}", context, "StepA", NullLogger.Instance);

        Assert.Equal("provider 1", channel.Events.OfType<ToolStartedEvent>().Single().Display);
        Assert.Null(channel.Events.OfType<ToolCompletedEvent>().Single().Display);
    }

    [Fact]
    public async Task Yaml_display_block_sets_start_and_end_templates_on_any_step()
    {
        HookKit kit = new();
        var pipeline = kit.Factory.CreateFromYaml("""
            name: shown
            steps:
              - id: probe
                type: HookProbe
                display:
                  start: "Probing {{ input }}"
                  end: "Result {{ output.v }}"
            """);

        StepBase step = Assert.IsAssignableFrom<StepBase>(pipeline.Steps[0]);
        Assert.Equal("Probing {{ input }}", step.DisplayStart);
        Assert.Equal("Result {{ output.v }}", step.DisplayEnd);

        CapturingChannel channel = new();
        PipelineContext context = new(channel, kit.Services);
        await pipeline.ExecuteAsync(new Newtonsoft.Json.Linq.JObject(), context);
        Assert.StartsWith("Result 1", channel.Events.OfType<StepCompletedEvent>().Single().Display);
    }

    [Fact]
    public void Yaml_display_with_an_unknown_key_or_a_non_mapping_is_rejected()
    {
        HookKit kit = new();

        InvalidOperationException unknown = Assert.Throws<InvalidOperationException>(() => kit.Factory.CreateFromYaml("""
            name: bad
            steps:
              - id: probe
                type: HookProbe
                display: { middle: "x" }
            """));
        Assert.Contains("start, end", unknown.Message);

        Assert.Throws<InvalidOperationException>(() => kit.Factory.CreateFromYaml("""
            name: bad
            steps:
              - id: probe
                type: HookProbe
                display: "text"
            """));
    }
}
