using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Conversation.Storage;
using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.LLM.Tools.Implementation;
using AITaskAgent.Observability;
using AITaskAgent.Support.Caching;
using AITaskAgent.Support.Template;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Reflection;

namespace AITaskAgent.Configuration;

/// <summary>
/// Extension methods for registering AITaskAgent services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds AITaskAgent services to the service collection.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddAITaskAgent(
        this IServiceCollection services)
    {
        // 1. Load basic options
        services.AddOptions<AITaskAgentOptions>()
            .Configure<IConfiguration>((options, config) =>
            {
                config.GetSection(AITaskAgentConfigurationKeys.RootSection).BindEx(options);
            });

        // 2. Configure LlmEnvironment using internal Raw models for clean binding
        services.AddSingleton<IConfigureOptions<LlmEnvironment>>(sp =>
            new ConfigureLlmEnvironmentOptions(sp));

        // 3. Register PipelineOptions for IOptions<T>
        services.AddOptions<PipelineOptions>()
            .Configure<IOptions<AITaskAgentOptions>>((pipelineOptions, agentOptions) =>
            {
                pipelineOptions.DefaultPipelineTimeout = agentOptions.Value.Timeouts.DefaultPipelineTimeout;
                pipelineOptions.DefaultStepTimeout = agentOptions.Value.Timeouts.DefaultStepTimeout;
            });

        // 4. Register Pipeline as Singleton
        services.TryAddSingleton<IPipeline>(sp =>
        {
            var pipelineOptions = sp.GetRequiredService<IOptions<PipelineOptions>>().Value;

            return new Pipeline(
                pipelineOptions.DefaultPipelineTimeout,
                pipelineOptions.DefaultStepTimeout,
                sp.GetRequiredService<ILoggerFactory>());
        });

        // 5. Shared Infrastructure
        services.TryAddSingleton<ITemplateRendered, TinyTemplateRendered>();
        services.TryAddSingleton<IEventChannel>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AITaskAgentOptions>>().Value;
            var logger = sp.GetService<ILogger<EventChannel>>();
            return new EventChannel(options.Observability.EventLogLevel, logger);
        });
        services.TryAddSingleton<IPipelineContextStorage, InMemoryPipelineContextStorage>();
        services.TryAddSingleton<PipelineContextFactory>();
        services.TryAddSingleton<ICacheService, MemoryCacheService>();
        services.AddSingleton<IConversationStorage, InMemoryConversationStorage>();

        services.AddSingleton<ITools>(AvailableTools.Instance);

        // 6. Tool discovery
        //AddToolDiscovery(services);

        // 8. Conversation factory
        services.AddTransient(sp =>
        {
            var config = sp.GetRequiredService<IOptions<AITaskAgentOptions>>().Value;
            var llmService = sp.GetService<ILlmService>() ?? new NullLlmService();

            return new ConversationContext(
                config.Conversation.MaxTokens,
                llmService.EstimateTokenCount);
        });

        return services;
    }

    /// <summary>
    /// Initializes internal static configurations and factories required by AITaskAgent Framework.
    /// Call this once after building the ServiceProvider.
    /// </summary>
    public static IServiceProvider UseAITaskAgent(this IServiceProvider serviceProvider)
    {
        // Force singleton creation to initialize services
        _ = serviceProvider.GetRequiredService<IPipeline>();
        _ = serviceProvider.GetRequiredService<PipelineContextFactory>();

        // Registrar tools
        var localTools = serviceProvider.GetServices<ITool>();
        foreach (var tool in localTools)
        {
            AvailableTools.Instance.Register(tool);
        }

        return serviceProvider;
    }

    private static void AddToolDiscovery(IServiceCollection services)
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        var toolTypes = SafeGetTypes(assembly)
            .Where(t => typeof(ITool).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        foreach (var type in toolTypes)
        {
            if (!services.Any(s => s.ServiceType == typeof(ITool) && s.ImplementationType == type))
            {
                services.AddSingleton(typeof(ITool), type);
            }
        }

    }

    private static Type[] SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); } catch { return []; }
    }

    #region Private Configuration Logic

    private class ConfigureLlmEnvironmentOptions(IServiceProvider sp) : IConfigureOptions<LlmEnvironment>
    {
        public void Configure(LlmEnvironment options)
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var section = configuration.GetSection(AITaskAgentConfigurationKeys.LlmProviders);
            var raw = section.GetEx<LlmEnvironmentRaw>();
            if (raw == null)
            {
                return;
            }

            var availableServices = sp.GetServices<ILlmService>().ToList();

            // 1. Build Providers
            foreach (var kvp in raw.Providers)
            {
                var r = kvp.Value;
                var providerName = kvp.Key;
                var serviceType = r.ServiceType ?? providerName;

                var service = availableServices.FirstOrDefault(s => s.Name.Equals(serviceType, StringComparison.OrdinalIgnoreCase))
                           ?? availableServices.FirstOrDefault(s => s.Name.Contains(serviceType, StringComparison.OrdinalIgnoreCase))
                           ?? availableServices.FirstOrDefault();

                if (service == null)
                {
                    continue;
                }

                LlmProvider provider = new()
                {
                    BaseUrl = r.BaseUrl,
                    ApiKey = r.ApiKey,
                    ApiVersion = r.ApiVersion,
                    Inference = r.Inference,
                    Models = r.Models,
                    Service = service
                };

                options.Providers[providerName] = provider;
            }

            var globalFallbacks = raw.FallbackProviders
                .Where(f => options.Providers.ContainsKey(f.Provider) && !string.IsNullOrWhiteSpace(f.Model))
                .Select(f => new LlmFallback(f.Model, options.Providers[f.Provider], f.Inference))
                .ToList();

            // 2. Build Roles
            foreach (var kvp in raw.Roles)
            {
                var r = kvp.Value;
                var roleName = kvp.Key;

                if (string.IsNullOrEmpty(r.Provider) || !options.Providers.TryGetValue(r.Provider, out var provider))
                {
                    if (options.Providers.Count > 0)
                    {
                        provider = options.Providers.Values.First();
                    }
                    else
                    {
                        continue;
                    }
                }
                if (string.IsNullOrWhiteSpace(r.Model))
                {
                    continue;
                }

                var roleFallbacks = r.Fallbacks != null && r.Fallbacks.Count > 0
                    ? [.. r.Fallbacks
                        .Where(f => options.Providers.ContainsKey(f.Provider) && !string.IsNullOrWhiteSpace(f.Model))
                        .Select(f => new LlmFallback(f.Model, options.Providers[f.Provider], f.Inference))]
                    : globalFallbacks;
                LlmRole role = new()
                {
                    Provider = provider,
                    Model = r.Model,
                    SystemPrompt = r.SystemPrompt,
                    Inference = r.Inference,
                    Fallbacks = roleFallbacks
                };

                options.Roles[roleName] = role;
            }

        }
    }

    private class LlmEnvironmentRaw
    {
        public Dictionary<string, LlmProviderRaw> Providers { get; set; } = [];
        public Dictionary<string, LlmRoleRaw> Roles { get; set; } = [];
        public string DefaultRole { get; set; } = "default";
        public List<FallbackProviderSettings> FallbackProviders { get; set; } = [];
    }

    private class LlmProviderRaw
    {
        public string? BaseUrl { get; set; }
        public string? ApiKey { get; set; }
        public string? ApiVersion { get; set; }
        public string? ServiceType { get; set; }
        public LlmInferenceSettings? Inference { get; set; }
        public List<LlmModelSettings> Models { get; set; } = [];
    }

    private class LlmRoleRaw
    {
        public string? Provider { get; set; }
        public string? Model { get; set; }
        public string? SystemPrompt { get; set; }
        public LlmInferenceSettings? Inference { get; set; }
        public List<FallbackProviderSettings>? Fallbacks { get; set; }
    }

    #endregion
}
