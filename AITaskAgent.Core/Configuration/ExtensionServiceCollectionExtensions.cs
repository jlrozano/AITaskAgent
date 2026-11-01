using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Steps;
using AITaskAgent.Support.Template;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace AITaskAgent.Configuration;

/// <summary>
/// Registration of <see cref="IExtension"/>s and of the service collection's <see cref="IStepRegistry"/>.
/// </summary>
public static class ExtensionServiceCollectionExtensions
{
    /// <summary>
    /// Adds an extension: runs its <see cref="IExtension.Register"/> against this collection and its
    /// step registry, and registers the instance as an <see cref="IExtension"/> service.
    /// An extension whose <see cref="IExtension.Name"/> is already registered is ignored.
    /// </summary>
    public static IServiceCollection AddExtension(this IServiceCollection services, IExtension extension)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(extension);

        bool alreadyAdded = services.Any(d =>
            d.ServiceType == typeof(IExtension)
            && !d.IsKeyedService
            && d.ImplementationInstance is IExtension existing
            && string.Equals(existing.Name, extension.Name, StringComparison.Ordinal));
        if (alreadyAdded)
        {
            return services;
        }

        extension.Register(new ExtensionBuilder(services, services.GetOrAddStepRegistry()));
        services.AddSingleton(extension);
        return services;
    }

    /// <summary>Adds an extension created with its parameterless constructor; see <see cref="AddExtension(IServiceCollection, IExtension)"/>.</summary>
    public static IServiceCollection AddExtension<TExtension>(this IServiceCollection services)
        where TExtension : IExtension, new()
        => services.AddExtension(new TExtension());

    /// <summary>
    /// Returns the <see cref="IStepRegistry"/> singleton of this collection, creating and registering
    /// it (with the core's tagged steps) the first time. Registration code fills it before the
    /// provider exists; DI hands out the same instance.
    /// </summary>
    public static IStepRegistry GetOrAddStepRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        ServiceDescriptor? descriptor = services.LastOrDefault(d => d.ServiceType == typeof(IStepRegistry) && !d.IsKeyedService);
        if (descriptor?.ImplementationInstance is IStepRegistry existing)
        {
            return existing;
        }

        if (descriptor != null)
        {
            throw new InvalidOperationException(
                "IStepRegistry is registered with a factory or a type; register it as an instance " +
                "(services.AddSingleton<IStepRegistry>(registry)) so extensions can fill it before the provider is built.");
        }

        StepRegistry registry = new();
        registry.AddFromAssembly(typeof(StepRegistry).Assembly);
        services.AddSingleton<IStepRegistry>(registry);
        return registry;
    }

    /// <summary>
    /// Returns the <see cref="HookRegistry"/> instance of this collection, adding one the first time (the YAML registration then keeps it).
    /// </summary>
    public static HookRegistry GetOrAddHookRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.LastOrDefault(d => d.ServiceType == typeof(HookRegistry) && !d.IsKeyedService)?.ImplementationInstance is HookRegistry existing)
        {
            return existing;
        }

        HookRegistry registry = new();
        services.AddSingleton(registry);
        services.AddSingleton<IHookRegistry>(registry);
        return registry;
    }

    private sealed class ExtensionBuilder(IServiceCollection services, IStepRegistry steps) : IExtensionBuilder
    {
        public IServiceCollection Services { get; } = services;

        public IStepRegistry Steps { get; } = steps;

        public IExtensionBuilder AddStep(Type stepType, string? tag = null)
        {
            Steps.Add(stepType, tag);
            return this;
        }

        public IExtensionBuilder AddStep<TStep>(string? tag = null) where TStep : IStep
            => AddStep(typeof(TStep), tag);

        public IExtensionBuilder AddStepsFromAssembly(Assembly assembly)
        {
            Steps.AddFromAssembly(assembly);
            return this;
        }

        public IExtensionBuilder AddHook(string name, HookKind kind, MethodInfo method, string? description = null)
        {
            ArgumentNullException.ThrowIfNull(method);
            Type[] types = [.. method.GetParameters().Select(p => p.ParameterType), method.ReturnType];
            Delegate hook = method.CreateDelegate(System.Linq.Expressions.Expression.GetDelegateType(types));
            services.GetOrAddHookRegistry().Add(name, kind, hook, description, "di");
            return this;
        }

        public IExtensionBuilder AddResponseType(string name, Type type)
        {
            services.AddSingleton(new RegisteredResponseType(name, type));
            return this;
        }

        // Tools and tool sources are Extensions.Llm contracts, which Core cannot name: they are registered under the interface found by name.
        public IExtensionBuilder AddToolType(Type toolType)
        {
            ArgumentNullException.ThrowIfNull(toolType);
            services.AddSingleton(InterfaceNamed(toolType, ToolInterface), toolType);
            return this;
        }

        public IExtensionBuilder AddToolInstance(object tool)
        {
            ArgumentNullException.ThrowIfNull(tool);
            services.AddSingleton(InterfaceNamed(tool.GetType(), ToolInterface), tool);
            return this;
        }

        public IExtensionBuilder AddToolSourceInstance(object toolSource)
        {
            ArgumentNullException.ThrowIfNull(toolSource);
            services.AddSingleton(InterfaceNamed(toolSource.GetType(), "AITaskAgent.LLM.Tools.Abstractions.IToolSource"), toolSource);
            return this;
        }

        public IExtensionBuilder AddTemplateSourceInstance(object templateSource)
        {
            ArgumentNullException.ThrowIfNull(templateSource);
            services.AddSingleton(templateSource is ITemplateSource typed
                ? typed
                : throw new ArgumentException("It does not implement ITemplateSource.", nameof(templateSource)));
            return this;
        }

        private const string ToolInterface = "AITaskAgent.LLM.Tools.Abstractions.ITool";

        private static Type InterfaceNamed(Type type, string fullName)
            => type.GetInterfaces().FirstOrDefault(i => i.FullName == fullName)
               ?? throw new ArgumentException($"'{type.FullName}' does not implement {fullName}.", nameof(type));
    }
}
