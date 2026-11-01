using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Models;
using AITaskAgent.YAML.Steps;
using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.Support.Template;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace AITaskAgent.Tests;

/// <summary>
/// <see cref="IExtension.Info"/> (default, comparison), the discovery helpers (<see cref="ExtensionDiscovery"/>, <see cref="AssemblyDiscovery"/>), the
/// builder members for tools, hooks, response types and templates, and <see cref="ExtensionCapture"/>.
/// </summary>
public sealed class ExtensionInfoTests
{
    // ── Fixtures: what a plugin assembly could hold, found by scan of this assembly ──────────────────────────

    [StepTag("InfoProbeStep")]
    public sealed class InfoProbeStep : StepBase
    {
        public InfoProbeStep() : base("InfoProbeStep", typeof(IStepResult), typeof(IStepResult)) { }

        protected override Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    public sealed class InfoProbeTool : ITool
    {
        public string Name => "info_probe";

        public string Description => "probe";

        public ToolDefinition GetDefinition() => new() { Name = Name, Description = Description, ParametersJsonSchema = "{}" };

        public Task<string> ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, ILogger logger, CancellationToken cancellationToken = default)
            => Task.FromResult("ok");
    }

    public sealed class InfoProbeTools : IToolSource
    {
        public IEnumerable<ITool> Create(PluginContext context) => [new InfoProbeTool()];
    }

    public sealed class InfoProbeTemplates : ITemplateSource
    {
        public ITemplateProvider? Create(PluginContext context) => null;
    }

    public static class InfoProbeHooks
    {
        [Hook(Name = "InfoProbe.Done", Description = "Named hook.")]
        public static Task Done(FinalizeArgs args) => Task.CompletedTask;

        [Validator(Description = "Checks.")]
        public static Task<(bool IsValid, string? Error)> Check(ValidateArgs args) => Task.FromResult((true, (string?)null));
    }

    [ResponseType("InfoProbeFinding")]
    public sealed class InfoProbeFinding
    {
        public string? Text { get; set; }
    }

    private sealed class Bare : IExtension
    {
        public string Name => "Tests.Bare";

        public void Register(IExtensionBuilder builder)
        {
        }
    }

    private static readonly Assembly Own = typeof(ExtensionInfoTests).Assembly;

    // ── Info() and Difference ────────────────────────────────────────────────

    [Fact]
    public void An_extension_that_does_not_override_Info_declares_nothing_and_a_default_configuration_and_root_sections()
    {
        IExtension extension = new Bare();

        Assert.Empty(extension.Info().Entries);
        Assert.Same(ExtensionInfo.Empty, extension.Info());
        Assert.Empty(extension.RootSections);
        Assert.Empty(extension.ConfigurationDefaults(new PluginContext("Bare", "1.0.0", "C:/x", new ConfigurationBuilder().Build())));
        Assert.Null(extension.ConfigurationModel());
    }

    [Fact]
    public void Difference_compares_by_name_for_named_kinds_and_by_class_for_the_rest()
    {
        ExtensionInfo declared = new() { Entries = [new(ExtensionKinds.Step, "A", "T.A"), new(ExtensionKinds.Tool, "x", "T.Tool1"), new(ExtensionKinds.Hook, "H")] };
        ExtensionInfo actual = new() { Entries = [new(ExtensionKinds.Step, "A", "Other.Type"), new(ExtensionKinds.Tool, "", "T.Tool1"), new(ExtensionKinds.Tool, "", "T.Tool2")] };

        (IReadOnlyList<ExtensionEntry> missing, IReadOnlyList<ExtensionEntry> extra) = ExtensionInfo.Difference(declared, actual);

        Assert.Equal(["H"], missing.Select(e => e.Name));
        Assert.Equal(["T.Tool2"], extra.Select(e => e.Type));
    }

    [Fact]
    public void Merge_and_With_add_entries_without_changing_the_original()
    {
        ExtensionInfo one = ExtensionInfo.Empty.With(new ExtensionEntry(ExtensionKinds.Step, "A"));
        ExtensionInfo two = one.Merge(ExtensionInfo.Empty.With(new ExtensionEntry(ExtensionKinds.Hook, "H")));

        Assert.Single(one.Entries);
        Assert.Equal(["step:A", "hook:H"], two.Entries.Select(e => e.Key));
        Assert.Empty(ExtensionInfo.Empty.Entries);
    }

    // ── Discovery ────────────────────────────────────────────────────────────

    [Fact]
    public void ExtensionDiscovery_finds_hooks_validators_and_response_types_with_the_scanners_naming()
    {
        IReadOnlyList<DiscoveredHook> hooks = ExtensionDiscovery.Hooks(Own);

        DiscoveredHook named = hooks.Single(h => h.Name == "InfoProbe.Done");
        Assert.Equal((HookKind.Hook, "Named hook."), (named.Kind, named.Description));
        Assert.Equal(HookKind.StepValidator, hooks.Single(h => h.Name == "ExtensionInfoTests.InfoProbeHooks.Check").Kind);
        Assert.Contains(ExtensionDiscovery.ResponseTypes(Own), r => r.Name == "InfoProbeFinding" && r.Type == typeof(InfoProbeFinding));
        Assert.Contains(ExtensionDiscovery.TaggedSteps(Own), s => s.Tag == "InfoProbeStep" && s.Type == typeof(InfoProbeStep));
    }

    [Fact]
    public void AssemblyDiscovery_describes_tools_tool_sources_and_template_sources_besides_the_rest()
    {
        ExtensionInfo info = AssemblyDiscovery.Describe(Own);

        Assert.Contains(info.Of(ExtensionKinds.Tool), e => e.Type == typeof(InfoProbeTool).FullName);
        Assert.Contains(info.Of(ExtensionKinds.ToolSource), e => e.Type == typeof(InfoProbeTools).FullName);
        Assert.Contains(info.Of(ExtensionKinds.TemplateSource), e => e.Type == typeof(InfoProbeTemplates).FullName);
        Assert.Contains(info.Of(ExtensionKinds.Step), e => e.Name == "InfoProbeStep");
        Assert.Contains(info.Of(ExtensionKinds.Hook), e => e.Name == "InfoProbe.Done");
        Assert.Contains(info.Of(ExtensionKinds.ResponseType), e => e.Name == "InfoProbeFinding");
    }

    [Fact]
    public void The_framework_extensions_declare_what_they_register()
    {
        Assert.Contains(new LlmExtension().Info().Of(ExtensionKinds.Step), e => e.Name == "LlmStep" && e.Type == typeof(YamlLlmStep).FullName);
        Assert.Contains(new LlmExtension().Info().Of(ExtensionKinds.Configuration), e => e.Type != null);
    }

    // ── Capture and builder members ──────────────────────────────────────────

    [Fact]
    public void A_capture_keeps_what_Register_asked_for_and_reports_it_as_ActualInfo()
    {
        ExtensionCapture capture = new(plugin: new PluginContext("Probe", "1.0.0", "C:/probe", new ConfigurationBuilder().Build()));

        capture.AddStep<InfoProbeStep>()
            .AddTool<InfoProbeTool>()
            .AddTool(new InfoProbeTool())
            .AddToolSource(new InfoProbeTools())
            .AddTemplates(new InfoProbeTemplates())
            .AddHooksFromAssembly(Own)
            .AddResponseTypesFromAssembly(Own);
        capture.Services.AddSingleton<InfoProbeTool>();

        Assert.Equal("Probe", capture.Plugin!.Id);
        Assert.Equal(("InfoProbeStep", typeof(InfoProbeStep)), capture.CapturedSteps.Single());
        Assert.Equal([typeof(InfoProbeTool)], capture.ToolTypes);
        Assert.Single(capture.ToolInstances);
        Assert.Single(capture.ToolSources);
        Assert.Single(capture.TemplateSources);
        Assert.Contains(capture.Hooks, h => h.Name == "InfoProbe.Done");
        Assert.Contains(capture.ResponseTypes, r => r.Name == "InfoProbeFinding");
        Assert.Single(capture.Services);
        ExtensionInfo actual = capture.ActualInfo();
        Assert.Contains(actual.Of(ExtensionKinds.Tool), e => e.Name == "info_probe");
        Assert.Contains(actual.Of(ExtensionKinds.Tool), e => e.Name.Length == 0 && e.Type == typeof(InfoProbeTool).FullName);
        Assert.Single(actual.Of(ExtensionKinds.ToolSource));
        Assert.Single(actual.Of(ExtensionKinds.TemplateSource));
    }

    [Fact]
    public void A_capture_defaults_an_untagged_step_to_its_full_name_and_refuses_a_tag_twice()
    {
        ExtensionCapture capture = new();

        capture.Steps.Add(typeof(Plain));

        Assert.Equal(typeof(Plain).FullName, capture.CapturedSteps.Single().Tag);
        Assert.Throws<InvalidOperationException>(() => capture.Steps.Add(typeof(Plain)));
        Assert.True(capture.Steps.TryGetType(typeof(Plain).FullName!, out Type? found) && found == typeof(Plain));
    }

    public sealed class Plain : StepBase
    {
        public Plain() : base("Plain", typeof(IStepResult), typeof(IStepResult)) { }

        protected override Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class ScanExtension : IExtension
    {
        public string Name => "Tests.Scan";

        public void Register(IExtensionBuilder builder)
        {
            builder.AddTool<InfoProbeTool>().AddToolSource(new InfoProbeTools()).AddTemplates(new InfoProbeTemplates())
                .AddHooksFromAssembly(Own).AddResponseTypesFromAssembly(Own);
        }
    }

    [Fact]
    public void The_service_collection_builder_wires_tools_templates_hooks_and_response_types()
    {
        ServiceCollection services = new();

        services.AddExtension(new ScanExtension());

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.IsType<InfoProbeTool>(Assert.Single(provider.GetServices<ITool>()));
        Assert.IsType<InfoProbeTools>(Assert.Single(provider.GetServices<IToolSource>()));
        Assert.IsType<InfoProbeTemplates>(Assert.Single(provider.GetServices<ITemplateSource>()));
        Assert.Contains(provider.GetRequiredService<HookRegistry>().List(), h => h.Name == "InfoProbe.Done" && h.Source == "di");
        Assert.Contains(provider.GetServices<RegisteredResponseType>(), r => r.Name == "InfoProbeFinding");
    }

    [Fact]
    public void A_builder_that_cannot_wire_something_says_so()
    {
        IExtensionBuilder builder = new MinimalBuilder();

        Assert.Throws<NotSupportedException>(() => builder.AddToolType(typeof(InfoProbeTool)));
        Assert.Throws<NotSupportedException>(() => builder.AddHooksFromAssembly(Own));
        Assert.Throws<NotSupportedException>(() => builder.AddResponseType("x", typeof(InfoProbeFinding)));
        Assert.Null(builder.Plugin);
    }

    private sealed class MinimalBuilder : IExtensionBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IStepRegistry Steps => throw new NotSupportedException();

        public IExtensionBuilder AddStep(Type stepType, string? tag = null) => this;

        public IExtensionBuilder AddStep<TStep>(string? tag = null) where TStep : IStep => this;

        public IExtensionBuilder AddStepsFromAssembly(Assembly assembly) => this;
    }
}
