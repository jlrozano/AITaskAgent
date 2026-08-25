using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.LLM.Tools.Implementation;
using GeminiLlmService;
using McpIntegration.Configuration;
using McpIntegration.Extensions;
using McpIntegration.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using OpenAILLmService;
using Serilog;
using System.Text.RegularExpressions;

namespace Samples.McpIntegrationDemo;

/// <summary>
/// Demo application that demonstrates MCP integration with AITaskAgent framework.
/// Connects to kukapay MCP test server and executes tools.
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        // Configure Serilog from appsettings.json
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .Build();

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
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

        var baseDir = AppContext.BaseDirectory;

        // Build configuration
        var jsonPath = Path.Combine(baseDir, "appsettings.json");
        var jsonContent = File.ReadAllText(jsonPath);

        // Expand ${VARIABLE} placeholders
        jsonContent = Regex.Replace(jsonContent, @"\${(\w+)}", m => 
            Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);

        using var jsonStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(jsonContent));

        var config = new ConfigurationBuilder()
            .AddJsonStream(jsonStream)
            .AddEnvironmentVariables()
            .Build();

        // Setup DI
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(builder => builder.AddSerilog());

        // Register LLM services
        services.AddSingleton<OpenAILLmService.OpenAILlmService>();
        services.AddSingleton<GeminiLlmService.GeminiLlmService>();

        // Register them as ILlmService
        services.AddSingleton<ILlmService>(sp => sp.GetRequiredService<OpenAILLmService.OpenAILlmService>());
        services.AddSingleton<ILlmService>(sp => sp.GetRequiredService<GeminiLlmService.GeminiLlmService>());

        services.AddAITaskAgent();
        
        // Setup Tools
        var toolsRegistry = AvailableTools.Instance;
        services.AddSingleton<ITools>(toolsRegistry);

        var sp = services.BuildServiceProvider();
        sp.UseAITaskAgent();


        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<McpToolProvider>();

        // Read MCP config
        var serverConfig = new McpServerConfig
        {
            Name = config["McpServer:Name"] ?? "TestServer",
            TransportType = Enum.Parse<McpTransportType>(
                config["McpServer:TransportType"] ?? "StreamableHttp"),
            Url = config["McpServer:Url"],
            TimeoutSeconds = int.Parse(config["McpServer:TimeoutSeconds"] ?? "30"),
            Headers = config.GetSection("McpServer:Headers")
                .GetChildren()
                .ToDictionary(x => x.Key, x => x.Value!)
        };

        Console.WriteLine($"📡 Connecting to MCP server: {serverConfig.Name}");
        Console.WriteLine($"   URL: {serverConfig.Url}");
        Console.WriteLine();

        await using var mcpProvider = new McpToolProvider(serverConfig, logger);

        try
        {
            await mcpProvider.ConnectAsync();
            Console.WriteLine("✅ Connected to MCP!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Connection failed: {ex.Message}");
            return;
        }

        // Register MCP tools
        var registeredCount = await toolsRegistry.RegisterMcpToolsAsync(mcpProvider);
        Console.WriteLine($"✅ Registered {registeredCount} tool(s) in ToolRegistry");
        Console.WriteLine();

        // --- Execute with OpenRouter ---
        await ExecuteDemoWithLlmAsync("OpenRouter", "openrouter-role", sp);

        // --- Execute with Gemini ---
        await ExecuteDemoWithLlmAsync("Gemini", "gemini-role", sp);
    }

    private static async Task ExecuteDemoWithLlmAsync(string name, string roleName, IServiceProvider sp)
    {
        Console.WriteLine($"═══════════════════════════════════════════════════════════════");
        Console.WriteLine($"  Executing with {name} (Role: {roleName})");
        Console.WriteLine($"═══════════════════════════════════════════════════════════════");

        var env = sp.GetRequiredService<IOptions<LlmEnvironment>>().Value;
        if (!env.Roles.TryGetValue(roleName, out var role))
        {
            Console.WriteLine($"❌ Role {roleName} not found!");
            return;
        }

        var llmService = role.Provider?.Service ?? throw new InvalidOperationException("Role provider service not initialized");
        var conversation = new ConversationContext();
        conversation.AddUserMessage("Use the 'calculate_sum' tool to add the numbers [10, 20, 30, 40, 50]. Then tell me the result.");

        var toolNames = new[] { "calculate_sum" };
        var tools = sp.GetRequiredService<ITools>().GetTools(toolNames);
        var toolDefinitions = tools.Select(t => t.GetDefinition()).ToList();

        var request = new LlmRequest
        {
            Conversation = conversation,
            Model = role.Model,
            BaseUrl = role.Provider.BaseUrl,
            ApiKey = role.Provider.ApiKey,
            Tools = toolDefinitions,
            Temperature = role.Inference?.Temperature ?? 0.7f
        };

        try
        {
            Console.WriteLine("  Sending request...");
            var response = await llmService.InvokeAsync(request);
            
            Console.WriteLine($"  ✅ Response: {response.Content}");
            
            if (response.ToolCalls != null && response.ToolCalls.Count > 0)
            {
                Console.WriteLine($"  🛠️ Tool calls: {response.ToolCalls.Count}");
                foreach (var toolCall in response.ToolCalls)
                {
                    Console.WriteLine($"    - {toolCall.Name}({toolCall.Arguments})");
                    
                    // Optional: Execute the tool
                    var tool = tools.FirstOrDefault(t => t.Name == toolCall.Name);
                    if (tool != null)
                    {
                        var toolResult = await tool.ExecuteAsync(
                            toolCall.Arguments,
                            new PipelineContext(),
                            "demo-step",
                            sp.GetRequiredService<ILoggerFactory>().CreateLogger("ToolExecutor"));
                        
                        Console.WriteLine($"      ↪ Tool Result: {toolResult}");
                        
                        // Add to conversation and follow up
                        conversation.AddAssistantMessageWithToolCalls(new[] { toolCall });
                        conversation.AddToolMessage(toolCall.Id, toolResult);
                        
                        Console.WriteLine("  Sending follow-up request...");
                        var followUpResponse = await llmService.InvokeAsync(request with { Conversation = conversation });
                        Console.WriteLine($"  ✅ Follow-up Response: {followUpResponse.Content}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ Failed: {ex.Message}");
            if (ex.InnerException != null)
                Console.WriteLine($"     Inner: {ex.InnerException.Message}");
        }

        Console.WriteLine();
    }
}
