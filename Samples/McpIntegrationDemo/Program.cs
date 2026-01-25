using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Tools.Implementation;
using McpIntegration.Configuration;
using McpIntegration.Extensions;
using McpIntegration.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Serilog;

namespace Samples.McpIntegrationDemo;

/// <summary>
/// Demo application that demonstrates MCP integration with AITaskAgent framework.
/// Connects to kukapay MCP test server and executes tools.
/// </summary>
public static class Program
{
    /// <summary>
    /// Entry point for the demo application.
    /// </summary>
    /// <param name="args">Command line arguments.</param>
    public static async Task Main(string[] args)
    {
        // Configure Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .CreateLogger();

        try
        {
            await RunDemoAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Demo failed with error");
            throw;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static async Task RunDemoAsync()
    {
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║           MCP Integration Demo - AITaskAgent Framework        ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // Build configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        // Setup DI
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSerilog());

        var serviceProvider = services.BuildServiceProvider();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<McpToolProvider>();

        // Read config manually (could also use Options pattern)
        var serverConfig = new McpServerConfig
        {
            Name = configuration["McpServer:Name"] ?? "TestServer",
            TransportType = Enum.Parse<McpTransportType>(
                configuration["McpServer:TransportType"] ?? "StreamableHttp"),
            Url = configuration["McpServer:Url"],
            TimeoutSeconds = int.Parse(configuration["McpServer:TimeoutSeconds"] ?? "30"),
            Headers = configuration.GetSection("McpServer:Headers")
                .GetChildren()
                .ToDictionary(x => x.Key, x => x.Value!)
        };

        Console.WriteLine($"📡 Connecting to MCP server: {serverConfig.Name}");
        Console.WriteLine($"   URL: {serverConfig.Url}");
        Console.WriteLine($"   Transport: {serverConfig.TransportType}");
        Console.WriteLine();

        // Create and connect provider
        await using var provider = new McpToolProvider(serverConfig, logger);

        try
        {
            await provider.ConnectAsync();
            Console.WriteLine("✅ Connected successfully!");
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Connection failed: {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("This might be because:");
            Console.WriteLine("  - The server is temporarily unavailable");
            Console.WriteLine("  - Network connectivity issues");
            Console.WriteLine("  - The server URL has changed");
            Console.WriteLine();

            // Try with SSE transport as fallback
            Console.WriteLine("🔄 Trying SSE transport as fallback...");
            var sseConfig = serverConfig with
            {
                TransportType = McpTransportType.Sse,
                Url = serverConfig.Url?.Replace("/api/mcp", "/api/sse")
            };

            await using var sseProvider = new McpToolProvider(sseConfig, logger);
            try
            {
                await sseProvider.ConnectAsync();
                Console.WriteLine("✅ Connected via SSE!");
                await DemoWithProvider(sseProvider, loggerFactory);
            }
            catch
            {
                Console.WriteLine("❌ SSE fallback also failed. Server might be down.");
            }
            return;
        }

        await DemoWithProvider(provider, loggerFactory);
    }

    private static async Task DemoWithProvider(
        McpToolProvider provider,
        ILoggerFactory loggerFactory)
    {
        var demoLogger = loggerFactory.CreateLogger("Demo");

        // ═══════════════════════════════════════════════════════════════════════
        // STEP 1: List available tools
        // ═══════════════════════════════════════════════════════════════════════
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  STEP 1: Listing Available Tools");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");

        var tools = await provider.GetToolsAsync();
        Console.WriteLine($"Found {tools.Count} tool(s):");
        Console.WriteLine();

        foreach (var tool in tools)
        {
            Console.WriteLine($"  🔧 {tool.Name}");
            Console.WriteLine($"     Description: {tool.Description}");

            var definition = tool.GetDefinition();
            Console.WriteLine($"     Schema: {definition.ParametersJsonSchema}");
            Console.WriteLine();
        }

        // ═══════════════════════════════════════════════════════════════════════
        // STEP 2: Execute calculate_sum tool
        // ═══════════════════════════════════════════════════════════════════════
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  STEP 2: Executing calculate_sum Tool");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");

        var sumTool = tools.FirstOrDefault(t =>
            t.Name.Equals("calculate_sum", StringComparison.OrdinalIgnoreCase));

        if (sumTool is not null)
        {
            var numbers = new[] { 1, 2, 3, 4, 5 };
            var argsJson = JsonConvert.SerializeObject(new { numbers });

            Console.WriteLine($"  Input: {string.Join(" + ", numbers)}");
            Console.WriteLine();

            // Create a minimal pipeline context for execution
            var context = new PipelineContext();

            try
            {
                var result = await sumTool.ExecuteAsync(
                    argsJson,
                    context,
                    "demo-step",
                    demoLogger);

                Console.WriteLine($"  ✅ Result: {result}");
                Console.WriteLine($"  Expected: {numbers.Sum()}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ Execution failed: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("  ⚠️ calculate_sum tool not found");
        }

        Console.WriteLine();

        // ═══════════════════════════════════════════════════════════════════════
        // STEP 3: Register tools in IToolRegistry
        // ═══════════════════════════════════════════════════════════════════════
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  STEP 3: Registering Tools in IToolRegistry");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");

        var registry = new ToolRegistry();
        var registeredCount = await registry.RegisterMcpToolsAsync(provider);

        Console.WriteLine($"  ✅ Registered {registeredCount} tool(s) in ToolRegistry");
        Console.WriteLine();

        // List registered tools
        var allRegisteredTools = registry.GetAllTools();
        Console.WriteLine("  Registered tools:");
        foreach (var tool in allRegisteredTools)
        {
            Console.WriteLine($"    - {tool.Name}");
        }

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  Demo completed successfully! 🎉");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine();
    }
}
