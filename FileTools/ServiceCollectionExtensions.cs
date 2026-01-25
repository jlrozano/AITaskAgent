using AITaskAgent.FileTools.Tools;
using AITaskAgent.LLM.Tools.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AITaskAgent.FileTools;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all file tools into the service collection.
    /// </summary>
    public static IServiceCollection AddFileTools(this IServiceCollection services)
    {
        services.AddTransient<ITool, ViewFileTool>();
        services.AddTransient<ITool, WriteToFileTool>();
        services.AddTransient<ITool, ReplaceFileContentTool>();
        services.AddTransient<ITool, MultiReplaceFileContentTool>();
        services.AddTransient<ITool, ListDirTool>();
        services.AddTransient<ITool, FindByNameTool>();
        services.AddTransient<ITool, GrepSearchTool>();
        services.AddTransient<ITool, ViewFileOutlineTool>();
        services.AddTransient<ITool, ViewCodeItemTool>();

        return services;
    }
}
