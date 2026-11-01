using AITaskAgent.Core.Hooks;
using Microsoft.Extensions.DependencyInjection;
using NJsonSchema;
using System.Reflection;

namespace AITaskAgent.Core.Abstractions;

/// <summary>
/// A unit that adds services and steps to an AITaskAgent application (LLM, file tools, MCP...).
/// Registered with <c>services.AddExtension&lt;T&gt;()</c> or <c>services.AddExtension(instance)</c>.
/// The framework loads no assemblies: an application references its extensions directly.
/// </summary>
public interface IExtension
{
    /// <summary>
    /// Unique name. Adding a second extension with the same name to a service collection is ignored.
    /// </summary>
    string Name { get; }

    /// <summary>Registers the extension's services and steps.</summary>
    void Register(IExtensionBuilder builder);

    /// <summary>
    /// The class whose public properties are the extension's configuration (an options class or the
    /// shape of its configuration section), or null if it has none.
    /// </summary>
    Type? ConfigurationType => null;

    /// <summary>
    /// JSON Schema of the extension's configuration, if it has one: by default the one
    /// <see cref="TypeSchemaBuilder"/> extracts from <see cref="ConfigurationType"/>.
    /// </summary>
    JsonSchema? ConfigurationModel()
        => ConfigurationType is null ? null : TypeSchemaBuilder.Build(ConfigurationType, configuration: true);

    /// <summary>
    /// What this extension is going to register, as a declaration: an implementation may build it by discovery (the reflection helpers of
    /// <see cref="ExtensionDiscovery"/> and of <c>AITaskAgent.Extensions.Llm</c>) or by hand. Information only: a host that loads the
    /// extension from a plugin compares it with what <see cref="Register"/> really registered and reports the difference. The default is empty.
    /// </summary>
    ExtensionInfo Info() => ExtensionInfo.Empty;

    /// <summary>
    /// Default values of the extension's configuration as flat keys relative to its section (<c>"Suffix"</c>, <c>"Limits:Max"</c>) or, for a key
    /// of one of <see cref="RootSections"/>, relative to the configuration root (<c>"DotNetEnricher:CliPath"</c>). Only a host that gives the
    /// extension a configuration section of its own (the designer's plugins) reads it; the host's own values win. Default: none.
    /// </summary>
    IReadOnlyDictionary<string, string?> ConfigurationDefaults(PluginContext context) => new Dictionary<string, string?>();

    /// <summary>
    /// Top-level configuration sections that the host lifts from the extension's section to the root of <c>IConfiguration</c> (for libraries that
    /// read a section by a fixed name). Never <c>AITaskAgent</c> or a path: see <see cref="PluginConfigurationRules"/>. Default: none.
    /// </summary>
    IReadOnlyList<string> RootSections => [];
}

/// <summary>
/// What an <see cref="IExtension"/> registers into: the service collection and the step registry
/// of that collection.
/// </summary>
public interface IExtensionBuilder
{
    /// <summary>The application's service collection.</summary>
    IServiceCollection Services { get; }

    /// <summary>The step registry of this service collection (the same singleton DI hands out).</summary>
    IStepRegistry Steps { get; }

    /// <summary>Registers a step type; see <see cref="IStepRegistry.Add(Type, string?)"/>.</summary>
    IExtensionBuilder AddStep(Type stepType, string? tag = null);

    /// <summary>Registers a step type; see <see cref="IStepRegistry.Add(Type, string?)"/>.</summary>
    IExtensionBuilder AddStep<TStep>(string? tag = null) where TStep : IStep;

    /// <summary>Registers the tagged steps of an assembly; see <see cref="IStepRegistry.AddFromAssembly"/>.</summary>
    IExtensionBuilder AddStepsFromAssembly(Assembly assembly);

    /// <summary>
    /// Who the extension is when a host gives it an identity and a folder (the designer's plugins: id, version, folder); null in a plain
    /// service collection.
    /// </summary>
    PluginContext? Plugin => null;

    /// <summary>
    /// Registers a named hook, step validator or result validator (<paramref name="method"/> is a <c>public static</c> method). Not every host
    /// can wire hooks: a builder that cannot says so with <see cref="NotSupportedException"/>.
    /// </summary>
    IExtensionBuilder AddHook(string name, HookKind kind, MethodInfo method, string? description = null)
        => throw new NotSupportedException("This builder does not register hooks.");

    /// <summary>Registers the <c>[Hook]</c>, <c>[Validator]</c> and <c>[ResultValidator]</c> methods of <paramref name="assembly"/>'s public classes (<see cref="ExtensionDiscovery.Hooks"/>).</summary>
    IExtensionBuilder AddHooksFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (DiscoveredHook hook in ExtensionDiscovery.Hooks(assembly))
        {
            AddHook(hook.Name, hook.Kind, hook.Method, hook.Description);
        }

        return this;
    }

    /// <summary>Registers a response type: its JSON schema is available under <paramref name="name"/> (<c>outputSchema</c>, <c>schema:Name</c>).</summary>
    IExtensionBuilder AddResponseType(string name, Type type)
        => throw new NotSupportedException("This builder does not register response types.");

    /// <summary>Registers the public classes of <paramref name="assembly"/> marked <c>[ResponseType]</c> (<see cref="ExtensionDiscovery.ResponseTypes"/>).</summary>
    IExtensionBuilder AddResponseTypesFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach ((string name, Type type) in ExtensionDiscovery.ResponseTypes(assembly))
        {
            AddResponseType(name, type);
        }

        return this;
    }

    /// <summary>
    /// Registers a tool class (<c>ITool</c> with a public parameterless constructor) that the host creates for each engine it builds. The typed
    /// forms (<c>AddTool&lt;T&gt;()</c>, <c>AddTool(ITool)</c>, <c>AddToolsFromAssembly</c>) are in <c>AITaskAgent.Extensions.Llm</c>, which owns <c>ITool</c>.
    /// </summary>
    IExtensionBuilder AddToolType(Type toolType)
        => throw new NotSupportedException("This builder does not register tools.");

    /// <summary>Registers one <c>ITool</c> instance (shared by every engine the host builds).</summary>
    IExtensionBuilder AddToolInstance(object tool)
        => throw new NotSupportedException("This builder does not register tools.");

    /// <summary>Registers an <c>IToolSource</c> instance: the host calls its <c>Create(PluginContext)</c> once per engine.</summary>
    IExtensionBuilder AddToolSourceInstance(object toolSource)
        => throw new NotSupportedException("This builder does not register tool sources.");

    /// <summary>Registers an <c>ITemplateSource</c> instance: its provider is chained after the templates the host already has.</summary>
    IExtensionBuilder AddTemplateSourceInstance(object templateSource)
        => throw new NotSupportedException("This builder does not register templates.");

    /// <summary>
    /// Offers a .NET object to scripts as <c>host.&lt;name&gt;.&lt;method&gt;(args)</c>: the JavaScript extension exposes each public instance method
    /// of <paramref name="type"/> (architecture §18.7). The default body adds a <see cref="RegisteredScriptObject"/> to <see cref="Services"/>,
    /// which every builder has; the typed form (<c>AddScriptObject&lt;T&gt;</c>) is in <c>AITaskAgent.Extensions.Script</c>.
    /// </summary>
    /// <param name="name">Name in scripts: a letter or <c>_</c>, then letters, digits or <c>_</c>.</param>
    /// <param name="type">The class whose public instance methods scripts may call.</param>
    /// <param name="factory">Creates the instance from the run's services; null = <c>ActivatorUtilities.CreateInstance</c>.</param>
    /// <param name="description">What it offers, for catalogs.</param>
    IExtensionBuilder AddScriptObject(string name, Type type, Func<IServiceProvider, object>? factory = null, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!RegisteredScriptObject.IsValidName(name?.Trim()))
        {
            throw new ArgumentException($"'{name}' is not a valid script object name (a letter or '_', then letters, digits or '_').", nameof(name));
        }

        Services.AddSingleton(new RegisteredScriptObject(name!.Trim(), type, factory, description));
        return this;
    }
}
