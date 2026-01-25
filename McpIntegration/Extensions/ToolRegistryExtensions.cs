using AITaskAgent.LLM.Tools.Abstractions;
using McpIntegration.Abstractions;

namespace McpIntegration.Extensions;

/// <summary>
/// Extension methods for registering MCP tools in IToolRegistry.
/// </summary>
public static class ToolRegistryExtensions
{
    /// <summary>
    /// Registers all tools from an MCP provider into the tool registry.
    /// </summary>
    /// <param name="registry">The tool registry.</param>
    /// <param name="provider">The MCP tool provider (must be connected).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of tools registered.</returns>
    public static async Task<int> RegisterMcpToolsAsync(
        this IToolRegistry registry,
        IMcpToolProvider provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(provider);

        if (!provider.IsConnected)
        {
            throw new InvalidOperationException(
                $"MCP provider '{provider.ServerName}' is not connected. Call ConnectAsync first.");
        }

        var tools = await provider.GetToolsAsync(cancellationToken);
        foreach (var tool in tools)
        {
            registry.Register(tool);
        }

        return tools.Count;
    }

    /// <summary>
    /// Registers all tools from multiple MCP providers into the tool registry.
    /// </summary>
    /// <param name="registry">The tool registry.</param>
    /// <param name="providers">The MCP tool providers (must be connected).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Total number of tools registered.</returns>
    public static async Task<int> RegisterMcpToolsAsync(
        this IToolRegistry registry,
        IEnumerable<IMcpToolProvider> providers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(providers);

        var totalRegistered = 0;
        foreach (var provider in providers)
        {
            totalRegistered += await registry.RegisterMcpToolsAsync(provider, cancellationToken);
        }

        return totalRegistered;
    }
}
