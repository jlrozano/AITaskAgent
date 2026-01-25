using McpIntegration.Abstractions;
using McpIntegration.Configuration;
using McpIntegration.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace McpIntegration.Extensions;

/// <summary>
/// Extension methods for registering MCP services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds an MCP Tool Provider with the specified configuration.
    /// </summary>
    public static IServiceCollection AddMcpToolProvider(
        this IServiceCollection services,
        string serverName,
        Action<McpServerConfigBuilder> configure)
    {
        var builder = new McpServerConfigBuilder(serverName);
        configure(builder);
        var config = builder.Build();

        services.TryAddTransient<IMcpToolProvider>(sp =>
            new McpToolProvider(config, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<McpToolProvider>>()));

        return services;
    }

    /// <summary>
    /// Builder for McpServerConfig.
    /// </summary>
    public class McpServerConfigBuilder(string name)
    {
        private readonly string _name = name;
        private McpTransportType _transportType = McpTransportType.StreamableHttp;
        private string? _url;
        private string? _command;
        private readonly List<string> _arguments = [];
        private string? _workingDirectory;
        private readonly Dictionary<string, string> _environmentVariables = [];
        private readonly Dictionary<string, string> _headers = [];
        private int _timeoutSeconds = 30;

        public McpServerConfigBuilder WithUrl(string url)
        {
            _url = url;
            return this;
        }

        public McpServerConfigBuilder WithTransportType(McpTransportType type)
        {
            _transportType = type;
            return this;
        }

        public McpServerConfigBuilder WithStdioTransport(string command, params string[] arguments)
        {
            _transportType = McpTransportType.Stdio;
            _command = command;
            _arguments.Clear();
            _arguments.AddRange(arguments);
            return this;
        }

        public McpServerConfigBuilder WithWorkingDirectory(string path)
        {
            _workingDirectory = path;
            return this;
        }

        public McpServerConfigBuilder WithEnvironmentVariable(string key, string value)
        {
            _environmentVariables[key] = value;
            return this;
        }

        /// <summary>
        /// Adds an HTTP header to the configuration (e.g. for Authentication).
        /// </summary>
        public McpServerConfigBuilder WithHeader(string key, string value)
        {
            _headers[key] = value;
            return this;
        }

        public McpServerConfigBuilder WithTimeout(int seconds)
        {
            _timeoutSeconds = seconds;
            return this;
        }

        public McpServerConfig Build()
        {
            return new McpServerConfig
            {
                Name = _name,
                TransportType = _transportType,
                Url = _url,
                Command = _command,
                Arguments = _arguments,
                WorkingDirectory = _workingDirectory,
                EnvironmentVariables = _environmentVariables,
                Headers = _headers,
                TimeoutSeconds = _timeoutSeconds
            };
        }
    }
}
