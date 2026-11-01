using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Script.Hosting;
using AITaskAgent.Script.Steps;
using AITaskAgent.Script.Validators;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITaskAgent.Script.Configuration;

/// <summary>
/// The JavaScript extension: registers <see cref="ScriptStep"/> (tag <c>ScriptStep</c>) in the step registry,
/// so a YAML pipeline can declare <c>type: ScriptStep</c>, and the built-in host functions and the
/// <see cref="IScriptCatalog"/> (nothing is granted until the host sets <see cref="ScriptHostOptions"/>), and the JavaScript validators
/// (<c>js:&lt;library&gt;.&lt;function&gt;</c> in <c>validators</c>/<c>resultValidators</c>, architecture §18.7) with their default limits.
/// The catalog's host functions include the methods of every script object registered with <c>AddScriptObject</c>.
/// </summary>
public sealed class ScriptExtension : IExtension
{
    /// <inheritdoc/>
    public string Name => "Script";

    /// <inheritdoc/>
    public void Register(IExtensionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddStep<ScriptStep>();

        IServiceCollection services = builder.Services;
        if (services.All(d => d.ServiceType != typeof(BuiltInsMarker)))
        {
            services.AddSingleton<BuiltInsMarker>();
            foreach (IScriptHostFunction function in BuiltInHostFunctions.Create())
            {
                services.AddSingleton(function);
            }
        }

        services.TryAddSingleton<IScriptCatalog>(sp => new ScriptCatalog(
            sp.GetServices<IScriptLibrary>(),
            sp.GetServices<IScriptHostFunction>().Concat(ObjectFunctions(sp))));

        if (services.All(d => d.ServiceType != typeof(IValidatorFactory) || d.ImplementationType != typeof(JsValidatorFactory)))
        {
            services.AddSingleton<IValidatorFactory, JsValidatorFactory>();
        }

        // JavaScript hooks (architecture §18.8): js:<library>.<function> under hooks:.
        if (services.All(d => d.ServiceType != typeof(IHookFactory) || d.ImplementationType != typeof(JsHookFactory)))
        {
            services.AddSingleton<IHookFactory, JsHookFactory>();
        }

        services.TryAddSingleton(new JsValidatorOptions());
    }

    /// <inheritdoc/>
    public ExtensionInfo Info() => new() { Entries = [ExtensionDiscovery.StepEntry(typeof(ScriptStep))] };

    /// <summary>
    /// The host functions of the registered script objects; an object whose methods cannot be exposed (an overload, two data parameters) is left
    /// out with a warning instead of taking the whole catalog down.
    /// </summary>
    private static List<IScriptHostFunction> ObjectFunctions(IServiceProvider services)
    {
        List<IScriptHostFunction> functions = [];
        foreach (RegisteredScriptObject registration in services.GetServices<RegisteredScriptObject>())
        {
            try
            {
                functions.AddRange(ScriptObjects.Functions([registration]));
            }
            catch (InvalidOperationException ex)
            {
                services.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger<ScriptExtension>()
                    .LogWarning("Script object '{Name}' is not offered to scripts: {Message}", registration.Name, ex.Message);
            }
        }

        return functions;
    }

    private sealed class BuiltInsMarker;
}
