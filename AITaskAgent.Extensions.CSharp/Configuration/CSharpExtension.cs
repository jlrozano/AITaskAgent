using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Hooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITaskAgent.CSharp;

/// <summary>The C# extension: registers <see cref="CSharpStep"/> (tag <c>CSharpStep</c>) in the step registry.</summary>
public sealed class CSharpExtension : IExtension
{
    /// <inheritdoc/>
    public string Name => "CSharp";

    /// <inheritdoc/>
    public void Register(IExtensionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddStep<CSharpStep>();
    }

    /// <inheritdoc/>
    public ExtensionInfo Info() => new() { Entries = [ExtensionDiscovery.StepEntry(typeof(CSharpStep))] };
}

/// <summary>DI registration of the C# extension and of a compiled unit.</summary>
public static class CSharpServiceCollectionExtensions
{
    /// <summary>Adds <see cref="CSharpExtension"/> (idempotent).</summary>
    public static IServiceCollection AddCSharpExtension(this IServiceCollection services)
        => services.AddExtension(new CSharpExtension());

    /// <summary>
    /// Registers <paramref name="unit"/> for the <c>CSharpStep</c>s of this collection and puts its hooks and validators in the
    /// engine's <see cref="IHookRegistry"/> (source <c>unit:&lt;package&gt;</c>). The registry is decorated, not replaced: what a
    /// host registered keeps working, and a unit name that is also registered elsewhere is the usual «defined more than once»
    /// error when the binder looks it up. Call it after the services that register the registry (<c>AddAITaskAgentYaml</c>
    /// and the host's own). The collection disposes the unit with its provider.
    /// </summary>
    public static IServiceCollection AddCSharpUnit(this IServiceCollection services, CSharpUnit unit, string packageName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(unit);

        services.RemoveAll<ICSharpUnit>();
        services.AddSingleton<ICSharpUnit>(_ => unit);

        if (unit.Hooks.Count > 0)
        {
            ServiceDescriptor? previous = services.LastOrDefault(d => d.ServiceType == typeof(IHookRegistry) && !d.IsKeyedService);
            if (previous != null)
            {
                services.Remove(previous);
            }

            string source = "unit:" + packageName;
            services.AddSingleton<IHookRegistry>(sp => new UnitHookRegistry(unit, source, previous == null ? null : Resolve(previous, sp)));
        }

        return services;
    }

    private static IHookRegistry? Resolve(ServiceDescriptor descriptor, IServiceProvider services)
    {
        if (descriptor.ImplementationInstance is IHookRegistry instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is { } factory)
        {
            return (IHookRegistry)factory(services);
        }

        return descriptor.ImplementationType is { } type ? (IHookRegistry)ActivatorUtilities.CreateInstance(services, type) : null;
    }
}

/// <summary>The hooks of a unit on top of the registry a host already had (a name in both is an error, never a silent shadowing).</summary>
internal sealed class UnitHookRegistry : IHookRegistry
{
    private readonly HookRegistry _own = new();
    private readonly IHookRegistry? _inner;

    public UnitHookRegistry(CSharpUnit unit, string source, IHookRegistry? inner)
    {
        _inner = inner;
        HashSet<string> taken = new(inner?.List().Select(h => h.Name) ?? [], StringComparer.Ordinal);
        foreach (UnitHook hook in unit.Hooks)
        {
            if (taken.Contains(hook.Name))
            {
                throw new InvalidOperationException(
                    $"Hook name '{hook.Name}' is defined more than once: the C# unit ('{source}') and another source both register it.");
            }

            _own.Add(hook.Name, hook.Kind, hook.Delegate, hook.Description, source);
        }
    }

    public bool TryGet(string name, HookKind kind, Type delegateType, out Delegate? hook)
        => _own.TryGet(name, kind, delegateType, out hook) || (_inner?.TryGet(name, kind, delegateType, out hook) ?? false);

    public IReadOnlyCollection<HookInfo> List()
        => [.. _own.List().Concat(_inner?.List() ?? []).OrderBy(h => h.Name, StringComparer.Ordinal)];
}
