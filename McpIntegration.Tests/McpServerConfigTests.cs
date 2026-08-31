using McpIntegration.Configuration;

namespace McpIntegration.Tests;

/// <summary>
/// Validate() is the only guard against building a transport that can never connect
/// (a Stdio server with no Command, an Http server with no Url) - it runs before any
/// network/process I/O is attempted.
/// </summary>
public class McpServerConfigTests
{
    [Fact]
    public void Validate_throws_when_name_is_missing()
    {
        var config = new McpServerConfig
        {
            Name = "  ",
            TransportType = McpTransportType.Stdio,
            Command = "npx"
        };

        Assert.Throws<InvalidOperationException>(config.Validate);
    }

    [Fact]
    public void Validate_throws_for_stdio_without_command()
    {
        var config = new McpServerConfig
        {
            Name = "local",
            TransportType = McpTransportType.Stdio
        };

        Assert.Throws<InvalidOperationException>(config.Validate);
    }

    [Fact]
    public void Validate_passes_for_stdio_with_command()
    {
        var config = new McpServerConfig
        {
            Name = "local",
            TransportType = McpTransportType.Stdio,
            Command = "npx"
        };

        var exception = Record.Exception(config.Validate);

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(McpTransportType.StreamableHttp)]
    [InlineData(McpTransportType.Sse)]
    public void Validate_throws_for_http_based_transport_without_url(McpTransportType transport)
    {
        var config = new McpServerConfig
        {
            Name = "remote",
            TransportType = transport
        };

        Assert.Throws<InvalidOperationException>(config.Validate);
    }

    [Theory]
    [InlineData(McpTransportType.StreamableHttp)]
    [InlineData(McpTransportType.Sse)]
    public void Validate_passes_for_http_based_transport_with_url(McpTransportType transport)
    {
        var config = new McpServerConfig
        {
            Name = "remote",
            TransportType = transport,
            Url = "https://example.com/mcp"
        };

        var exception = Record.Exception(config.Validate);

        Assert.Null(exception);
    }

    [Fact]
    public void TimeoutSeconds_defaults_to_thirty()
    {
        var config = new McpServerConfig
        {
            Name = "local",
            TransportType = McpTransportType.Stdio,
            Command = "npx"
        };

        Assert.Equal(30, config.TimeoutSeconds);
    }
}
