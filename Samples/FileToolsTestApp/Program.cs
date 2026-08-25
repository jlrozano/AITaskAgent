using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.FileTools;
using AITaskAgent.FileTools.Streaming;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Conversation.Storage;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Steps;
using AITaskAgent.LLM.Streaming;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.LLM.Tools.Implementation;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

using Serilog;
using System.Text.RegularExpressions;
using System.Text;

namespace FileToolsTestApp;

class Program
{
    static async Task Main(string[] args)
    {
        // 1. Setup paths
        var projectDir = AppContext.BaseDirectory;
        if (projectDir.Contains("bin"))
        {
            projectDir = Path.GetFullPath(Path.Combine(projectDir, "..", "..", ".."));
        }

        // Clean logs directory path (remove redundancy)
        var logsDir = Path.Combine(projectDir, "logs");
        Directory.CreateDirectory(logsDir);

        // 2. Clear old logs
        foreach (var file in Directory.GetFiles(logsDir))
        {
            try { File.Delete(file); } catch { }
        }

        // 3. Load configuration
        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development";
        
        // Expansion of environment variables ${VAR} in appsettings.json
        var jsonConfig = File.ReadAllText("appsettings.json");
        jsonConfig = Regex.Replace(jsonConfig, @"\$\{(\w+)\}", m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);
        using var configStream = new MemoryStream(Encoding.UTF8.GetBytes(jsonConfig));

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonStream(configStream)
            .AddEnvironmentVariables()
            .Build();

        // 4. Configure Serilog for MAX VERBOSITY
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose() // Serilog Verbose = Trace
            .Enrich.FromLogContext()
            .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information)
            .WriteTo.File(
                Path.Combine(logsDir, "trace.log"),
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1))
            .CreateLogger();

        // 5. Setup Dependency Injection
        var services = new ServiceCollection();
        services.AddLogging(builder => 
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: true);
            builder.SetMinimumLevel(LogLevel.Trace);
        });
        services.AddSingleton<IConfiguration>(configuration);

        // 6. Register AITaskAgent and FileTools
        services.AddAITaskAgent();
        services.AddFileTools();

        // Register OpenAI specifically
        services.AddSingleton<ILlmService, OpenAILLmService.OpenAILlmService>();
        services.AddSingleton<OpenAILLmService.OpenAILlmService>();

        var serviceProvider = services.BuildServiceProvider();
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

        // 7. Initialize Framework
        serviceProvider.UseAITaskAgent();

        // Register tools in AvailableTools instance
        var allTools = serviceProvider.GetServices<ITool>().ToList();
        foreach (var t in allTools)
        {
            AvailableTools.Instance.Register(t);
            logger.LogTrace("Registered tool: {ToolName}", t.Name);
        }

        // Start event listener for console feedback
        var eventChannel = serviceProvider.GetRequiredService<IEventChannel>();
        _ = Task.Run(async () =>
        {
            try
            {
                var subscription = eventChannel.Subscribe();
                await foreach (var ev in subscription.ReadAllAsync())
                {
                    if (ev.SuppressFromUser) continue;

                    switch (ev)
                    {
                        case TagStartedEvent ts:
                            Console.WriteLine($"\n📝 [{ts.TagName}] Iniciando... ");
                            break;
                        case TagCompletedEvent tc:
                            if (!tc.Success) Console.WriteLine($"\n❌ [{tc.TagName}] Error: {tc.ErrorMessage}");
                            break;
                        case LlmResponseEvent resp when resp.FinishReason == FinishReason.Streaming:
                            Console.Write(resp.Content);
                            break;
                        case ToolStartedEvent tool:
                            Console.WriteLine($"\n🛠️  [Tool: {tool.ToolName}]");
                            break;
                        case ToolCompletedEvent toolComp:
                            if (!toolComp.Success) Console.WriteLine($"\n❌ [Tool: {toolComp.ToolName}] Error: {toolComp.ErrorMessage}");
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger.LogError(ex, "Event loop error"); }
        });

        // 8. Manual Hydration
        var configSection = configuration.GetSection("AITaskAgent:LlmProviders");
        var rawEnv = new LlmEnvironment();
        configSection.Bind(rawEnv); 

        var openAiService = serviceProvider.GetRequiredService<ILlmService>();
        foreach (var kvp in rawEnv.Providers)
        {
            typeof(LlmProvider).GetProperty("Service")?.SetValue(kvp.Value, openAiService);
        }

        var rolesSection = configSection.GetSection("Roles");
        foreach (var roleKvp in rawEnv.Roles)
        {
            var r = roleKvp.Value;
            var providerName = rolesSection.GetSection(roleKvp.Key).GetValue<string>("Provider");
            if (!string.IsNullOrEmpty(providerName) && rawEnv.Providers.TryGetValue(providerName, out var p))
            {
                typeof(LlmRole).GetProperty("Provider")?.SetValue(r, p);
            }
            else if (rawEnv.Providers.Count > 0)
            {
                 typeof(LlmRole).GetProperty("Provider")?.SetValue(r, rawEnv.Providers.Values.First());
            }
        }

        // 9. Shared State
        var conversation = new ConversationContext();
        var handlers = new List<IStreamingTagHandler>
        {
            new WriteFileTagHandler(projectDir, serviceProvider.GetRequiredService<ILogger<WriteFileTagHandler>>())
        };

        // 11. Define Agent Step
        var defaultProvider = rawEnv.Providers.Values.FirstOrDefault() 
            ?? new LlmProvider { Service = openAiService };

        var role = rawEnv.Roles.GetValueOrDefault("default") 
            ?? rawEnv.Roles.GetValueOrDefault(rawEnv.DefaultRole ?? string.Empty)
            ?? new LlmRole { Provider = defaultProvider, Model = "gpt-4o" };

        var agentStep = new TemplateLlmStep<IStepResult, LlmStringStepResult>(
            name: "FileAgent",
            role: role,
            templateProvider: null,
            tools: allTools.Where(t => t.Name != "write_to_file").Select(t => t.Name).ToList(),
            promtTemplateString: "{{input}}", // CRITICAL: TinyTemplate uses lowercase for property access
            systemTemplateString: $"""
                Eres un experto asistente de archivos. Tu objetivo es ayudar al usuario a gestionar sus archivos en el directorio: {projectDir}
                
                REGLAS CRÍTICAS:
                1. Para crear o modificar archivos, DEBES usar el tag <write_file>.
                2. FORMATO EXACTO DEL TAG: <write_file path="nombre_archivo.txt">Contenido</write_file>
                   - Usa SIEMPRE comillas DOBLES (") para el path.
                   - NO pongas espacios extra dentro de los corchetes del tag.
                3. Para listar, leer o buscar, USA las herramientas (tools) disponibles.
                4. Si el usuario te pide listar archivos, USA la herramienta 'list_dir'.
                5. Responde siempre en español de forma concisa.
                """
        )
        {
            MaxToolIterations = 500,
            StreamingHandlers = handlers
        };

        Console.WriteLine("========================================");
        Console.WriteLine("    AITaskAgent Interactive Demo (TRACE)");
        Console.WriteLine("========================================");
        Console.WriteLine($"Project: {projectDir}");
        Console.WriteLine($"Model:   {role.Model}");
        Console.WriteLine($"Logs:    {Path.Combine(logsDir, "trace.log")}");
        Console.WriteLine($"Tools:   {string.Join(", ", allTools.Select(t => t.Name))}");
        Console.WriteLine("----------------------------------------");
        Console.WriteLine("Escribe 'salir' o 'exit' para terminar.\n");

        Console.WriteLine("[Agente]: Hola, ¿en qué puedo ayudarte hoy con tus archivos?\n");

        while (true)
        {
            Console.Write("[Usuario]: ");
            var input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input) || input.ToLower() is "salir" or "exit")
                break;

            var turnContext = new PipelineContext(eventChannel, serviceProvider) 
            { 
                Conversation = conversation 
            };

            try
            {
                logger.LogTrace("Executing step with input: {Input}", input);
                var stepInput = new LlmStringStepResult(agentStep) { Content = input };
                var result = await ((IStep)agentStep).ExecuteAsync(stepInput, turnContext, 1, null, CancellationToken.None);
                
                if (result.HasError)
                {
                    Console.WriteLine($"\n[ERROR]: {result.Error?.Message}");
                }
                else
                {
                    Console.WriteLine();
                }
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Fatal error in loop");
                Console.WriteLine($"\n[FATAL ERROR]: {ex.Message}");
            }

            Console.WriteLine();
        }

        Console.WriteLine("Demo terminada.");
    }
}
