using AITaskAgent.Configuration;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Tools.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using AITaskAgent.Core.Execution;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.Core.Abstractions;

namespace Samples.SimpleChatDemo;

public class Program
{
    public static async Task Main(string[] args)
    {
        var baseDir = AppContext.BaseDirectory;
        var config = new ConfigurationBuilder()
            .SetBasePath(baseDir)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        // 2. Setup Logging
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(config)
            .CreateLogger();

        // 3. Setup Dependency Injection
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(builder => builder.AddSerilog());

        // Register as ILlmService (multiple implementations allowed)
        services.AddSingleton<ILlmService,GeminiLlmService.GeminiLlmService>();
        services.AddSingleton<ILlmService,OpenAILLmService.OpenAILlmService>();

        // Register AITaskAgent Framework
        services.AddAITaskAgent();

        var sp = services.BuildServiceProvider();
        sp.UseAITaskAgent();



        // 4. Start Interactive Chat
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║           AITaskAgent - Interactive Simple Chat              ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine("Commands: /exit (quit), /role [name] (change LLM role), /clear (new chat)");
        Console.WriteLine("Try asking: 'What time is it?' or 'Explain how quantum computing works'.");
        Console.WriteLine();

        var env = sp.GetRequiredService<IOptions<LlmEnvironment>>().Value;
        var currentRoleName = "default"; // Use stable default role
        var conversation = new ConversationContext(logger: sp.GetRequiredService<ILogger<ConversationContext>>());
        var pipeline = sp.GetRequiredService<IPipeline>();
        
        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"[{currentRoleName}] User > ");
            Console.ResetColor();

            var input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input)) continue;

            // Command handling
            if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase)) break;

            if (input.StartsWith("/role ", StringComparison.OrdinalIgnoreCase))
            {
                var newRole = input[6..].Trim();
                if (env.Roles.ContainsKey(newRole))
                {
                    currentRoleName = newRole;
                    Console.WriteLine($"[System] Switched to role: {currentRoleName}");
                }
                else
                {
                    Console.WriteLine($"[Error] Role '{newRole}' not found in configuration.");
                    Console.WriteLine($"Available roles: {string.Join(", ", env.Roles.Keys)}");
                }
                continue;
            }

            if (input.Equals("/clear", StringComparison.OrdinalIgnoreCase))
            {
                conversation = new ConversationContext(logger: sp.GetRequiredService<ILogger<ConversationContext>>());
                Console.WriteLine("[System] Conversation history cleared.");
                continue;
            }

            // Process message
            if (!env.Roles.TryGetValue(currentRoleName, out var role))
            {
                Console.WriteLine($"[Error] Current role '{currentRoleName}' is missing.");
                continue;
            }

            // conversation.AddUserMessage(input);
            var context = PipelineContextFactory.Create(conversation);
       
            try
            { 
                var response = await pipeline.ExecuteAsync("Chat",[
                    new BaseLlmStep<LlmStringStepResult,LlmStringStepResult>("Chat",role, input) {
                        Tools = [new GetTimeTool()]
                    }
                ], input, context);
                
                if (response.HasError) {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[Error] {response.Error?.Message}");
                    if (response.Error?.OriginalException != null) Console.WriteLine($"Details: {response.Error.OriginalException.Message}");
                    Console.ResetColor();
                    continue;
                }
                
                if (response is LlmStepResult llmResponse && !string.IsNullOrEmpty(llmResponse.Reasoning))
                {
                    Console.WriteLine();
                    Console.BackgroundColor = ConsoleColor.DarkBlue;
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine("  THOUGHT PROCESS  ");
                    Console.ResetColor();
                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.WriteLine(llmResponse.Reasoning.Trim());
                    Console.WriteLine(new string('─', Console.WindowWidth - 1));
                    Console.ResetColor();
                }

                    
                // 3. Display Final Content
                
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("AI > ");
                Console.WriteLine();
                Console.ResetColor();
                Console.WriteLine(response.Value?.ToString());
                Console.WriteLine();
                
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[Error] {ex.Message}");
                if (ex.InnerException != null) Console.WriteLine($"        Details: {ex.InnerException.Message}");
                Console.ResetColor();
            }
        }
    }
}

/// <summary>
/// A simple tool that returns the current time.
/// </summary>
public class GetTimeTool : ITool
{
    public string Name => "get_current_time";
    public string Description => "Returns the current local time of the system.";
    public ToolDefinition GetDefinition() => new() { Name = Name, Description = Description, ParametersJsonSchema = "{\"type\": \"object\", \"properties\": {}}" };

    public Task<string> ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, Microsoft.Extensions.Logging.ILogger logger, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(DateTime.Now.ToString("HH:mm:ss"));
    }
}
