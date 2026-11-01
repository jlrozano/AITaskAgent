using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace AITaskAgent.NugetLoader;

/// <summary>
/// The configuration a plugin's <c>Register</c> sees in <c>IExtensionBuilder.Plugin.Section</c> while the loader runs it: there is none, because
/// the host composes the plugin's real section later, per engine. Everything is empty and read-only.
/// </summary>
internal sealed class EmptyConfiguration : IConfiguration, IConfigurationSection
{
    public static EmptyConfiguration Instance { get; } = new();

    public string? this[string key]
    {
        get => null;
        set => throw new NotSupportedException("The configuration of a plugin is read-only while it registers.");
    }

    public string Key => string.Empty;

    public string Path => string.Empty;

    public string? Value
    {
        get => null;
        set => throw new NotSupportedException("The configuration of a plugin is read-only while it registers.");
    }

    public IEnumerable<IConfigurationSection> GetChildren() => [];

    public IChangeToken GetReloadToken() => new CancellationChangeToken(CancellationToken.None);

    public IConfigurationSection GetSection(string key) => this;
}
