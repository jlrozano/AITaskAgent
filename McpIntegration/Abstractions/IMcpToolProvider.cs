using AITaskAgent.LLM.Tools.Abstractions;

namespace McpIntegration.Abstractions;

/// <summary>
/// Provider that connects to MCP servers and exposes tools as ITool.
/// Supports multiple transport types (stdio, HTTP, SSE).
/// </summary>
public interface IMcpToolProvider : IAsyncDisposable
{
    /// <summary>
    /// Gets the server configuration.
    /// </summary>
    string ServerName { get; }

    /// <summary>
    /// Indicates whether the provider is currently connected to the MCP server.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Connects to the MCP server using the configured transport.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when already connected.</exception>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects from the MCP server.
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Gets all tools available from the connected MCP server as ITool implementations.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of tools wrapped as ITool.</returns>
    /// <exception cref="InvalidOperationException">Thrown when not connected.</exception>
    Task<IReadOnlyList<ITool>> GetToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes the tool list from the MCP server.
    /// Useful when the server's tools may have changed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RefreshToolsAsync(CancellationToken cancellationToken = default);
}
