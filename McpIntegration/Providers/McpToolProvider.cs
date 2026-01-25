using AITaskAgent.LLM.Tools.Abstractions;
using McpIntegration.Abstractions;
using McpIntegration.Configuration;
using McpIntegration.Tools;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpIntegration.Providers;

/// <summary>
/// Provides ITool implementations from an MCP server.
/// Supports stdio, HTTP, and SSE transport modes.
/// </summary>
public sealed class McpToolProvider : IMcpToolProvider
{
    private readonly McpServerConfig _config;
    private readonly ILogger<McpToolProvider> _logger;
    private readonly Lock _lock = new();

    private McpClient? _client;
    private IReadOnlyList<ITool>? _cachedTools;

    /// <summary>
    /// Creates a new MCP tool provider with the specified configuration.
    /// </summary>
    /// <param name="config">Server configuration.</param>
    /// <param name="logger">Logger instance.</param>
    public McpToolProvider(McpServerConfig config, ILogger<McpToolProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        config.Validate();
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string ServerName => _config.Name;

    /// <inheritdoc/>
    public bool IsConnected => _client is not null;

    /// <inheritdoc/>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_client is not null)
            {
                throw new InvalidOperationException(
                    $"Already connected to MCP server: {_config.Name}");
            }
        }

        _logger.LogInformation(
            "Connecting to MCP server {ServerName} using {TransportType} transport",
            _config.Name, _config.TransportType);

        try
        {
            var transport = CreateTransport();
            var client = await McpClient.CreateAsync(
                transport,
                cancellationToken: cancellationToken);

            lock (_lock)
            {
                _client = client;
            }

            _logger.LogInformation(
                "Successfully connected to MCP server: {ServerName}",
                _config.Name);

            // Register for process exit to ensure cleanup
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to connect to MCP server {ServerName}: {Error}",
                _config.Name, ex.Message);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task DisconnectAsync()
    {
        McpClient? client;
        lock (_lock)
        {
            client = _client;
            _client = null;
            _cachedTools = null;
        }

        if (client is not null)
        {
            // Unregister first
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

            await client.DisposeAsync();
            _logger.LogInformation("Disconnected from MCP server: {ServerName}", _config.Name);
        }
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        if (_client != null)
        {
            try
            {
                // Force synchronous disposal during process exit
                _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // Ignore errors during shutdown
            }
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ITool>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_client is null)
            {
                throw new InvalidOperationException(
                    $"Not connected to MCP server: {_config.Name}. Call ConnectAsync first.");
            }

            if (_cachedTools is not null)
            {
                return _cachedTools;
            }
        }

        await RefreshToolsAsync(cancellationToken);

        lock (_lock)
        {
            return _cachedTools ?? [];
        }
    }

    /// <inheritdoc/>
    public async Task RefreshToolsAsync(CancellationToken cancellationToken = default)
    {
        McpClient client;
        lock (_lock)
        {
            if (_client is null)
            {
                throw new InvalidOperationException(
                    $"Not connected to MCP server: {_config.Name}. Call ConnectAsync first.");
            }
            client = _client;
        }

        _logger.LogDebug("Fetching tools from MCP server: {ServerName}", _config.Name);

        var mcpTools = await client.ListToolsAsync(new ListToolsRequestParams(), cancellationToken);
        var wrappedTools = (mcpTools.Tools ?? []).Select(t => new McpToolWrapper(t, client, _config.Name))
            .Cast<ITool>()
            .ToList();

        lock (_lock)
        {
            _cachedTools = wrappedTools;
        }

        _logger.LogInformation(
            "Found {ToolCount} tools from MCP server {ServerName}: {ToolNames}",
            wrappedTools.Count,
            _config.Name,
            string.Join(", ", wrappedTools.Select(t => t.Name)));
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }

    /// <summary>
    /// Creates the appropriate transport based on configuration.
    /// </summary>
    private IClientTransport CreateTransport()
    {
        return _config.TransportType switch
        {
            McpTransportType.Stdio => CreateStdioTransport(),
            McpTransportType.StreamableHttp => CreateHttpTransport(),
            McpTransportType.Sse => CreateSseTransport(),
            _ => throw new NotSupportedException(
                $"Transport type {_config.TransportType} is not supported.")
        };
    }

    /// <summary>
    /// Creates a stdio transport for local MCP server execution.
    /// </summary>
    private StdioClientTransport CreateStdioTransport()
    {
        var options = new StdioClientTransportOptions
        {
            Command = _config.Command!,
            Arguments = _config.Arguments?.ToArray() ?? []
        };

        if (!string.IsNullOrEmpty(_config.WorkingDirectory))
        {
            options.WorkingDirectory = _config.WorkingDirectory;
        }

        if (_config.EnvironmentVariables is { Count: > 0 })
        {
            // Fix: Cast explicitly to IDictionary<string, string?>
            var envVars = new Dictionary<string, string?>();
            foreach (var kvp in _config.EnvironmentVariables)
            {
                envVars[kvp.Key] = kvp.Value;
            }
            options.EnvironmentVariables = envVars;
        }

        return new StdioClientTransport(options);
    }

    /// <summary>
    /// Creates an HTTP Streamable transport for remote servers.
    /// </summary>
    private HttpClientTransport CreateHttpTransport()
    {
        var httpClient = CreateConfiguredHttpClient();
        return new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(_config.Url!)
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: true);
    }

    /// <summary>
    /// Creates an SSE transport for legacy servers.
    /// </summary>
    private HttpClientTransport CreateSseTransport()
    {
        var httpClient = CreateConfiguredHttpClient();
        return new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(_config.Url!)
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: true);
    }

    private HttpClient CreateConfiguredHttpClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds);

        if (_config.Headers is not null)
        {
            foreach (var kvp in _config.Headers)
            {
                client.DefaultRequestHeaders.Add(kvp.Key, kvp.Value);
            }
        }

        return client;
    }
}
