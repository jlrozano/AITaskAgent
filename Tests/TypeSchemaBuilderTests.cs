using AITaskAgent.Core.Abstractions;
using AITaskAgent.LLM.Configuration;
using Newtonsoft.Json;
using NJsonSchema;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace AITaskAgent.Tests;

/// <summary>
/// <see cref="TypeSchemaBuilder"/> in its configuration flavour (what
/// <c>IExtension.ConfigurationModel</c> returns by default) and the extensions that declare a
/// <c>ConfigurationType</c>.
/// </summary>
public sealed class TypeSchemaBuilderTests
{
    public enum Level { Low, High }

    public sealed class Child
    {
        public string Name { get; init; } = "";

        public Child? Next { get; init; }
    }

    public sealed class Options
    {
        [Description("Root folder.")]
        public string Root { get; init; } = "";

        public required int Port { get; init; }

        [Required]
        public string? Token { get; set; }

        public Level Level { get; set; }

        public List<string> Tags { get; init; } = [];

        public List<Child> Children { get; init; } = [];

        public Dictionary<string, Child> ByName { get; init; } = [];

        public Child Single { get; init; } = new();

        public IServiceProvider? Services { get; init; }

        [JsonIgnore]
        public string Ignored { get; init; } = "";

        public string ReadOnly => "fixed";
    }

    private sealed class OptionsExtension : IExtension
    {
        public string Name => "Tests.Options";

        public Type? ConfigurationType => typeof(Options);

        public void Register(IExtensionBuilder builder)
        {
        }
    }

    private sealed class PlainExtension : IExtension
    {
        public string Name => "Tests.Plain";

        public void Register(IExtensionBuilder builder)
        {
        }
    }

    [Fact]
    public void Init_and_set_properties_are_described_and_ignored_or_read_only_ones_are_not()
    {
        JsonSchema schema = TypeSchemaBuilder.Build(typeof(Options), configuration: true);

        Assert.Equal(JsonObjectType.String, schema.Properties["root"].Type);
        Assert.Equal("Root folder.", schema.Properties["root"].Description);
        Assert.Equal(JsonObjectType.Integer, schema.Properties["port"].Type);
        Assert.Equal(JsonObjectType.String, schema.Properties["level"].Type);
        Assert.Equal(["Low", "High"], schema.Properties["level"].Enumeration.Select(v => v!.ToString()));
        Assert.DoesNotContain("ignored", schema.Properties.Keys);
        Assert.DoesNotContain("readOnly", schema.Properties.Keys);
    }

    [Fact]
    public void Required_is_only_Required_attribute_or_required_member()
    {
        JsonSchema schema = TypeSchemaBuilder.Build(typeof(Options), configuration: true);

        Assert.Equal(["port", "token"], schema.RequiredProperties.Order());
    }

    [Fact]
    public void Nested_classes_lists_and_string_keyed_dictionaries_are_described()
    {
        JsonSchema schema = TypeSchemaBuilder.Build(typeof(Options), configuration: true);

        Assert.Equal(JsonObjectType.Array, schema.Properties["tags"].Type);
        Assert.Equal(JsonObjectType.String, schema.Properties["tags"].Items.Single().Type);
        Assert.Equal(JsonObjectType.Object, schema.Properties["children"].Items.Single().Type);
        Assert.Contains("name", schema.Properties["children"].Items.Single().Properties.Keys);
        Assert.Equal(JsonObjectType.Object, schema.Properties["byName"].Type);
        Assert.Contains("name", schema.Properties["byName"].AdditionalPropertiesSchema!.Properties.Keys);
        Assert.Contains("name", schema.Properties["single"].Properties.Keys);
    }

    [Fact]
    public void A_class_that_contains_itself_is_described_once_and_framework_types_stay_untyped()
    {
        JsonSchema schema = TypeSchemaBuilder.Build(typeof(Options), configuration: true);

        JsonSchemaProperty next = schema.Properties["single"].Properties["next"];
        Assert.Equal(JsonObjectType.None, next.Type);
        Assert.Empty(next.Properties);
        Assert.Equal(JsonObjectType.None, schema.Properties["services"].Type);
    }

    [Fact]
    public void ConfigurationModel_defaults_to_the_schema_of_ConfigurationType_and_to_null_without_one()
    {
        IExtension withOptions = new OptionsExtension();
        IExtension plain = new PlainExtension();

        Assert.Contains("port", withOptions.ConfigurationModel()!.Properties.Keys);
        Assert.Null(plain.ConfigurationModel());
    }

    [Fact]
    public void LlmExtension_describes_providers_roles_and_default_role()
    {
        IExtension extension = new LlmExtension();

        JsonSchema schema = extension.ConfigurationModel()!;

        JsonSchema provider = schema.Properties["providers"].AdditionalPropertiesSchema!;
        Assert.Contains("baseUrl", provider.Properties.Keys);
        Assert.Contains("apiKey", provider.Properties.Keys);
        Assert.Equal(JsonObjectType.Array, provider.Properties["models"].Type);
        Assert.Contains("name", provider.Properties["models"].Items.Single().Properties.Keys);

        JsonSchema role = schema.Properties["roles"].AdditionalPropertiesSchema!;
        Assert.Contains("provider", role.Properties.Keys);
        Assert.Contains("systemPrompt", role.Properties.Keys);
        Assert.Contains("inference", role.Properties.Keys);
        Assert.Equal(JsonObjectType.String, schema.Properties["defaultRole"].Type);
    }

    public sealed class SecretOptions
    {
        [PasswordPropertyText(true)]
        public string? ApiKey { get; set; }

        [DataType(DataType.Password)]
        public string? Pat { get; set; }

        public string? TokenEndpoint { get; set; }

        public int MaxTokens { get; set; }
    }

    [Fact]
    public void A_setting_declared_as_a_password_has_format_password_and_a_name_alone_never_does()
    {
        JsonSchema schema = TypeSchemaBuilder.Build(typeof(SecretOptions), configuration: true);

        Assert.Equal("password", schema.Properties["apiKey"].Format);
        Assert.Equal("password", schema.Properties["pat"].Format);
        Assert.Null(schema.Properties["tokenEndpoint"].Format);
        Assert.Null(schema.Properties["maxTokens"].Format);
        // The flat flavour (step configuration) does not mark it.
        Assert.Null(TypeSchemaBuilder.Build(typeof(SecretOptions)).Properties["apiKey"].Format);
    }
}
