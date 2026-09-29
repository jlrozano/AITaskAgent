using AITaskAgent.LLM.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using OpenAILLmService;

namespace CodebaseAnalysis.Core.Hosting;

/// <summary>Valores de <c>ServiceType</c> de un proveedor, y claves de su <see cref="ILlmService"/> en DI.</summary>
public static class LlmServiceTypes
{
    public const string AzureOpenAI = "azure-openai";
    public const string OpenAI = "openai";
    public const string OpenRouter = "openrouter";
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra los tres servicios LLM concretos, un <see cref="ILlmService"/> con clave por cada
    /// <see cref="LlmServiceTypes"/> (así <see cref="LlmRoleFactory"/> resuelve el <c>ServiceType</c> de
    /// cada proveedor, y un test puede registrar el suyo con otra clave) y el <see cref="ILlmService"/>
    /// por defecto, que es Azure.
    /// </summary>
    public static IServiceCollection AddCodebaseAnalysisLlmServices(this IServiceCollection services)
    {
        services.AddSingleton<AzureOpenAILlmService>();
        services.AddSingleton<OpenAILlmService>();
        services.AddSingleton<OpenRouterLlmService>();

        services.AddKeyedSingleton<ILlmService>(LlmServiceTypes.AzureOpenAI, (sp, _) => sp.GetRequiredService<AzureOpenAILlmService>());
        services.AddKeyedSingleton<ILlmService>(LlmServiceTypes.OpenAI, (sp, _) => sp.GetRequiredService<OpenAILlmService>());
        services.AddKeyedSingleton<ILlmService>(LlmServiceTypes.OpenRouter, (sp, _) => sp.GetRequiredService<OpenRouterLlmService>());

        services.AddSingleton<ILlmService>(sp => sp.GetRequiredService<AzureOpenAILlmService>());
        return services;
    }
}
