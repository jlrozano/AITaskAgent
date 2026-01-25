namespace McpIntegration.Configuration;

/// <summary>
/// Defines the transport type for MCP server connections.
/// </summary>
public enum McpTransportType
{
    /// <summary>
    /// Standard input/output transport. Most common for local MCP servers.
    /// Requires Command and Arguments configuration.
    /// </summary>
    Stdio,

    /// <summary>
    /// HTTP-based Streamable transport (recommended for remote servers).
    /// Uses the server URL directly.
    /// </summary>
    StreamableHttp,

    /// <summary>
    /// Server-Sent Events transport (legacy, deprecated in MCP standard).
    /// Uses the server URL with SSE endpoint.
    /// </summary>
    Sse
}
