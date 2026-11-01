using AITaskAgent.Core.Abstractions;
using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.Support.Template;
using System.Reflection;

namespace AITaskAgent.LLM.Configuration;

/// <summary>
/// Typed forms of the tool and template registrations of <see cref="IExtensionBuilder"/> (whose own members take types and objects because
/// <c>AITaskAgent.Abstractions</c> knows neither <see cref="ITool"/> nor <see cref="ITemplateSource"/>), and the discovery of an assembly's
/// contributions an <see cref="IExtension"/> can use to register by scan and to declare by <see cref="IExtension.Info"/>.
/// </summary>
public static class ExtensionBuilderToolExtensions
{
    /// <summary>Registers a tool instance (shared by every engine the host builds).</summary>
    public static IExtensionBuilder AddTool(this IExtensionBuilder builder, ITool tool)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tool);
        return builder.AddToolInstance(tool);
    }

    /// <summary>Registers a tool class with a public parameterless constructor; the host creates one per engine.</summary>
    public static IExtensionBuilder AddTool<TTool>(this IExtensionBuilder builder) where TTool : ITool, new()
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddToolType(typeof(TTool));
    }

    /// <summary>Registers a tool source; the host calls its <see cref="IToolSource.Create"/> once per engine.</summary>
    public static IExtensionBuilder AddToolSource(this IExtensionBuilder builder, IToolSource source)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        return builder.AddToolSourceInstance(source);
    }

    /// <summary>Registers a template source; its provider is chained after the templates the host already has.</summary>
    public static IExtensionBuilder AddTemplates(this IExtensionBuilder builder, ITemplateSource source)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        return builder.AddTemplateSourceInstance(source);
    }

    /// <summary>
    /// Registers every public concrete <see cref="ITool"/> with a public parameterless constructor of <paramref name="assembly"/> (as classes,
    /// created per engine) and every public <see cref="IToolSource"/> with one (created now, once).
    /// </summary>
    public static IExtensionBuilder AddToolsFromAssembly(this IExtensionBuilder builder, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (Type type in AssemblyDiscovery.Creatable<ITool>(assembly))
        {
            builder.AddToolType(type);
        }

        foreach (Type type in AssemblyDiscovery.Creatable<IToolSource>(assembly))
        {
            builder.AddToolSourceInstance(Activator.CreateInstance(type)!);
        }

        return builder;
    }

    /// <summary>Registers every public <see cref="ITemplateSource"/> with a public parameterless constructor of <paramref name="assembly"/> (created now, once).</summary>
    public static IExtensionBuilder AddTemplatesFromAssembly(this IExtensionBuilder builder, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (Type type in AssemblyDiscovery.Creatable<ITemplateSource>(assembly))
        {
            builder.AddTemplateSourceInstance(Activator.CreateInstance(type)!);
        }

        return builder;
    }

    /// <summary>
    /// Registers everything <see cref="AssemblyDiscovery.Describe"/> reports for <paramref name="assembly"/>: tagged steps, tools, tool
    /// sources, template sources, hooks and validators, response types. The usual body of an extension that registers by scan.
    /// </summary>
    public static IExtensionBuilder AddAllFromAssembly(this IExtensionBuilder builder, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .AddStepsFromAssembly(assembly)
            .AddToolsFromAssembly(assembly)
            .AddTemplatesFromAssembly(assembly)
            .AddHooksFromAssembly(assembly)
            .AddResponseTypesFromAssembly(assembly);
    }
}

/// <summary>
/// Reflection over an assembly for the contributions of an <see cref="IExtension"/>: the same rules as <see cref="ExtensionDiscovery"/> plus
/// the types only this project can name (tools, tool sources, template sources). An extension returns <see cref="Describe"/> from
/// <see cref="IExtension.Info"/> when it registers with <see cref="ExtensionBuilderToolExtensions.AddAllFromAssembly"/>.
/// </summary>
public static class AssemblyDiscovery
{
    /// <summary>The public, concrete, non-generic classes of <paramref name="assembly"/> assignable to <typeparamref name="T"/> with a public parameterless constructor.</summary>
    public static IReadOnlyList<Type> Creatable<T>(Assembly assembly)
        => [.. ExtensionDiscovery.PublicTypes(assembly)
            .Where(t => t.IsClass && !t.IsAbstract && !t.ContainsGenericParameters && typeof(T).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null)];

    /// <summary>What <see cref="ExtensionBuilderToolExtensions.AddAllFromAssembly"/> registers for <paramref name="assembly"/>, as a declaration.</summary>
    public static ExtensionInfo Describe(Assembly assembly)
        => ExtensionDiscovery.Describe(assembly).With(
        [
            .. Creatable<ITool>(assembly).Select(t => new ExtensionEntry(ExtensionKinds.Tool, string.Empty, t.FullName, ExtensionDiscovery.DescriptionOf(t))),
            .. Creatable<IToolSource>(assembly).Select(t => new ExtensionEntry(ExtensionKinds.ToolSource, string.Empty, t.FullName, ExtensionDiscovery.DescriptionOf(t))),
            .. Creatable<ITemplateSource>(assembly).Select(t => new ExtensionEntry(ExtensionKinds.TemplateSource, string.Empty, t.FullName, ExtensionDiscovery.DescriptionOf(t))),
        ]);
}
