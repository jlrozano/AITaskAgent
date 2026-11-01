using AITaskAgent.Core.Hooks;
using System.Text.Json.Serialization;

namespace AITaskAgent.NugetLoader;

/// <summary>A step a plugin declares. <see cref="Tagged"/> false = no <c>[StepTag]</c>: <see cref="Tag"/> is <see cref="TypeName"/>.</summary>
public sealed record StepContribution(
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("type")] string TypeName,
    [property: JsonPropertyName("tagged")] bool Tagged)
{
    [JsonIgnore] internal MetadataRef Ref { get; init; }
}

/// <summary>A hook, validator or result validator a plugin declares (a <c>public static</c> method with the attribute).</summary>
public sealed record HookContribution(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind"), JsonConverter(typeof(JsonStringEnumConverter<HookKind>))] HookKind Kind,
    [property: JsonPropertyName("type")] string TypeName,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("description")] string? Description)
{
    [JsonIgnore] internal MetadataRef Ref { get; init; }
}

/// <summary>A class of a plugin marked <c>[ResponseType]</c>.</summary>
public sealed record ResponseTypeContribution(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string TypeName)
{
    [JsonIgnore] internal MetadataRef Ref { get; init; }
}

/// <summary>A class of a plugin that implements one of the plugin contracts (tool, tool source, template source, configuration).</summary>
public sealed record TypeContribution([property: JsonPropertyName("type")] string TypeName)
{
    [JsonIgnore] internal MetadataRef Ref { get; init; }
}

/// <summary>Where a contribution is in the plugin's files: the assembly file and the metadata token (the same in the loaded assembly).</summary>
internal readonly record struct MetadataRef(string File, int Token);

/// <summary>
/// What a plugin brings, read from the metadata of its own assemblies by <see cref="PluginScanner"/> without loading any of them (so it can be
/// shown before the plugin is approved).
/// </summary>
public sealed record PluginContributions
{
    /// <summary>The plugin's own assemblies that were scanned (file names).</summary>
    [JsonPropertyName("assemblies")] public IReadOnlyList<string> Assemblies { get; init; } = [];

    /// <summary>Public, concrete, non-generic <c>IStep</c> classes.</summary>
    [JsonPropertyName("steps")] public IReadOnlyList<StepContribution> Steps { get; init; } = [];

    /// <summary>Public, concrete <c>ITool</c> classes with a public parameterless constructor.</summary>
    [JsonPropertyName("tools")] public IReadOnlyList<TypeContribution> Tools { get; init; } = [];

    /// <summary><c>IToolSource</c> classes.</summary>
    [JsonPropertyName("toolSources")] public IReadOnlyList<TypeContribution> ToolSources { get; init; } = [];

    /// <summary>Hooks, validators and result validators.</summary>
    [JsonPropertyName("hooks")] public IReadOnlyList<HookContribution> Hooks { get; init; } = [];

    /// <summary><c>[ResponseType]</c> classes.</summary>
    [JsonPropertyName("responseTypes")] public IReadOnlyList<ResponseTypeContribution> ResponseTypes { get; init; } = [];

    /// <summary><c>ITemplateSource</c> classes.</summary>
    [JsonPropertyName("templateSources")] public IReadOnlyList<TypeContribution> TemplateSources { get; init; } = [];

    /// <summary>The <c>IPluginConfiguration</c> class (at most one).</summary>
    [JsonPropertyName("configuration")] public TypeContribution? Configuration { get; init; }

    /// <summary>Every public concrete <c>IExtension</c> with a public parameterless constructor in the plugin's own assemblies.</summary>
    [JsonPropertyName("extensions")] public IReadOnlyList<string> Extensions { get; init; } = [];

    /// <summary>
    /// The plugin's entry point: its only <c>IExtension</c>, or the one <c>plugin.json</c> names. Null = no extension (the plugin loads by the
    /// discovery above) or the plugin cannot be driven (<c>multiple-extensions</c>, <c>extension-not-found</c>, <c>invalid-extension</c>).
    /// </summary>
    [JsonPropertyName("extension")] public TypeContribution? Extension { get; init; }

    /// <summary>Step types of the plugin that are not registered (abstract, generic, not public).</summary>
    [JsonIgnore] public IReadOnlyList<SkippedStep> Skipped { get; init; } = [];
}
