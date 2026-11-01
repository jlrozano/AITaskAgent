using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NJsonSchema;
using System.ComponentModel;

namespace AITaskAgent.Tests;

/// <summary>
/// <see cref="StepRegistry"/> (the instance registry that replaced the static <c>StepManager</c>)
/// and its materialization through <c>AddAITaskAgentCore</c> / <c>AddExtension</c>.
/// </summary>
public sealed class StepRegistryTests
{
    public enum Mode { Fast, Careful }

    public sealed class Greeter
    {
        public string Greeting => "hello";
    }

    [StepTag("RegistryProbe")]
    [Description("Probe step for the registry tests.")]
    public sealed class RegistryProbeStep : StepBase
    {
        public RegistryProbeStep() : base("probe", typeof(object), typeof(object)) { }

        public RegistryProbeStep(Greeter greeter) : this() => Greeter = greeter;

        [JsonIgnore]
        public Greeter? Greeter { get; }

        [Description("Text the probe carries.")]
        public string Text { get; set; } = "";

        public string? OptionalNote { get; set; }

        public int Count { get; set; }

        public Mode Mode { get; set; }

        public List<string> Tags { get; set; } = [];

        [JsonIgnore]
        public string Ignored { get; set; } = "";

        public string ReadOnly => "fixed";

        public string PrivatelySet { get; private set; } = "";

        protected override Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
            => throw new NotSupportedException("not executed by these tests");
    }

    [StepTag("RegistryOtherProbe")]
    public sealed class OtherProbeStep : StepBase
    {
        public OtherProbeStep() : base("other", typeof(object), typeof(object)) { }

        protected override Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
            => throw new NotSupportedException("not executed by these tests");
    }

    [StepTag("RegistryAbstractProbe")]
    public abstract class AbstractProbeStep() : StepBase("abstract", typeof(object), typeof(object));

    [StepTag("RegistryGenericProbe")]
    public sealed class GenericProbeStep<T>() : StepBase("generic", typeof(T), typeof(T))
    {
        protected override Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
            => throw new NotSupportedException("not executed by these tests");
    }

    public sealed class UntaggedStep() : StepBase("untagged", typeof(object), typeof(object))
    {
        protected override Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
            => throw new NotSupportedException("not executed by these tests");
    }

    [StepTag("RegistryDefaultsProbe")]
    public sealed class DefaultsOnlyCtorStep : StepBase
    {
        public DefaultsOnlyCtorStep(Greeter? greeter = null) : base("defaults", typeof(object), typeof(object))
        {
            Greeter = greeter;
            Initialized = true;
        }

        [JsonIgnore]
        public Greeter? Greeter { get; }

        [JsonIgnore]
        public bool Initialized { get; }

        protected override Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
            => throw new NotSupportedException("not executed by these tests");
    }

    [StepTag("RegistryNeedsDi")]
    public sealed class NeedsServiceStep(Greeter greeter) : StepBase("needs", typeof(object), typeof(object))
    {
        [JsonIgnore]
        public Greeter Greeter { get; } = greeter;

        protected override Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
            => throw new NotSupportedException("not executed by these tests");
    }

    private sealed class ProbeExtension : IExtension
    {
        public int RegisterCalls { get; private set; }

        public string Name => "Tests.Probe";

        public void Register(IExtensionBuilder builder)
        {
            RegisterCalls++;
            builder.AddStep<RegistryProbeStep>()
                   .AddStep<OtherProbeStep>("AliasedOther");
            builder.Services.AddSingleton<Greeter>();
        }
    }

    // ── Registration ─────────────────────────────────────────────────────────

    [Fact]
    public void Add_uses_the_StepTag_or_the_explicit_tag()
    {
        IStepRegistry registry = new StepRegistry();

        registry.Add<RegistryProbeStep>();
        registry.Add(typeof(OtherProbeStep), "Custom");

        Assert.True(registry.TryGetType("RegistryProbe", out var probe));
        Assert.Equal(typeof(RegistryProbeStep), probe);
        Assert.True(registry.TryGetType("Custom", out var other));
        Assert.Equal(typeof(OtherProbeStep), other);
        Assert.False(registry.TryGetType("RegistryOtherProbe", out _));
        Assert.Equal(new[] { "Custom", "RegistryProbe" }, registry.GetRegisteredTypes().Keys.Order());
    }

    [Fact]
    public void Adding_the_same_type_under_the_same_tag_twice_is_a_no_op()
    {
        var registry = new StepRegistry();

        registry.Add(typeof(RegistryProbeStep));
        registry.Add(typeof(RegistryProbeStep));

        Assert.Single(registry.GetRegisteredTypes());
    }

    [Fact]
    public void A_tag_already_registered_with_another_type_is_rejected_naming_both_types()
    {
        var registry = new StepRegistry();
        registry.Add(typeof(RegistryProbeStep));

        var ex = Assert.Throws<InvalidOperationException>(() => registry.Add(typeof(OtherProbeStep), "RegistryProbe"));

        Assert.Contains("RegistryProbe", ex.Message);
        Assert.Contains(nameof(RegistryProbeStep), ex.Message);
        Assert.Contains(nameof(OtherProbeStep), ex.Message);
        Assert.Equal(typeof(RegistryProbeStep), registry.GetRegisteredTypes()["RegistryProbe"]);
    }

    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(AbstractProbeStep))]
    [InlineData(typeof(GenericProbeStep<>))]
    public void Types_that_cannot_be_registered_are_rejected(Type type)
    {
        var registry = new StepRegistry();

        Assert.Throws<ArgumentException>(() => registry.Add(type));
        Assert.Empty(registry.GetRegisteredTypes());
    }

    [Fact]
    public void A_step_without_StepTag_is_registered_under_its_class_name()
    {
        var registry = new StepRegistry();

        registry.Add(typeof(UntaggedStep));

        Assert.Equal(typeof(UntaggedStep), registry.GetRegisteredTypes()[nameof(UntaggedStep)]);
    }

    [Fact]
    public void A_class_name_already_registered_with_another_type_is_rejected()
    {
        var registry = new StepRegistry();
        registry.Add(typeof(UntaggedStep));

        Assert.Throws<InvalidOperationException>(() => registry.Add(typeof(OtherProbeStep), nameof(UntaggedStep)));
    }

    [Fact]
    public void AddFromAssembly_registers_tagged_concrete_steps_and_ignores_abstract_and_open_generic_ones()
    {
        var registry = new StepRegistry();

        registry.AddFromAssembly(typeof(StepRegistryTests).Assembly);

        var types = registry.GetRegisteredTypes();
        Assert.Equal(typeof(RegistryProbeStep), types["RegistryProbe"]);
        Assert.Equal(typeof(OtherProbeStep), types["RegistryOtherProbe"]);
        Assert.False(types.ContainsKey("RegistryAbstractProbe"));
        Assert.False(types.ContainsKey("RegistryGenericProbe"));
        Assert.DoesNotContain(typeof(UntaggedStep), types.Values);
    }

    [Fact]
    public void AddFromAssembly_on_the_core_registers_its_tagged_control_flow_steps()
    {
        var registry = new StepRegistry();

        registry.AddFromAssembly(typeof(StepRegistry).Assembly);

        var types = registry.GetRegisteredTypes();
        Assert.Equal(typeof(GroupStep), types["GroupStep"]);
        Assert.Equal(typeof(LoopStep), types["LoopStep"]);
        Assert.Equal(typeof(ParallelStep), types["ParallelStep"]);
        Assert.Equal(typeof(BreakStep), types["BreakStep"]);
    }

    // ── Creation ─────────────────────────────────────────────────────────────

    [Fact]
    public void Create_with_an_unknown_tag_lists_the_registered_tags()
    {
        var registry = new StepRegistry();
        registry.Add(typeof(RegistryProbeStep));
        registry.Add(typeof(OtherProbeStep));

        var ex = Assert.Throws<KeyNotFoundException>(() => registry.Create("Nope"));

        Assert.Contains("'Nope'", ex.Message);
        Assert.Contains("RegistryOtherProbe, RegistryProbe", ex.Message);
    }

    [Fact]
    public void Create_populates_the_properties_from_the_configuration()
    {
        var registry = new StepRegistry();
        registry.Add(typeof(RegistryProbeStep));
        var config = JObject.FromObject(new
        {
            text = "line 1\nline \"2\": {x} <tag> & ñ",
            count = 7,
            mode = "Careful",
            tags = new[] { "a", "b" }
        });

        var step = Assert.IsType<RegistryProbeStep>(registry.Create("RegistryProbe", config));

        Assert.Equal("line 1\nline \"2\": {x} <tag> & ñ", step.Text);
        Assert.Equal(7, step.Count);
        Assert.Equal(Mode.Careful, step.Mode);
        Assert.Equal(new[] { "a", "b" }, step.Tags);
        Assert.Null(step.Greeter);
    }

    [Fact]
    public void Create_resolves_constructor_dependencies_from_the_given_provider()
    {
        var registry = new StepRegistry();
        registry.Add(typeof(RegistryProbeStep));
        var greeter = new Greeter();
        using var provider = new ServiceCollection().AddSingleton(greeter).BuildServiceProvider();

        var step = Assert.IsType<RegistryProbeStep>(
            registry.Create("RegistryProbe", new JObject { ["text"] = "with di" }, provider));

        Assert.Same(greeter, step.Greeter);
        Assert.Equal("with di", step.Text);
    }

    // Regression (review R9): without a provider, a type with no parameterless constructor used to come
    // back from RuntimeHelpers.GetUninitializedObject, with no constructor or initializer run.
    [Fact]
    public void Create_without_a_provider_runs_a_constructor_whose_parameters_all_have_defaults()
    {
        var registry = new StepRegistry();
        registry.Add(typeof(DefaultsOnlyCtorStep));

        var step = Assert.IsType<DefaultsOnlyCtorStep>(registry.Create("RegistryDefaultsProbe"));

        Assert.True(step.Initialized);
        Assert.Null(step.Greeter);
        Assert.Equal("defaults", step.Name);
    }

    [Fact]
    public void Create_fails_clearly_when_no_constructor_can_be_called()
    {
        var registry = new StepRegistry();
        registry.Add(typeof(NeedsServiceStep));
        using var empty = new ServiceCollection().BuildServiceProvider();

        var withoutProvider = Assert.Throws<InvalidOperationException>(() => registry.Create("RegistryNeedsDi"));
        var withEmptyProvider = Assert.Throws<InvalidOperationException>(() => registry.Create("RegistryNeedsDi", services: empty));

        Assert.Contains(nameof(NeedsServiceStep), withoutProvider.Message);
        Assert.Contains("cannot be created", withoutProvider.Message);
        Assert.Contains("IServiceProvider", withoutProvider.Message);
        Assert.Contains("cannot be created", withEmptyProvider.Message);
    }

    // ── Description ──────────────────────────────────────────────────────────

    [Fact]
    public void Describe_builds_a_schema_from_the_public_writable_properties()
    {
        var registry = new StepRegistry();
        registry.Add(typeof(RegistryProbeStep));

        StepDescription description = registry.Describe("RegistryProbe");

        Assert.Equal("RegistryProbe", description.TagName);
        Assert.Equal(typeof(RegistryProbeStep), description.StepType);
        Assert.Equal("Probe step for the registry tests.", description.Description);
        var schema = Assert.IsType<JsonSchema>(description.Parameters);
        var props = schema.Properties;
        Assert.Equal(JsonObjectType.String, props["text"].Type);
        Assert.Equal("Text the probe carries.", props["text"].Description);
        Assert.Equal(JsonObjectType.Integer, props["count"].Type);
        Assert.Equal(JsonObjectType.String, props["mode"].Type);
        Assert.Equal(new[] { "Fast", "Careful" }, props["mode"].Enumeration.Select(e => e!.ToString()));
        Assert.Equal(JsonObjectType.Array, props["tags"].Type);
        Assert.Equal(JsonObjectType.String, Assert.Single(props["tags"].Items).Type);
        Assert.Contains("maxRetries", props.Keys);
        Assert.DoesNotContain("ignored", props.Keys);
        Assert.DoesNotContain("greeter", props.Keys);
        Assert.DoesNotContain("readOnly", props.Keys);
        Assert.DoesNotContain("privatelySet", props.Keys);
        Assert.DoesNotContain("name", props.Keys);
        Assert.Contains("text", schema.RequiredProperties);
        Assert.DoesNotContain("optionalNote", schema.RequiredProperties);
    }

    [Fact]
    public void Describe_by_type_uses_the_registered_tag()
    {
        var registry = new StepRegistry();
        registry.Add(typeof(OtherProbeStep), "Aliased");

        Assert.Equal("Aliased", registry.Describe(typeof(OtherProbeStep)).TagName);
        Assert.Equal("RegistryProbe", registry.Describe(typeof(RegistryProbeStep)).TagName);
    }

    [Fact]
    public void Describe_with_an_unknown_tag_fails()
    {
        Assert.Throws<KeyNotFoundException>(() => new StepRegistry().Describe("Nope"));
    }

    // ── DI materialization ───────────────────────────────────────────────────

    [Fact]
    public void AddAITaskAgentCore_registers_a_singleton_registry_with_the_core_steps()
    {
        var services = new ServiceCollection().AddAITaskAgentCore();
        services.AddAITaskAgentCore();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IStepRegistry>();

        Assert.Same(registry, provider.GetRequiredService<IStepRegistry>());
        Assert.Single(services, d => d.ServiceType == typeof(IStepRegistry));
        Assert.True(registry.TryGetType("GroupStep", out _));
    }

    [Fact]
    public void AddExtension_registers_its_steps_and_services_once()
    {
        var extension = new ProbeExtension();
        var services = new ServiceCollection().AddAITaskAgentCore();

        services.AddExtension(extension).AddExtension(extension);

        Assert.Equal(1, extension.RegisterCalls);
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IStepRegistry>();
        Assert.True(registry.TryGetType("RegistryProbe", out _));
        Assert.Equal(typeof(OtherProbeStep), registry.GetRegisteredTypes()["AliasedOther"]);
        Assert.True(registry.TryGetType("GroupStep", out _));
        Assert.Same(extension, Assert.Single(provider.GetServices<IExtension>()));

        var step = Assert.IsType<RegistryProbeStep>(registry.Create("RegistryProbe", services: provider));
        Assert.Same(provider.GetRequiredService<Greeter>(), step.Greeter);
    }

    [Fact]
    public void AddAITaskAgent_adds_the_llm_extension()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddAITaskAgent();
        services.AddAITaskAgentLlm();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<LlmExtension>(Assert.Single(provider.GetServices<IExtension>()));
        Assert.True(provider.GetRequiredService<IStepRegistry>().TryGetType("LoopStep", out _));
    }
}
