using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace AITaskAgent.Core.Abstractions;

/// <summary>
/// An <see cref="IExtensionBuilder"/> that registers nothing anywhere: it keeps what <see cref="IExtension.Register"/> asked for so a host
/// can inspect it, compare it with <see cref="IExtension.Info"/> (<see cref="ActualInfo"/>) and wire it itself. Services go to
/// <see cref="Services"/>, a collection of its own; steps to <see cref="CapturedSteps"/> (reads go to the reference registry, when given).
/// </summary>
public sealed class ExtensionCapture : IExtensionBuilder
{
    private readonly List<(string Tag, Type Type)> _steps = [];
    private readonly List<DiscoveredHook> _hooks = [];
    private readonly List<(string Name, Type Type)> _responseTypes = [];
    private readonly List<Type> _toolTypes = [];
    private readonly List<object> _toolInstances = [];
    private readonly List<object> _toolSources = [];
    private readonly List<object> _templateSources = [];
    private readonly Registry _registry;

    /// <summary>Creates a capture.</summary>
    /// <param name="reference">The host's step registry, for the reads an extension may do during <see cref="IExtension.Register"/> (a tag already taken); null = none.</param>
    /// <param name="plugin">The identity <see cref="Plugin"/> reports; null for none.</param>
    public ExtensionCapture(IStepRegistry? reference = null, PluginContext? plugin = null)
    {
        _registry = new Registry(this, reference);
        Plugin = plugin;
    }

    /// <inheritdoc/>
    public IServiceCollection Services { get; } = new ServiceCollection();

    /// <inheritdoc/>
    public IStepRegistry Steps => _registry;

    /// <inheritdoc/>
    public PluginContext? Plugin { get; }

    /// <summary>The step types registered, with their tag (<c>[StepTag]</c>, else the full class name).</summary>
    public IReadOnlyList<(string Tag, Type Type)> CapturedSteps => _steps;

    /// <summary>The hooks, validators and result validators registered.</summary>
    public IReadOnlyList<DiscoveredHook> Hooks => _hooks;

    /// <summary>The response types registered.</summary>
    public IReadOnlyList<(string Name, Type Type)> ResponseTypes => _responseTypes;

    /// <summary>The tool classes registered (<see cref="IExtensionBuilder.AddToolType"/>).</summary>
    public IReadOnlyList<Type> ToolTypes => _toolTypes;

    /// <summary>The tool instances registered.</summary>
    public IReadOnlyList<object> ToolInstances => _toolInstances;

    /// <summary>The tool sources registered.</summary>
    public IReadOnlyList<object> ToolSources => _toolSources;

    /// <summary>The template sources registered.</summary>
    public IReadOnlyList<object> TemplateSources => _templateSources;

    /// <inheritdoc/>
    public IExtensionBuilder AddStep(Type stepType, string? tag = null)
    {
        _registry.Add(stepType, tag);
        return this;
    }

    /// <inheritdoc/>
    public IExtensionBuilder AddStep<TStep>(string? tag = null) where TStep : IStep => AddStep(typeof(TStep), tag);

    /// <inheritdoc/>
    public IExtensionBuilder AddStepsFromAssembly(Assembly assembly)
    {
        _registry.AddFromAssembly(assembly);
        return this;
    }

    /// <inheritdoc/>
    public IExtensionBuilder AddHook(string name, HookKind kind, MethodInfo method, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(method);
        _hooks.Add(new DiscoveredHook(name.Trim(), kind, method, description));
        return this;
    }

    /// <inheritdoc/>
    public IExtensionBuilder AddResponseType(string name, Type type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(type);
        _responseTypes.Add((name.Trim(), type));
        return this;
    }

    /// <inheritdoc/>
    public IExtensionBuilder AddToolType(Type toolType)
    {
        ArgumentNullException.ThrowIfNull(toolType);
        _toolTypes.Add(toolType);
        return this;
    }

    /// <inheritdoc/>
    public IExtensionBuilder AddToolInstance(object tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _toolInstances.Add(tool);
        return this;
    }

    /// <inheritdoc/>
    public IExtensionBuilder AddToolSourceInstance(object toolSource)
    {
        ArgumentNullException.ThrowIfNull(toolSource);
        _toolSources.Add(toolSource);
        return this;
    }

    /// <inheritdoc/>
    public IExtensionBuilder AddTemplateSourceInstance(object templateSource)
    {
        ArgumentNullException.ThrowIfNull(templateSource);
        _templateSources.Add(templateSource);
        return this;
    }

    /// <summary>
    /// What was really registered (plus the configuration the extension itself reports), in the shape of <see cref="IExtension.Info"/>, for
    /// <see cref="ExtensionInfo.Difference"/>. A tool instance reports its <c>Name</c> property when it has one.
    /// </summary>
    public ExtensionInfo ActualInfo(IExtension? extension = null)
    {
        List<ExtensionEntry> entries = [];
        entries.AddRange(_steps.Select(s => new ExtensionEntry(ExtensionKinds.Step, s.Tag, s.Type.FullName, ExtensionDiscovery.DescriptionOf(s.Type))));
        entries.AddRange(_toolTypes.Select(t => new ExtensionEntry(ExtensionKinds.Tool, string.Empty, t.FullName)));
        entries.AddRange(_toolInstances.Select(t => new ExtensionEntry(ExtensionKinds.Tool, NameOf(t), t.GetType().FullName)));
        entries.AddRange(_toolSources.Select(t => new ExtensionEntry(ExtensionKinds.ToolSource, string.Empty, t.GetType().FullName)));
        entries.AddRange(_hooks.Select(ExtensionDiscovery.EntryOf));
        entries.AddRange(_responseTypes.Select(r => new ExtensionEntry(ExtensionKinds.ResponseType, r.Name, r.Type.FullName, ExtensionDiscovery.DescriptionOf(r.Type))));
        entries.AddRange(_templateSources.Select(t => new ExtensionEntry(ExtensionKinds.TemplateSource, string.Empty, t.GetType().FullName)));
        entries.AddRange(Services.Select(d => d.IsKeyedService ? null : d.ImplementationInstance).OfType<RegisteredScriptObject>()
            .Select(o => new ExtensionEntry(ExtensionKinds.ScriptObject, o.Name, o.Type.FullName, o.Description)));
        if (extension?.ConfigurationType is { } configuration)
        {
            entries.Add(new ExtensionEntry(ExtensionKinds.Configuration, string.Empty, configuration.FullName));
        }

        foreach (string section in extension?.RootSections ?? [])
        {
            entries.Add(new ExtensionEntry(ExtensionKinds.RootSection, section));
        }

        return new ExtensionInfo { Entries = entries };
    }

    private static string NameOf(object tool)
    {
        try
        {
            return tool.GetType().GetProperty("Name")?.GetValue(tool) as string ?? string.Empty;
        }
        catch (Exception ex) when (ex is TargetInvocationException or AmbiguousMatchException)
        {
            return string.Empty;
        }
    }

    private sealed class Registry(ExtensionCapture owner, IStepRegistry? reference) : IStepRegistry
    {
        public void Add(Type stepType, string? tag = null)
        {
            ArgumentNullException.ThrowIfNull(stepType);
            string resolved = tag ?? stepType.GetCustomAttribute<StepTagAttribute>(inherit: false)?.TagName ?? stepType.FullName ?? stepType.Name;
            if (owner._steps.Any(s => s.Tag == resolved))
            {
                throw new InvalidOperationException($"Step tag '{resolved}' is already registered by this extension.");
            }

            owner._steps.Add((resolved, stepType));
        }

        public int AddFromAssembly(Assembly assembly)
        {
            IReadOnlyList<(string Tag, Type Type)> found = ExtensionDiscovery.TaggedSteps(assembly);
            foreach ((string tag, Type type) in found)
            {
                Add(type, tag);
            }

            return found.Count;
        }

        public bool TryGetType(string tag, [NotNullWhen(true)] out Type? stepType)
        {
            if (owner._steps.FirstOrDefault(s => s.Tag == tag) is { Type: { } own })
            {
                stepType = own;
                return true;
            }

            stepType = null;
            return reference?.TryGetType(tag, out stepType) ?? false;
        }

        public IReadOnlyDictionary<string, Type> GetRegisteredTypes()
        {
            Dictionary<string, Type> all = new(reference?.GetRegisteredTypes() ?? new Dictionary<string, Type>(), StringComparer.Ordinal);
            foreach ((string tag, Type type) in owner._steps)
            {
                all[tag] = type;
            }

            return all;
        }

        public IStep Create(string tag, JObject? config = null, IServiceProvider? services = null)
            => reference?.Create(tag, config, services) ?? throw new NotSupportedException("A capturing registry creates no steps.");

        public StepDescription Describe(string tag)
            => reference?.Describe(tag) ?? throw new NotSupportedException("A capturing registry describes no steps.");

        public StepDescription Describe(Type stepType)
            => reference?.Describe(stepType) ?? throw new NotSupportedException("A capturing registry describes no steps.");
    }
}
