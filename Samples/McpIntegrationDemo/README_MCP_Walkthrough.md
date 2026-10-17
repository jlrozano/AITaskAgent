# Walkthrough: MCP Integration for AITaskAgent Framework

This document outlines the successful integration of the **Model Context Protocol (MCP)** into the **AITaskAgent Framework**, enabling the framework to consume tools from any MCP-compliant server (Stdio, HTTP, SSE).

## 1. Work Accomplished

### **New Library Project: `McpIntegration`**
A standalone library providing the core logic for MCP integration.
- **Provider Pattern**: Implemented `McpToolProvider` ([Source](../../McpIntegration/Providers/McpToolProvider.cs)) to manage server connections and lifecycles.
- **Tool Wrapper**: Created `McpToolWrapper` ([Source](../../McpIntegration/Tools/McpToolWrapper.cs)) to adapt MCP tools to the framework's `ITool` interface.
- **Configuration**: robust `McpServerConfig` with validation and fluent builder.
- **Transports**: Support for `Stdio`, `StreamableHttp`, and `Sse` transports using the official `ModelContextProtocol` SDK.

### **Demo Application: `Samples.McpIntegrationDemo`**
A console application demonstrating the integration in action.
- **Scenario**: Connects to the public Kukapay MCP test server (`https://test.kukapay.com/api/mcp`), configured in the `McpServer` section of `appsettings.json`.
- **Functionality**:
    1. Connects with the transport set in `McpServer:TransportType` (`StreamableHttp` in the shipped config; there is no automatic fallback to SSE).
    2. Registers the server's tools into the framework's tool registry (`AvailableTools.Instance`, `ITools`).
    3. For each LLM role (`openrouter-role`, then `gemini-role`), asks the model to add `[10, 20, 30, 40, 50]` with `calculate_sum`, executes the tool call it returns against the MCP server and sends a follow-up request with the result.

## 2. Validation Results

The integration was validated using the Demo application connecting to `https://test.kukapay.com/api/mcp`.

### **Execution Log Summary**
Log of an earlier version of the demo, which listed the tools and called `calculate_sum` directly; the current `Program.cs` drives the tool through the LLM roles instead.
```
[INFO] Connecting to MCP server KukapayTestServer using StreamableHttp transport
[INFO] Successfully connected to MCP server: KukapayTestServer
[INFO] Found 1 tools from MCP server KukapayTestServer: calculate_sum

STEP 1: Listing Available Tools
  🔧 calculate_sum
     Description: Calculate the sum of the given numbers.

STEP 2: Executing calculate_sum Tool
  Input: 1 + 2 + 3 + 4 + 5
  ✅ Result: Sum: 15
  Expected: 15

STEP 3: Registering Tools in IToolRegistry
  ✅ Registered 1 tool(s) in ToolRegistry
```

## 3. How to Use

### **1. Add Reference**
Add a reference to `McpIntegration` in your project.

### **2. Configure Service**
```csharp
// In your Program.cs or Startup - local server over stdio:
services.AddMcpToolProvider("MyMcpServer", builder => builder
    .WithStdioTransport("npx", "-y", "@modelcontextprotocol/server-everything"));

// OR a remote server (StreamableHttp is the default transport; WithTransportType(McpTransportType.Sse) for SSE):
services.AddMcpToolProvider("MyMcpServer", builder => builder
    .WithUrl("https://my-mcp-server.com/mcp")
    // For Authentication (API Key/Bearer):
    .WithHeader("Authorization", "Bearer my-token"));
```

`AddMcpToolProvider` registers a single `IMcpToolProvider` (`TryAddTransient`: a second call is ignored, and every resolution creates a new, unconnected provider).

### **3. Configuration via appsettings.json**
The demo reads its server settings, headers included, from an `McpServer` section that `Program.cs` maps by hand into an `McpServerConfig` (the library does not bind this section itself):
```json
"McpServer": {
  "Headers": {
    "Authorization": "Bearer your-api-key",
    "X-Api-Key": "your-key"
  }
}
```

### **4. Register Tools**
```csharp
await using var provider = serviceProvider.GetRequiredService<IMcpToolProvider>();
await provider.ConnectAsync(); // RegisterMcpToolsAsync throws if the provider is not connected
int count = await AvailableTools.Instance.RegisterMcpToolsAsync(provider); // extension on ITools
```

## 4. Technical Notes
- **SDK Version**: Integrated with `ModelContextProtocol` v0.5.0-preview.1.
- **JSON Compatibility**: Uses `System.Text.Json` internally for MCP compliance, while adapting to the framework's use of JSON strings.
