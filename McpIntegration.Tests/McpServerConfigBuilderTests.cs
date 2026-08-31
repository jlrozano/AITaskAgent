using McpIntegration.Configuration;
using McpIntegration.Extensions;

namespace McpIntegration.Tests;

using Builder = ServiceCollectionExtensions.McpServerConfigBuilder;

public class McpServerConfigBuilderTests
{
    [Fact]
    public void Build_defaults_to_streamable_http_with_thirty_second_timeout()
    {
        var config = new Builder("server").Build();

        Assert.Equal("server", config.Name);
        Assert.Equal(McpTransportType.StreamableHttp, config.TransportType);
        Assert.Equal(30, config.TimeoutSeconds);
        Assert.Null(config.Url);
        Assert.Null(config.Command);
    }

    [Fact]
    public void WithStdioTransport_switches_transport_and_replaces_arguments()
    {
        var config = new Builder("server")
            .WithUrl("https://example.com/mcp") // set first, then overridden by stdio below
            .WithStdioTransport("npx", "-y", "@scope/server")
            .Build();

        Assert.Equal(McpTransportType.Stdio, config.TransportType);
        Assert.Equal("npx", config.Command);
        Assert.Equal(["-y", "@scope/server"], config.Arguments);
    }

    [Fact]
    public void WithStdioTransport_called_twice_replaces_previous_arguments_instead_of_appending()
    {
        var config = new Builder("server")
            .WithStdioTransport("npx", "first")
            .WithStdioTransport("npx", "second")
            .Build();

        Assert.Equal(["second"], config.Arguments);
    }

    [Fact]
    public void WithHeader_and_WithEnvironmentVariable_accumulate_entries()
    {
        var config = new Builder("server")
            .WithHeader("Authorization", "Bearer token")
            .WithHeader("X-Custom", "value")
            .WithEnvironmentVariable("PATH", "/usr/bin")
            .Build();

        Assert.Equal(new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer token",
            ["X-Custom"] = "value"
        }, config.Headers);
        Assert.Equal(new Dictionary<string, string> { ["PATH"] = "/usr/bin" }, config.EnvironmentVariables);
    }

    [Fact]
    public void WithTimeout_overrides_the_default()
    {
        var config = new Builder("server").WithTimeout(120).Build();

        Assert.Equal(120, config.TimeoutSeconds);
    }

    [Fact]
    public void WithWorkingDirectory_is_carried_into_the_built_config()
    {
        var config = new Builder("server").WithWorkingDirectory("/srv/app").Build();

        Assert.Equal("/srv/app", config.WorkingDirectory);
    }
}
