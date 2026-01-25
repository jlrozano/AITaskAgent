using AITaskAgent.Configuration;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.FileTools;
using AITaskAgent.FileTools.Streaming;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.LLM.Streaming;
using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

using Serilog;
using Serilog.Events;

namespace FileToolsTestApp;

class Program
{
    static async Task Main(string[] args)
    {
        // 1. Setup Project Directory and Paths
        var projectDir = "C:\\Users\\juan.rozano\\Desktop\\AgenteAI\\AITaskAgentFramework";
        var logsDir = Path.Combine(projectDir, "Samples", "FileToolsTestApp", "logs");

        // Clean up logs from previous run
        if (Directory.Exists(logsDir))
        {
            try
            {
                Directory.Delete(logsDir, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not clear logs: {ex.Message}");
            }
        }
        Directory.CreateDirectory(logsDir);

        // 3. Load configuration
        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development";
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)

            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(logsDir, "app.log"),
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1))
            .WriteTo.File(
                formatter: new YamlLogFormatter(),
                path: Path.Combine(logsDir, "app.yaml"),
                rollingInterval: RollingInterval.Day,
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1))
            .CreateLogger();

        // 4. Setup DI
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: true);
        });

        services.AddAITaskAgent(configuration);
        services.AddFileTools();
        FileToolsConfiguration.RootDirectory = projectDir;
        services.AddSingleton<ILlmService, OpenAILLmService.OpenAILlmService>();

        var serviceProvider = services.BuildServiceProvider();

        // Initialize Pipeline logger factory to enable logging in Steps
        AITaskAgent.Core.Execution.Pipeline.LoggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();

        // 5. Run Demo Loopgging (jsonl)
        var eventChannel = serviceProvider.GetRequiredService<IEventChannel>() as EventChannel;
        if (eventChannel == null)
        {
            Console.WriteLine("Warning: EventChannel not found or wrong type.");
            return;
        }
        var eventsFile = Path.Combine(logsDir, "events.jsonl");

        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            var reader = eventChannel.Subscribe();
            try
            {
                await foreach (var evt in reader.ReadAllAsync(cts.Token))
                {
                    var json = JsonConvert.SerializeObject(evt);
                    await File.AppendAllTextAsync(eventsFile, json + Environment.NewLine, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });

        // 8. Get FileTools
        var llmProviderResolver = serviceProvider.GetRequiredService<ILlmProviderResolver>();
        var llmService = serviceProvider.GetRequiredService<ILlmService>();
        var allTools = serviceProvider.GetServices<ITool>().ToList();

        // 9. Create shared Conversation (persists across all turns)
        // We create an initial context just to get the Conversation instance
        var initialContext = new PipelineContext(eventChannel);
        var conversation = initialContext.Conversation;

        // 10. Configure Pipeline timeout for complex operations
        Pipeline.DefaultStepTimeout = TimeSpan.FromMinutes(30);

        // CREATE STREAMING HANDLERS
        var handlers = new List<IStreamingTagHandler>
        {
            new WriteFileTagHandler(projectDir, serviceProvider.GetRequiredService<ILogger<WriteFileTagHandler>>())
        };

        // 11. Define Agent Step
        // The promptBuilder extracts the user message from input.Value
        // BaseLlmStep.ConfigureJsonResponse then adds this message to the Conversation automatically
        var profile = llmProviderResolver.GetProvider();
        var agentStep = new LlmStep(
            llmService,
            name: "FileAgent",
            profile: profile,
            promptBuilder: (input, context) => Task.FromResult(input.Value?.ToString() ?? ""),
            systemPrompt: $"""
                Eres un asistente experto en sistemas de archivos. 
                Tu directorio de trabajo autorizado es: {projectDir}
                Solo puedes operar dentro de este directorio.

                Responde siempre en español.
                """,
            tools: allTools.Where(t => t.Name != "write_to_file").ToList(),
            maxToolIterations: 500,
            streamingHandlers: handlers
        );

        Console.Clear();
        Console.WriteLine("========================================");
        Console.WriteLine("    AITaskAgent Interactive Demo");
        Console.WriteLine("========================================");
        Console.WriteLine($"Project: {projectDir}");
        Console.WriteLine($"Model:   {profile.Model}");
        Console.WriteLine($"Logs:    {logsDir}");
        Console.WriteLine($"Tools:   {string.Join(", ", allTools.Select(t => t.Name))}");
        Console.WriteLine("----------------------------------------");
        Console.WriteLine("Escribe 'salir' o 'exit' para terminar.");

        // Initial Greeting
        Console.WriteLine("\n[Agente]: Hola, ¿en qué puedo ayudarte hoy con tus archivos?");

        while (true)
        {
            Console.Write("\n[Usuario]: ");
            var userInput = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(userInput) ||
                userInput.Equals("salir", StringComparison.OrdinalIgnoreCase) ||
                userInput.Equals("exit", StringComparison.OrdinalIgnoreCase))
                break;

            try
            {
                // Create NEW PipelineContext for each turn (new CorrelationId)
                // but reuse the SAME Conversation to maintain history
                var turnContext = new PipelineContext(eventChannel)
                {
                    Conversation = conversation  // ← Reuse the shared conversation
                };
                var turnCorrelationId = turnContext.CorrelationId;

                var inputResult = new SuccessStepResult(new EmptyStep("UserInput"), userInput);

                // Subscribe to streaming events for this turn (display in real-time)
                var streamingCts = new CancellationTokenSource();
                var isThinking = false;
                var hasContent = false;

                var streamingReader = eventChannel.Subscribe();
                var streamingTask = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var evt in streamingReader.ReadAllAsync(streamingCts.Token))
                        {
                            // Only process events for this turn
                            if (evt.CorrelationId != turnCorrelationId)
                                continue;

                            // Handle Progress Events (tool execution)
                            if (evt is StepProgressEvent progressEvt && !progressEvt.SuppressFromUser)
                            {
                                Console.ForegroundColor = ConsoleColor.DarkGray;
                                Console.WriteLine($"  {progressEvt.Message}");
                                Console.ResetColor();
                            }

                            // Handle Tag Started Events (streaming tag execution)
                            if (evt is TagStartedEvent tagStarted)
                            {
                                Console.ForegroundColor = ConsoleColor.Cyan;
                                var path = tagStarted.Attributes?.GetValueOrDefault("path") ?? "";
                                Console.WriteLine($"  📝 [{tagStarted.TagName}] Iniciando... {path}");
                                Console.ResetColor();
                            }

                            // Handle Tag Completed Events
                            if (evt is TagCompletedEvent tagCompleted)
                            {
                                Console.ForegroundColor = tagCompleted.Success ? ConsoleColor.Green : ConsoleColor.Red;
                                var status = tagCompleted.Success ? "✅" : "❌";
                                Console.WriteLine($"  {status} [{tagCompleted.TagName}] Completado ({tagCompleted.Duration.TotalMilliseconds:F0}ms)");
                                Console.ResetColor();
                            }

                            // Handle LLM Streaming Events
                            if (evt is LlmResponseEvent llmEvt && llmEvt.FinishReason == FinishReason.Streaming)
                            {
                                // First content? Print prefix
                                if (!hasContent)
                                {
                                    Console.Write("\n[Agente]: ");
                                    hasContent = true;
                                }

                                // Handle thinking state transitions
                                if (llmEvt.IsThinking && !isThinking)
                                {
                                    isThinking = true;
                                    Console.ForegroundColor = ConsoleColor.Magenta;
                                    Console.Write("<think>");
                                }
                                else if (!llmEvt.IsThinking && isThinking)
                                {
                                    isThinking = false;
                                    Console.Write("</think>\n");
                                    Console.ResetColor();
                                }

                                // Print content with appropriate color
                                if (llmEvt.IsThinking)
                                {
                                    Console.ForegroundColor = ConsoleColor.Magenta;
                                }
                                else
                                {
                                    Console.ResetColor();
                                }
                                Console.Write(llmEvt.Content);
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    finally
                    {
                        if (isThinking)
                        {
                            Console.Write("</think>\n");
                        }
                        Console.ResetColor();
                    }
                }, streamingCts.Token);

                // Execute via Pipeline with the new context
                var result = await Pipeline.ExecuteAsync(
                    "FileChatPipeline",
                    [agentStep],
                    inputResult,
                    turnContext
                );

                // Stop streaming display
                await streamingCts.CancelAsync();
                try { await streamingTask; } catch (OperationCanceledException) { }

                // Handle errors (streaming already showed success content)
                if (result.HasError)
                {
                    var errorMessage = result.Error?.Message ?? "Unknown error";

                    // Detect timeout errors
                    if (errorMessage.Contains("canceled", StringComparison.OrdinalIgnoreCase) ||
                        result.Error is OperationCanceledException)
                    {
                        var timeoutMessage = $"⏱️ La operación excedió el tiempo límite de {Pipeline.DefaultStepTimeout.TotalMinutes} minutos y fue cancelada.";
                        Console.WriteLine($"\n[ERROR]: {timeoutMessage}");
                        Log.Warning("Step timeout: {ErrorMessage}", errorMessage);
                    }
                    else
                    {
                        Console.WriteLine($"\n[ERROR]: {errorMessage}");
                        Log.Error("Step execution failed: {ErrorMessage}. Error details: {ErrorDetails}",
                            errorMessage, result.Error?.ToString() ?? "No details");
                    }
                }
                else
                {
                    // If no streaming content was shown, display the final result
                    if (!hasContent)
                    {
                        var finalContent = result.Value?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(finalContent))
                        {
                            Console.WriteLine($"\n[Agente]: {finalContent}");
                        }
                    }
                    else
                    {
                        // Just add a newline after streaming content
                        Console.WriteLine();
                    }
                }
            }
            catch (OperationCanceledException ex)
            {
                var timeoutMessage = $"⏱️ La operación excedió el tiempo límite de {Pipeline.DefaultStepTimeout.TotalMinutes} minutos y fue cancelada.";
                Console.WriteLine($"\n[ERROR]: {timeoutMessage}");
                Log.Warning(ex, "Step timeout occurred");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[CRITICAL ERROR]: {ex.Message}");
                Log.Error(ex, "Error during agent execution");
            }
        }

        Console.WriteLine("\n========================================");
        Console.WriteLine("   Demo terminada");
        Console.WriteLine("========================================");

        await cts.CancelAsync();
        if (serviceProvider is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
        else serviceProvider.Dispose();

        await Log.CloseAndFlushAsync();
    }

    static string? FindProjectDir(string startDir)
    {
        var current = new DirectoryInfo(startDir);
        while (current != null)
        {
            if (current.GetFiles("*.csproj").Length != 0)
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        return null;
    }
}
