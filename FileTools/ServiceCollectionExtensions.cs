using AITaskAgent.FileTools.Tools;
using AITaskAgent.LLM.Tools.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AITaskAgent.FileTools;

public static class ServiceCollectionExtensions
{
    /// <summary>Configuration section the file tools bind to.</summary>
    public const string ConfigurationSection = "AITaskAgent:FileTools";

    /// <summary>
    /// Registers all file tools into the service collection.
    /// </summary>
    /// <remarks>
    /// Leaves <see cref="FileToolsConfiguration"/> untouched, so paths resolve against the
    /// executable's directory unless a root is configured elsewhere. Prefer one of the overloads
    /// that state the root explicitly.
    /// </remarks>
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

    /// <summary>
    /// Registers all file tools rooted at <paramref name="rootDirectory"/>.
    /// </summary>
    /// <param name="services">Service collection to register the tools into.</param>
    /// <param name="rootDirectory">
    /// Directory relative paths resolve against, and always an allowed location.
    /// </param>
    /// <param name="additionalAllowedDirectories">
    /// Extra directories the tools may touch. Relative paths never resolve against these.
    /// </param>
    public static IServiceCollection AddFileTools(
        this IServiceCollection services,
        string rootDirectory,
        params string[] additionalAllowedDirectories) =>
        services.AddFileTools(options =>
        {
            options.RootDirectory = rootDirectory;
            options.AdditionalAllowedDirectories = [.. additionalAllowedDirectories];
        });

    /// <summary>
    /// Registers all file tools, configuring the roots in code.
    /// </summary>
    public static IServiceCollection AddFileTools(
        this IServiceCollection services,
        Action<FileToolsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new FileToolsOptions();
        configure(options);
        Apply(options);

        return services.AddFileTools();
    }

    /// <summary>
    /// Registers all file tools, reading the roots from the
    /// <see cref="ConfigurationSection"/> section.
    /// </summary>
    public static IServiceCollection AddFileTools(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new FileToolsOptions();
        configuration.GetSection(ConfigurationSection).Bind(options);
        Apply(options);

        return services.AddFileTools();
    }

    private static void Apply(FileToolsOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.RootDirectory))
        {
            FileToolsConfiguration.RootDirectory = options.RootDirectory;
        }

        foreach (var directory in options.AdditionalAllowedDirectories)
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                FileToolsConfiguration.AddAllowedDirectory(directory);
            }
        }
    }
}
