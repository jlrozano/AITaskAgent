using AITaskAgent.LLM.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using OpenAILLmService;

namespace CodebaseAnalysis.Core.Hosting;

/// <summary>Valores de <c>ServiceType</c> de un proveedor: el <see cref="ILlmService.Name"/> de cada servicio del sample.</summary>
public static class LlmServiceTypes
{
    public const string AzureOpenAI = "azure-openai";
    public const string OpenAI = "openai";
    public const string OpenRouter = "openrouter";
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra los tres servicios LLM concretos como <see cref="ILlmService"/>: el framework resuelve el <c>ServiceType</c>
    /// de cada proveedor por su <see cref="ILlmService.Name"/> entre los registrados. Azure va el último porque es el
    /// <see cref="ILlmService"/> por defecto.
    /// </summary>
    public static IServiceCollection AddCodebaseAnalysisLlmServices(this IServiceCollection services)
    {
        services.AddSingleton<AzureOpenAILlmService>();
        services.AddSingleton<OpenAILlmService>();
        services.AddSingleton<OpenRouterLlmService>();

        services.AddSingleton<ILlmService>(sp => sp.GetRequiredService<OpenAILlmService>());
        services.AddSingleton<ILlmService>(sp => sp.GetRequiredService<OpenRouterLlmService>());
        services.AddSingleton<ILlmService>(sp => sp.GetRequiredService<AzureOpenAILlmService>());
        return services;
    }
}
