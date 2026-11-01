using AITaskAgent.Core.Hooks;
using AITaskAgent.YAML.Execution;
using AITaskAgent.YAML.Schemas;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITaskAgent.YAML.Configuration;

/// <summary>
/// Registration of the YAML pipeline engine.
/// </summary>
public static class YamlServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SchemaRegistry"/> and <see cref="YamlPipelineFactory"/> as singletons.
    /// The factory needs the core services (<c>IStepRegistry</c>, <c>IPipeline</c>), so call
    /// <c>AddAITaskAgentCore()</c> (or <c>AddAITaskAgent()</c>, which also adds the LLM steps) too.
    /// Load the schemas into the <see cref="SchemaRegistry"/> before building a pipeline.
    /// </summary>
    public static IServiceCollection AddAITaskAgentYaml(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<SchemaRegistry>();
        services.TryAddSingleton<HookRegistry>();
        services.TryAddSingleton<IHookRegistry>(sp => sp.GetRequiredService<HookRegistry>());
        services.TryAddSingleton<YamlPipelineFactory>();
        return services;
    }
}
