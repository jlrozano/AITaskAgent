using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Script.Hosting;
using AITaskAgent.Script.Validators;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITaskAgent.Script.Configuration;

/// <summary>DI registration of the JavaScript extension.</summary>
public static class ScriptServiceCollectionExtensions
{
    /// <summary>Adds <see cref="ScriptExtension"/> (idempotent). Sugar for <c>services.AddExtension(new ScriptExtension())</c>.</summary>
    public static IServiceCollection AddScriptExtension(this IServiceCollection services)
        => services.AddExtension(new ScriptExtension());

    /// <summary>Registers a library steps can declare in <c>uses</c>. Add it before the service provider is built.</summary>
    public static IServiceCollection AddScriptLibrary(this IServiceCollection services, IScriptLibrary library)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(library);
        return services.AddSingleton(library);
    }

    /// <summary>Registers a host function scripts can call once the step lists it in <c>allow</c> and the host grants it.</summary>
    public static IServiceCollection AddScriptHostFunction(this IServiceCollection services, IScriptHostFunction function)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(function);
        return services.AddSingleton(function);
    }

    /// <summary>Sets what scripts may do on this host (default deny); see <see cref="ScriptHostOptions"/>.</summary>
    public static IServiceCollection ConfigureScriptHost(this IServiceCollection services, ScriptHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        return services.AddSingleton(options);
    }

    /// <summary>Sets the limits of every JavaScript validator call (replaces the defaults); see <see cref="JsValidatorOptions"/>.</summary>
    public static IServiceCollection ConfigureJsValidators(this IServiceCollection services, JsValidatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Problem() is { } problem)
        {
            throw new ArgumentException(problem, nameof(options));
        }

        services.RemoveAll<JsValidatorOptions>();
        return services.AddSingleton(options);
    }

    /// <summary>
    /// Offers <typeparamref name="T"/> to scripts as <c>host.&lt;name&gt;.&lt;method&gt;(args)</c> (architecture §18.7): each public instance
    /// method (see <see cref="ScriptObjects"/>) in camelCase. Registering it is the grant; a ScriptStep still lists the methods it uses in
    /// <c>allow</c>, a JavaScript validator may call them all.
    /// </summary>
    public static IServiceCollection AddScriptObject<T>(this IServiceCollection services, string name, Func<IServiceProvider, T>? factory = null, string? description = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!RegisteredScriptObject.IsValidName(name?.Trim()))
        {
            throw new ArgumentException($"'{name}' is not a valid script object name (a letter or '_', then letters, digits or '_').", nameof(name));
        }

        return services.AddSingleton(new RegisteredScriptObject(name!.Trim(), typeof(T), factory == null ? null : sp => factory(sp), description));
    }
}

/// <summary>The typed forms of the script members of <see cref="IExtensionBuilder"/> (the builder itself cannot see this extension's types).</summary>
public static class ScriptExtensionBuilderExtensions
{
    /// <summary>
    /// Offers <typeparamref name="T"/> to scripts as <c>host.&lt;name&gt;.&lt;method&gt;(args)</c>; see
    /// <see cref="IExtensionBuilder.AddScriptObject"/> and <see cref="ScriptObjects"/>.
    /// </summary>
    public static IExtensionBuilder AddScriptObject<T>(this IExtensionBuilder builder, string name, Func<IServiceProvider, T>? factory = null, string? description = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddScriptObject(name, typeof(T), factory == null ? null : sp => factory(sp), description);
    }
}
