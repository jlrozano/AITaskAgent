namespace McpIntegration.Configuration;

/// <summary>
/// Configuration for an MCP server connection.
/// Supports stdio, HTTP, and SSE transport modes.
/// </summary>
public sealed record McpServerConfig
{
    /// <summary>
    /// Unique name for this MCP server. Used for identification and logging.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Transport type to use for the connection.
    /// </summary>
    public required McpTransportType TransportType { get; init; }

    /// <summary>
    /// Server URL for HTTP-based transports (StreamableHttp, Sse).
    /// Required when TransportType is StreamableHttp or Sse.
    /// Example: "https://test.kukapay.com/api/mcp"
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Command to execute for stdio transport.
    /// Required when TransportType is Stdio.
    /// Example: "npx" or "node"
    /// </summary>
    public string? Command { get; init; }

    /// <summary>
    /// Arguments for the stdio command.
    /// Example: ["-y", "@modelcontextprotocol/server-everything"]
    /// </summary>
    public IReadOnlyList<string>? Arguments { get; init; }

    /// <summary>
    /// Working directory for stdio command execution.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Environment variables for stdio command execution.
    /// </summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }

    /// <summary>
    /// HTTP headers to include in requests (e.g. Authentication).
    /// Used for StreamableHttp and Sse transports.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Connection timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Validates the configuration based on transport type.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when required fields are missing.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException("MCP server Name is required.");
        }

        switch (TransportType)
        {
            case McpTransportType.Stdio:
                if (string.IsNullOrWhiteSpace(Command))
                {
                    throw new InvalidOperationException(
                        $"Command is required for Stdio transport. Server: {Name}");
                }
                break;

            case McpTransportType.StreamableHttp:
            case McpTransportType.Sse:
                if (string.IsNullOrWhiteSpace(Url))
                {
                    throw new InvalidOperationException(
                        $"Url is required for {TransportType} transport. Server: {Name}");
                }
                break;
        }
    }
}
