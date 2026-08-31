using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Tools.Abstractions;
using McpIntegration.Abstractions;
using McpIntegration.Extensions;
using Microsoft.Extensions.Logging;

namespace McpIntegration.Tests;

/// <summary>
/// RegisterMcpToolsAsync is the glue between a connected MCP provider and the framework's
/// tool registry - both dependencies are interfaces here, so the connection guard and the
/// registration count can be verified without a real MCP server.
/// </summary>
public class ToolRegistryExtensionsTests
{
    private sealed class FakeTool(string name) : ITool
    {
        public string Name { get; } = name;
        public string Description => string.Empty;
        public ToolDefinition GetDefinition() => new() { Name = Name, Description = Description, ParametersJsonSchema = "{}" };
        public Task<string> ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, ILogger logger, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);
    }

    private sealed class FakeToolProvider(string serverName, bool isConnected, IReadOnlyList<ITool> tools) : IMcpToolProvider
    {
        public string ServerName { get; } = serverName;
        public bool IsConnected { get; } = isConnected;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<ITool>> GetToolsAsync(CancellationToken cancellationToken = default) => Task.FromResult(tools);
        public Task RefreshToolsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeToolRegistry : ITools
    {
        public List<ITool> Registered { get; } = [];
        public IReadOnlyList<ITool> GetAllTools(string? moduleName = null) => Registered;
        public ITool? GetTool(string name) => Registered.FirstOrDefault(t => t.Name == name);
        public IReadOnlyList<ITool> GetTools(params string[] names) => Registered.Where(t => names.Contains(t.Name)).ToList();
        public void Register(ITool tool) => Registered.Add(tool);
    }

    [Fact]
    public async Task RegisterMcpToolsAsync_throws_when_provider_is_not_connected()
    {
        var registry = new FakeToolRegistry();
        var provider = new FakeToolProvider("server", isConnected: false, tools: []);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.RegisterMcpToolsAsync(provider));
    }

    [Fact]
    public async Task RegisterMcpToolsAsync_registers_every_tool_from_a_connected_provider()
    {
        var registry = new FakeToolRegistry();
        var provider = new FakeToolProvider("server", isConnected: true, tools: [new FakeTool("a"), new FakeTool("b")]);

        var count = await registry.RegisterMcpToolsAsync(provider);

        Assert.Equal(2, count);
        Assert.Equal(["a", "b"], registry.Registered.Select(t => t.Name));
    }

    [Fact]
    public async Task RegisterMcpToolsAsync_for_multiple_providers_sums_the_counts_across_all_of_them()
    {
        var registry = new FakeToolRegistry();
        var providers = new IMcpToolProvider[]
        {
            new FakeToolProvider("one", isConnected: true, tools: [new FakeTool("a")]),
            new FakeToolProvider("two", isConnected: true, tools: [new FakeTool("b"), new FakeTool("c")])
        };

        var count = await registry.RegisterMcpToolsAsync(providers);

        Assert.Equal(3, count);
        Assert.Equal(["a", "b", "c"], registry.Registered.Select(t => t.Name));
    }

    [Fact]
    public async Task RegisterMcpToolsAsync_for_multiple_providers_stops_at_the_first_disconnected_one()
    {
        var registry = new FakeToolRegistry();
        var providers = new IMcpToolProvider[]
        {
            new FakeToolProvider("one", isConnected: true, tools: [new FakeTool("a")]),
            new FakeToolProvider("two", isConnected: false, tools: [new FakeTool("never-registered")])
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.RegisterMcpToolsAsync(providers));

        // The first provider's tool was already registered before the second one threw.
        Assert.Equal(["a"], registry.Registered.Select(t => t.Name));
    }
}
