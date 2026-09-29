using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AITaskAgent.Core.Models;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using McpIntegration.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpIntegration.Tests;

/// <summary>
/// McpToolWrapper.ExecuteAsync used to read only CallToolResult.Content and ignore IsError,
/// so an MCP server reporting a tool-level failure was still emitted as
/// ToolCompletedEvent { Success = true }. These tests drive the wrapper against a fake
/// ITransport that answers the MCP handshake and a scripted tools/call response, so the
/// assertions exercise the real McpClient wire path rather than a hand-built CallToolResult.
/// </summary>
public class McpToolWrapperTests
{
    /// <summary>
    /// Minimal MCP client transport: answers "initialize" so McpClient.CreateAsync completes,
    /// and answers "tools/call" with whatever CallToolResult the test configured. Every other
    /// request (e.g. tools/list) gets an empty object result, which is enough because the
    /// wrapper is constructed directly with a <see cref="Tool"/> rather than discovered via
    /// ListToolsAsync.
    /// </summary>
    private sealed class FakeMcpTransport : ITransport
    {
        private readonly Channel<JsonRpcMessage> _channel = Channel.CreateUnbounded<JsonRpcMessage>();

        public CallToolResult ToolCallResult { get; set; } = new() { Content = [] };

        public string? SessionId => "test-session";

        public ChannelReader<JsonRpcMessage> MessageReader => _channel.Reader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            if (message is JsonRpcRequest request)
            {
                JsonNode? result = request.Method switch
                {
                    RequestMethods.Initialize => JsonSerializer.SerializeToNode(new InitializeResult
                    {
                        ProtocolVersion = "2025-06-18",
                        Capabilities = new ServerCapabilities(),
                        ServerInfo = new Implementation { Name = "fake-server", Version = "1.0.0" }
                    }, McpJsonUtilities.DefaultOptions),
                    RequestMethods.ToolsCall => JsonSerializer.SerializeToNode(ToolCallResult, McpJsonUtilities.DefaultOptions),
                    _ => JsonSerializer.SerializeToNode(new { }, McpJsonUtilities.DefaultOptions)
                };

                _channel.Writer.TryWrite(new JsonRpcResponse { Id = request.Id, Result = result! });
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeClientTransport(FakeMcpTransport transport) : IClientTransport
    {
        public string Name => "fake";

        public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ITransport>(transport);
    }

    /// <summary>
    /// Captures every event sent through the PipelineContext so tests can assert on the
    /// ToolCompletedEvent the wrapper emits.
    /// </summary>
    private sealed class RecordingEventChannel : IEventChannel
    {
        public List<IProgressEvent> Events { get; } = [];

        public Task SendAsync<TEvent>(TEvent progressEvent, CancellationToken cancellationToken = default)
            where TEvent : IProgressEvent
        {
            Events.Add(progressEvent);
            return Task.CompletedTask;
        }

        public IDisposable Subscribe(Func<IProgressEvent, CancellationToken, Task> handler, string? correlationId = null) =>
            throw new NotSupportedException("Not used by these tests.");

        public IEventSubscription CreateSubscription(int capacity = 100, string? correlationId = null) =>
            throw new NotSupportedException("Not used by these tests.");
    }

    private static async Task<McpClient> CreateClientAsync(FakeMcpTransport transport) =>
        await McpClient.CreateAsync(new FakeClientTransport(transport));

    [Fact]
    public async Task ExecuteAsync_throws_and_reports_failure_when_the_mcp_result_has_IsError_true()
    {
        var transport = new FakeMcpTransport
        {
            ToolCallResult = new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "boom: the downstream call failed" }]
            }
        };
        var client = await CreateClientAsync(transport);
        var wrapper = new McpToolWrapper(new Tool { Name = "failing_tool" }, client, "test-server");
        var eventChannel = new RecordingEventChannel();
        var context = new PipelineContext(eventChannel);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => wrapper.ExecuteAsync(
            "{}", context, "step", NullLogger.Instance, CancellationToken.None));

        Assert.Contains("boom: the downstream call failed", ex.Message);

        var completed = Assert.Single(eventChannel.Events.OfType<ToolCompletedEvent>());
        Assert.False(completed.Success);
        Assert.Equal("boom: the downstream call failed", completed.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_reports_success_when_the_mcp_result_has_no_error()
    {
        var transport = new FakeMcpTransport
        {
            ToolCallResult = new CallToolResult
            {
                IsError = false,
                Content = [new TextContentBlock { Text = "all good" }]
            }
        };
        var client = await CreateClientAsync(transport);
        var wrapper = new McpToolWrapper(new Tool { Name = "ok_tool" }, client, "test-server");
        var eventChannel = new RecordingEventChannel();
        var context = new PipelineContext(eventChannel);

        var result = await wrapper.ExecuteAsync("{}", context, "step", NullLogger.Instance, CancellationToken.None);

        Assert.Equal("all good", result);

        var completed = Assert.Single(eventChannel.Events.OfType<ToolCompletedEvent>());
        Assert.True(completed.Success);
        Assert.Null(completed.ErrorMessage);
    }
}
