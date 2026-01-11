using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.JSON;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Conversation.Storage;
using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.LLM.Tools.Implementation;
using AITaskAgent.Observability;
using AITaskAgent.Resilience;
using AITaskAgent.Support.Caching;
using AITaskAgent.Support.Template;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;


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
    /// <param name="configuration">Optional configuration section. If null, uses default configuration.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddAITaskAgent(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        // Load centralized configuration
        services.AddOptions<AITaskAgentOptions>()
            .Configure<IConfiguration>((options, config) =>
            {
                if (configuration != null)
                {
                    configuration.Bind(options);
                }
                else
                {
                    config.GetSection(AITaskAgentConfigurationKeys.RootSection).Bind(options);
                }
            });

        // Configure Pipeline static defaults from configuration
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IOptions<AITaskAgentOptions>>().Value;
            Pipeline.DefaultPipelineTimeout = config.Timeouts.DefaultPipelineTimeout;
            Pipeline.DefaultStepTimeout = config.Timeouts.DefaultStepTimeout;
            return config;
        });

        // Core services
        services.AddSingleton<JsonResponseParser>();
        services.AddSingleton<IStepTracer, ConsoleStepTracer>();

        // Observability - Event Channel
        services.AddSingleton<IEventChannel, EventChannel>();
        services.PostConfigure<EventChannelOptions>(options =>
        {
            // EventLogLevel will be set from AITaskAgentOptions in EventChannel constructor
            // Default is already set in ObservabilityOptions
        });

        // Pipeline Context Factory
        services.AddSingleton(sp =>
        {
            // Initialize static Pipeline logger factory
            Pipeline.LoggerFactory = sp.GetRequiredService<ILoggerFactory>();

            var config = sp.GetRequiredService<IOptions<AITaskAgentOptions>>().Value;
            var eventChannel = sp.GetService<IEventChannel>();

            return new PipelineContextFactory(
                eventChannel);
        });

        // LLM Provider configuration
        services.AddOptions<LlmProvidersConfig>()
            .Configure<IConfiguration>((options, config) =>
            {
                config.GetSection(AITaskAgentConfigurationKeys.LlmProviders).Bind(options);
            });

        services.AddSingleton<ILlmProviderResolver>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<LlmProvidersConfig>>().Value;
            return new LlmProviderResolver(config);
        });

        // Tool system
        services.AddSingleton<IToolRegistry, ToolRegistry>();

        if (!services.Any(s => s.ServiceType == typeof(ITemplateEngine)))
        {
            // Template engine
            services.AddSingleton<ITemplateEngine, JsonTemplateEngine>();
        }

        // Caching
        services.AddMemoryCache();
        services.AddSingleton<ICacheService, MemoryCacheService>();

        // Resilience services
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IOptions<AITaskAgentOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<CircuitBreaker>>();

            return new CircuitBreaker(
                config.CircuitBreaker.FailureThreshold,
                TimeSpan.FromSeconds(config.CircuitBreaker.OpenDurationSeconds),
                logger);
        });

        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IOptions<AITaskAgentOptions>>().Value;

            return new RateLimiter(
                config.RateLimit.MaxTokens,
                TimeSpan.FromMilliseconds(config.RateLimit.RefillIntervalMs),
                config.RateLimit.TokensPerRefill);
        });

        // Conversation storage
        services.AddSingleton<IConversationStorage, InMemoryConversationStorage>();

        // Conversation factory
        services.AddTransient(sp =>
        {
            var config = sp.GetRequiredService<IOptions<AITaskAgentOptions>>().Value;
            var llmService = sp.GetRequiredService<ILlmService>();

            return new ConversationContext(
                config.Conversation.MaxTokens,
                llmService.EstimateTokenCount);
        });

        return services;
    }
}
