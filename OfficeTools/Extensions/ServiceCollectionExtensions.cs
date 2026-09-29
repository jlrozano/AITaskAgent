using AITaskAgent.LLM.Tools.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using OfficeTools.Abstractions;
using OfficeTools.Extractors;
using OfficeTools.Tools;

namespace OfficeTools.Extensions;

/// <summary>
/// Extension methods for registering OfficeTools services and tools in Microsoft.Extensions.DependencyInjection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds OfficeTools extractors and LLM tools to the specified service collection.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddOfficeTools(this IServiceCollection services)
    {
        // 1. Register Extractors
        services.AddSingleton<IAccessSchemaExtractor, AccessSchemaExtractor>();
        services.AddSingleton<IAccessVbaExtractor, AccessVbaExtractor>();
        services.AddSingleton<IDocxReader, DocxReader>();

        // 2. Register LLM Tools
        services.AddSingleton<AccessSchemaTool>();
        services.AddSingleton<ITool, AccessSchemaTool>(sp => sp.GetRequiredService<AccessSchemaTool>());

        services.AddSingleton<AccessQuerySqlTool>();
        services.AddSingleton<ITool, AccessQuerySqlTool>(sp => sp.GetRequiredService<AccessQuerySqlTool>());

        services.AddSingleton<DocxSearchTool>();
        services.AddSingleton<ITool, DocxSearchTool>(sp => sp.GetRequiredService<DocxSearchTool>());

        return services;
    }
}
