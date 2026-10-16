using CodebaseAnalysis.Core.Support;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>Las reglas de nombres de credenciales que comparten el escáner de disco, el escritor Java y el enriquecedor Roslyn.</summary>
public class CredentialNamesTests
{
    [Theory]
    [InlineData("password")]
    [InlineData("DB_PASSWORD")]
    [InlineData("passwd")]
    [InlineData("pwd")]
    [InlineData("ClientSecret")]
    [InlineData("api_key")]
    [InlineData("ApiKey")]
    [InlineData("accessToken")]
    [InlineData("SetPassword")]
    [InlineData("credentials")]
    public void Identifiers_with_a_credential_word_name_a_credential(string identifier) =>
        Assert.True(CredentialNames.NamesACredential(identifier));

    [Theory]
    [InlineData("name")]
    [InlineData("connectionString")]
    [InlineData("user")]
    [InlineData("pass")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_identifiers_do_not(string? identifier) =>
        Assert.False(CredentialNames.NamesACredential(identifier));

    [Theory]
    [InlineData("${DB_PASSWORD}", true)]
    [InlineData("{secret}", true)]
    [InlineData("<password>", true)]
    [InlineData("%PWD%", true)]
    [InlineData("********", true)]
    [InlineData("xxxxxx", true)]
    [InlineData("Ord3rs#2019", false)]
    [InlineData("hunter2", false)]
    public void Placeholders_and_already_masked_values_are_not_secrets(string value, bool placeholder) =>
        Assert.Equal(placeholder, CredentialNames.IsPlaceholder(value));

    [Theory]
    [InlineData("Ord3rs#2019", "Or*** (11 caracteres)")]
    [InlineData("ab", "ab*** (2 caracteres)")]
    [InlineData("x", "x*** (1 caracteres)")]
    public void The_mask_keeps_two_characters_and_the_length_only(string value, string expected) =>
        Assert.Equal(expected, CredentialNames.Mask(value));

    [Fact]
    public void The_disk_scanner_still_exposes_the_reference_rule_it_delegates()
    {
        Assert.True(HardcodedSecretScanner.NamesAReference("UserSecretsId"));
        Assert.False(HardcodedSecretScanner.NamesAReference("ApiKey"));
    }
}
