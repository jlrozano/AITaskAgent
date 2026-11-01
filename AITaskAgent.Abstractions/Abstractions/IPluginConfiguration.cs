using Microsoft.Extensions.Configuration;
using NJsonSchema;

namespace AITaskAgent.Core.Abstractions;

/// <summary>
/// What a plugin is given when the host builds its contributions (tool sources, template sources, configuration defaults): who it is,
/// where its files are and its own configuration section.
/// </summary>
/// <param name="Id">The plugin's id (NuGet id or folder plugin id).</param>
/// <param name="Version">The exact version loaded.</param>
/// <param name="Folder">The plugin's folder: the folder plugin itself, or the extracted <c>lib</c> folder of a NuGet plugin.</param>
/// <param name="Section">The plugin's configuration (<c>AITaskAgent:Plugins:&lt;id&gt;</c> in the designer); empty when it has none.</param>
public sealed record PluginContext(string Id, string Version, string Folder, IConfiguration Section);

/// <summary>
/// The configuration a plugin declares (at most one class per plugin implements it; it needs a public parameterless constructor). The host
/// composes the configuration: the plugin only describes it, it decorates nothing.
/// </summary>
public interface IPluginConfiguration
{
    /// <summary>The class whose public properties are the plugin's settings (its options class), or null.</summary>
    Type? OptionsType { get; }

    /// <summary>JSON Schema of the settings: by default the one <see cref="TypeSchemaBuilder"/> extracts from <see cref="OptionsType"/>.</summary>
    JsonSchema? Schema() => OptionsType is null ? null : TypeSchemaBuilder.Build(OptionsType, configuration: true);

    /// <summary>
    /// Default values, as flat configuration keys relative to the plugin's section (<c>"Suffix"</c>, <c>"Limits:Max"</c>) or, for a key of
    /// one of <see cref="RootSections"/>, relative to the configuration root (<c>"DotNetEnricher:CliPath"</c>). The host's values win.
    /// </summary>
    IReadOnlyDictionary<string, string?> Defaults(PluginContext context) => new Dictionary<string, string?>();

    /// <summary>
    /// Top-level sections of the plugin's settings that the host lifts to the root of <c>IConfiguration</c> (for libraries that read their
    /// own section by a fixed name). Never <c>AITaskAgent</c> or anything under it: see <see cref="PluginConfigurationRules"/>.
    /// </summary>
    IReadOnlyList<string> RootSections => [];
}

/// <summary>Checks a host applies to an <see cref="IPluginConfiguration"/>.</summary>
public static class PluginConfigurationRules
{
    /// <summary>What is wrong with <paramref name="rootSection"/> as a root section; null when it is valid.</summary>
    public static string? Problem(string? rootSection)
    {
        if (string.IsNullOrWhiteSpace(rootSection))
        {
            return "A root section name cannot be empty.";
        }

        string name = rootSection.Trim();
        if (name.Contains(':', StringComparison.Ordinal))
        {
            return $"Root section '{name}' must be a top-level name (no ':').";
        }

        return string.Equals(name, "AITaskAgent", StringComparison.OrdinalIgnoreCase)
            ? $"Root section '{name}' is reserved: a plugin cannot lift settings into the framework's section."
            : null;
    }
}
