using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Observability;
using AITaskAgent.Support.Caching;
using AITaskAgent.Support.Template;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AITaskAgent.Configuration;

/// <summary>
/// Extension methods for registering the AITaskAgent core services (no LLM).
/// </summary>
/// <remarks>
/// <c>AddAITaskAgent</c>/<c>UseAITaskAgent</c>, which also register the LLM services, live with
/// the LLM extension (<c>LlmServiceCollectionExtensions</c>, same namespace).
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the AITaskAgent core services: options, pipeline, event channel, context storage and
    /// factory, template renderer, cache and the <see cref="Core.Abstractions.IStepRegistry"/>.
    /// No LLM services; see <c>AddAITaskAgent</c> for those. Extensions are added with
    /// <see cref="ExtensionServiceCollectionExtensions.AddExtension{TExtension}"/>.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddAITaskAgentCore(
        this IServiceCollection services)
    {
        // 1. Load basic options
        services.AddOptions<AITaskAgentOptions>()
            .Configure<IConfiguration>((options, config) =>
            {
                config.GetSection(AITaskAgentConfigurationKeys.RootSection).BindEx(options);
            });

        // 2. Register PipelineOptions for IOptions<T>
        services.AddOptions<PipelineOptions>()
            .Configure<IOptions<AITaskAgentOptions>>((pipelineOptions, agentOptions) =>
            {
                pipelineOptions.DefaultPipelineTimeout = agentOptions.Value.Timeouts.DefaultPipelineTimeout;
                pipelineOptions.DefaultStepTimeout = agentOptions.Value.Timeouts.DefaultStepTimeout;
            });

        // 3. Register Pipeline as Singleton
        services.TryAddSingleton<IPipeline>(sp =>
        {
            var pipelineOptions = sp.GetRequiredService<IOptions<PipelineOptions>>().Value;

            return new Pipeline(
                pipelineOptions.DefaultPipelineTimeout,
                pipelineOptions.DefaultStepTimeout,
                sp.GetRequiredService<ILoggerFactory>());
        });

        // 4. Shared Infrastructure
        services.TryAddSingleton<ITemplateRendered, TinyTemplateRendered>();
        services.TryAddSingleton<IEventChannel>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AITaskAgentOptions>>().Value;
            var logger = sp.GetService<ILogger<EventChannel>>();
            return new EventChannel(
                new EventChannelOptions
                {
                    EventLogLevel = options.Observability.EventLogLevel,
                    ChannelCapacity = options.Observability.EventChannelCapacity
                },
                logger);
        });
        // Chosen by AITaskAgent:Storage:Context (memory by default); a storage the host registers
        // itself wins: before this call through TryAdd, after it as the last registration.
        services.TryAddSingleton<IPipelineContextStorage>(CreateContextStorage);
        services.TryAddSingleton<PipelineContextFactory>();
        services.TryAddSingleton<ICacheService, MemoryCacheService>();

        // 5. Step registry (singleton instance with the core's [StepTag] steps); extensions add to it
        services.GetOrAddStepRegistry();

        return services;
    }

    private static IPipelineContextStorage CreateContextStorage(IServiceProvider sp)
    {
        const string key = AITaskAgentConfigurationKeys.ContextStorage;
        StorageLocationOptions options = sp.GetRequiredService<IOptions<AITaskAgentOptions>>().Value.Storage.Context;

        return options.GetValidatedType(key, StorageLocationOptions.Memory, StorageLocationOptions.File) switch
        {
            StorageLocationOptions.File => new FilePipelineContextStorage(
                options.GetRequiredDirectory(key),
                // Resolved per load, not here: the factory itself depends on this storage.
                () => sp.GetRequiredService<PipelineContextFactory>().NewContext()),
            _ => new InMemoryPipelineContextStorage()
        };
    }

    /// <summary>
    /// Initializes the core's static factories. Call this once after building the ServiceProvider
    /// (<c>UseAITaskAgent</c> calls it).
    /// </summary>
    public static IServiceProvider UseAITaskAgentCore(this IServiceProvider serviceProvider)
    {
        // Force singleton creation to initialize services
        _ = serviceProvider.GetRequiredService<IPipeline>();
        _ = serviceProvider.GetRequiredService<PipelineContextFactory>();

        return serviceProvider;
    }
}
