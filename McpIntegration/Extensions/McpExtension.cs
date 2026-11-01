using AITaskAgent.Core.Abstractions;
using McpIntegration.Configuration;

namespace McpIntegration.Extensions;

/// <summary>
/// An MCP server as an extension: registers its <see cref="Abstractions.IMcpToolProvider"/>
/// (what <see cref="ServiceCollectionExtensions.AddMcpToolProvider"/> did). As before, the provider
/// is added with TryAdd, so only the first MCP server registered in a collection takes effect.
/// </summary>
public sealed class McpExtension(McpServerConfig config) : IExtension
{
    private readonly McpServerConfig _config = config ?? throw new ArgumentNullException(nameof(config));

    /// <inheritdoc/>
    public string Name => $"AITaskAgent.Mcp:{_config.Name}";

    /// <inheritdoc/>
    public Type? ConfigurationType => typeof(McpServerConfig);

    /// <inheritdoc/>
    public void Register(IExtensionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ServiceCollectionExtensions.RegisterProvider(builder.Services, _config);
    }

    /// <summary>The tools of an MCP server are only known once it is connected, so they are not declared here: only the configuration is.</summary>
    public ExtensionInfo Info() => new() { Entries = [ExtensionEntry.For(ExtensionKinds.Configuration, typeof(McpServerConfig))] };
}
