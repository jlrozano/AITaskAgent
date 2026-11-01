using AITaskAgent.FileTools.Tools;
using AITaskAgent.Core.Abstractions;

namespace AITaskAgent.FileTools;

/// <summary>
/// The file tools as an extension: registers every file <c>ITool</c>
/// (what <see cref="ServiceCollectionExtensions.AddFileTools(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/> did).
/// </summary>
/// <param name="options">
/// Roots to apply when the extension registers (<see cref="FileToolsConfiguration"/> is static).
/// Null leaves the configuration untouched.
/// </param>
public sealed class FileToolsExtension(FileToolsOptions? options = null) : IExtension
{
    /// <inheritdoc/>
    public string Name => "AITaskAgent.FileTools";

    /// <inheritdoc/>
    public Type? ConfigurationType => typeof(FileToolsOptions);

    /// <inheritdoc/>
    public void Register(IExtensionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (options != null)
        {
            ServiceCollectionExtensions.Apply(options);
        }

        ServiceCollectionExtensions.RegisterTools(builder.Services);
    }

    /// <inheritdoc/>
    public ExtensionInfo Info() => new()
    {
        Entries =
        [
            .. new[]
            {
                typeof(ViewFileTool), typeof(WriteToFileTool), typeof(ReplaceFileContentTool), typeof(MultiReplaceFileContentTool), typeof(ListDirTool),
                typeof(FindByNameTool), typeof(GrepSearchTool), typeof(ViewFileOutlineTool), typeof(ViewCodeItemTool)
            }.Select(t => ExtensionEntry.For(ExtensionKinds.Tool, t)),
            ExtensionEntry.For(ExtensionKinds.Configuration, typeof(FileToolsOptions))
        ]
    };
}
