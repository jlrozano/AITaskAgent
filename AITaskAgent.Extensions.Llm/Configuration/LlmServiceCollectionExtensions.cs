using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Conversation.Storage;
using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.LLM.Tools.Implementation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Reflection;

// Same namespace as the core registration, so callers keep finding AddAITaskAgent where it was.
namespace AITaskAgent.Configuration;

/// <summary>
/// Extension methods for registering AITaskAgent with its LLM services.
/// </summary>
public static class LlmServiceCollectionExtensions
{
    /// <summary>
    /// Adds AITaskAgent services to the service collection: the core
    /// (<see cref="ServiceCollectionExtensions.AddAITaskAgentCore"/>) plus the LLM services
    /// (<see cref="AddAITaskAgentLlm"/>).
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddAITaskAgent(
        this IServiceCollection services)
    {
        services.AddAITaskAgentCore();
        services.AddAITaskAgentLlm();
        return services;
    }

    /// <summary>
    /// Adds the LLM services: <see cref="LlmEnvironment"/> binding, conversation storage and
    /// factory, tools, and the context factory that makes the pipeline's default context an
    /// <see cref="LlmPipelineContext"/>. Expects <see cref="ServiceCollectionExtensions.AddAITaskAgentCore"/>
    /// too, which <see cref="AddAITaskAgent"/> calls.
    /// </summary>
    /// <remarks>Sugar for <c>services.AddExtension(new LlmExtension())</c>.</remarks>
    public static IServiceCollection AddAITaskAgentLlm(
        this IServiceCollection services)
        => services.AddExtension(new LlmExtension());

    /// <summary>The service registrations of <see cref="LlmExtension"/>.</summary>
    internal static void RegisterLlmServices(IServiceCollection services)
    {
        // LlmEnvironment, bound through internal Raw models for clean binding
        services.AddSingleton<IConfigureOptions<LlmEnvironment>>(sp =>
            new ConfigureLlmEnvironmentOptions(sp));

        // Default contexts must carry a conversation. Replace only the core's own default, so a
        // factory the application registered itself is kept, as TryAdd always allowed.
        var existingFactory = services.FirstOrDefault(d => d.ServiceType == typeof(PipelineContextFactory));
        if (existingFactory is null || existingFactory.ImplementationType == typeof(PipelineContextFactory))
        {
            services.Replace(ServiceDescriptor.Singleton<PipelineContextFactory, LlmPipelineContextFactory>());
        }

        // Chosen by AITaskAgent:Storage:Conversation (memory by default). TryAdd: a storage the host
        // registers itself wins, before this call or after it (the last registration is resolved).
        services.TryAddSingleton<IConversationStorage>(CreateConversationStorage);

        services.AddSingleton<ITools>(AvailableTools.Instance);

        // Tool discovery
        //AddToolDiscovery(services);

        // Conversation factory
        services.AddTransient(sp =>
        {
            var config = sp.GetRequiredService<IOptions<AITaskAgentOptions>>().Value;
            var llmService = sp.GetService<ILlmService>() ?? new NullLlmService();

            return new ConversationContext(
                config.Conversation.MaxTokens,
                llmService.EstimateTokenCount,
                sp.GetService<ILogger<ConversationContext>>(),
                config.Conversation);
        });
    }

    private static IConversationStorage CreateConversationStorage(IServiceProvider sp)
    {
        const string key = AITaskAgentConfigurationKeys.ConversationStorage;
        StorageLocationOptions options = sp.GetRequiredService<IOptions<AITaskAgentOptions>>().Value.Storage.Conversation;

        return options.GetValidatedType(key, StorageLocationOptions.Memory, StorageLocationOptions.File, StorageLocationOptions.Yaml) switch
        {
            StorageLocationOptions.File => new FileConversationStorage(options.GetRequiredDirectory(key)),
            StorageLocationOptions.Yaml => new YamlFileConversationStorage(options.GetRequiredDirectory(key)),
            _ => new InMemoryConversationStorage()
        };
    }

    /// <summary>
    /// Initializes internal static configurations and factories required by AITaskAgent Framework
    /// (<see cref="ServiceCollectionExtensions.UseAITaskAgentCore"/>) and registers the
    /// <see cref="ITool"/> services in <see cref="AvailableTools"/>.
    /// Call this once after building the ServiceProvider.
    /// </summary>
    public static IServiceProvider UseAITaskAgent(this IServiceProvider serviceProvider)
    {
        serviceProvider.UseAITaskAgentCore();

        // Built now so a wrong AITaskAgent:Storage:Conversation fails at startup, not at the first save.
        _ = serviceProvider.GetService<IConversationStorage>();

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
                           ?? availableServices.FirstOrDefault(s => s.Name.Contains(serviceType, StringComparison.OrdinalIgnoreCase));

                // Never fall back to "whatever service is registered": silently sending a prompt
                // to a different vendor than the one configured is far worse than refusing to start.
                if (service == null)
                {
                    var registered = availableServices.Count > 0
                        ? string.Join(", ", availableServices.Select(s => s.Name))
                        : "(none registered)";
                    throw new InvalidOperationException(
                        $"LLM provider '{providerName}' requests service type '{serviceType}', but no matching " +
                        $"ILlmService is registered. Registered services: {registered}. " +
                        "Register the provider's service, or correct the 'ServiceType' value.");
                }

                if (string.IsNullOrWhiteSpace(r.BaseUrl))
                {
                    throw new InvalidOperationException(
                        $"LLM provider '{providerName}' has no BaseUrl configured.");
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

            // A fallback that quietly vanishes because of a typo is the worst possible outcome:
            // it only shows up as an outage when the primary provider is already failing.
            var globalFallbacks = raw.FallbackProviders
                .Select(f => BuildFallback(f, options.Providers, "FallbackProviders"))
                .ToList();

            // FallbackProviders is documented as functional on IOptions<LlmEnvironment>.Value, so
            // the raw, unresolved settings must actually land on options, not just the resolved
            // globalFallbacks used internally to seed roles below.
            options.FallbackProviders.AddRange(raw.FallbackProviders);

            // 2. Build Roles
            foreach (var kvp in raw.Roles)
            {
                var r = kvp.Value;
                var roleName = kvp.Key;

                // Same reasoning as above: a role pointing at a provider that does not exist is a
                // configuration bug, not something to paper over with the first provider at hand.
                if (string.IsNullOrEmpty(r.Provider))
                {
                    throw new InvalidOperationException(
                        $"LLM role '{roleName}' does not declare a Provider.");
                }

                if (!options.Providers.TryGetValue(r.Provider, out var provider))
                {
                    var available = options.Providers.Count > 0
                        ? string.Join(", ", options.Providers.Keys)
                        : "(none configured)";
                    throw new InvalidOperationException(
                        $"LLM role '{roleName}' references provider '{r.Provider}', which is not configured. " +
                        $"Available providers: {available}.");
                }

                if (string.IsNullOrWhiteSpace(r.Model))
                {
                    throw new InvalidOperationException(
                        $"LLM role '{roleName}' does not declare a Model.");
                }

                var roleFallbacks = r.Fallbacks != null && r.Fallbacks.Count > 0
                    ? [.. r.Fallbacks.Select(f => BuildFallback(f, options.Providers, $"role '{roleName}'"))]
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

            // This was never copied from `raw`, so a configured DefaultRole other than "default"
            // was silently ignored - options.DefaultRole stayed at its hardcoded initializer, and
            // every sample happened to name a role "default" too, so nobody noticed.
            options.DefaultRole = raw.DefaultRole;

            // Steps that name no role fall back to this one, so a missing default is a failure
            // waiting to happen at the first such step rather than at startup.
            if (options.Roles.Count > 0 && !options.Roles.ContainsKey(options.DefaultRole))
            {
                throw new InvalidOperationException(
                    $"Default LLM role '{options.DefaultRole}' is not configured. " +
                    $"Available roles: {string.Join(", ", options.Roles.Keys)}.");
            }
        }

        /// <summary>
        /// Builds a fallback entry, failing loudly when it names a provider that does not exist or
        /// omits the model.
        /// </summary>
        private static LlmFallback BuildFallback(
            FallbackProviderSettings f,
            Dictionary<string, LlmProvider> providers,
            string origin)
        {
            if (!providers.TryGetValue(f.Provider, out var provider))
            {
                var available = providers.Count > 0
                    ? string.Join(", ", providers.Keys)
                    : "(none configured)";
                throw new InvalidOperationException(
                    $"Fallback in {origin} references provider '{f.Provider}', which is not configured. " +
                    $"Available providers: {available}.");
            }

            if (string.IsNullOrWhiteSpace(f.Model))
            {
                throw new InvalidOperationException(
                    $"Fallback in {origin} for provider '{f.Provider}' does not declare a Model.");
            }

            return new LlmFallback(f.Model, provider, f.Inference);
        }
    }

    internal class LlmEnvironmentRaw
    {
        public Dictionary<string, LlmProviderRaw> Providers { get; set; } = [];
        public Dictionary<string, LlmRoleRaw> Roles { get; set; } = [];
        public string DefaultRole { get; set; } = "default";
        public List<FallbackProviderSettings> FallbackProviders { get; set; } = [];
    }

    internal class LlmProviderRaw
    {
        public string? BaseUrl { get; set; }
        public string? ApiKey { get; set; }
        public string? ApiVersion { get; set; }
        public string? ServiceType { get; set; }
        public LlmInferenceSettings? Inference { get; set; }
        public List<LlmModelSettings> Models { get; set; } = [];
    }

    internal class LlmRoleRaw
    {
        public string? Provider { get; set; }
        public string? Model { get; set; }
        public string? SystemPrompt { get; set; }
        public LlmInferenceSettings? Inference { get; set; }
        public List<FallbackProviderSettings>? Fallbacks { get; set; }
    }

    #endregion
}
