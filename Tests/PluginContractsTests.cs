using AITaskAgent.Core.Abstractions;
using Microsoft.Extensions.Configuration;

namespace AITaskAgent.Tests;

/// <summary>The plugin contracts of the framework: <see cref="IPluginConfiguration"/> defaults and <see cref="PluginConfigurationRules"/>.</summary>
public sealed class PluginContractsTests
{
    public sealed class Settings
    {
        public string? Suffix { get; set; }
    }

    private sealed class OnlyOptions : IPluginConfiguration
    {
        public Type? OptionsType => typeof(Settings);
    }

    [Fact]
    public void A_configuration_with_only_an_options_type_gets_its_schema_no_defaults_and_no_root_sections()
    {
        IPluginConfiguration configuration = new OnlyOptions();
        PluginContext context = new("Acme", "1.0.0", "C:/plugins/acme", new ConfigurationBuilder().Build());

        Assert.Contains(configuration.Schema()!.Properties.Keys, k => k.Equals("suffix", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(configuration.Defaults(context));
        Assert.Empty(configuration.RootSections);
    }

    [Theory]
    [InlineData("CodebaseAnalysis", null)]
    [InlineData("AITaskAgent", "reserved")]
    [InlineData("aitaskagent", "reserved")]
    [InlineData("AITaskAgent:Plugins", "top-level")]
    [InlineData(" ", "empty")]
    public void A_root_section_is_a_top_level_name_and_never_the_frameworks(string section, string? problem)
    {
        string? found = PluginConfigurationRules.Problem(section);

        if (problem == null)
        {
            Assert.Null(found);
        }
        else
        {
            Assert.Contains(problem, found, StringComparison.Ordinal);
        }
    }
}
