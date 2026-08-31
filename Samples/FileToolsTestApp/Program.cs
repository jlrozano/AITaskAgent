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
        
        // Reads a settings file next to the binary, expanding ${VAR} placeholders from the
        // environment. Returns null when the file is absent (environment overlays are optional).
        static MemoryStream? ReadSettings(string fileName)
        {
            var path = Path.Combine(AppContext.BaseDirectory, fileName);
            if (!File.Exists(path)) return null;

            var json = Regex.Replace(
                File.ReadAllText(path),
                @"\$\{(\w+)\}",
                m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);
            return new MemoryStream(Encoding.UTF8.GetBytes(json));
        }

        using var configStream = ReadSettings("appsettings.json")
            ?? throw new FileNotFoundException("appsettings.json not found next to the binary.");
        // The environment overlay carries the API keys and must win over the base file.
        using var envConfigStream = ReadSettings($"appsettings.{environmentName}.json");

        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonStream(configStream);

        if (envConfigStream != null)
        {
            configBuilder.AddJsonStream(envConfigStream);
        }

        var configuration = configBuilder
            .AddEnvironmentVariables()
            .Build();

        // 4. Configure Serilog for MAX VERBOSITY
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose() // Serilog Verbose = Trace
            .Enrich.FromLogContext()
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
        // Anchor every file tool to the source directory. Without a root the tools fall back to
        // the executable's directory, so a relative path would list the build output instead.
        // Extra directories can be granted at runtime with /addFolder.
        services.AddFileTools(projectDir);

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
        // Counts streamed content characters written this turn, so the loop knows whether the
        // answer already reached the console or still has to be printed.
        var streamedChars = new int[1];
        var eventChannel = serviceProvider.GetRequiredService<IEventChannel>();
        _ = Task.Run(async () =>
        {
            try
            {
                // Subscribe(handler) is now the callback form; CreateSubscription is the one that
                // exposes a reader for async enumeration.
                await using var subscription = eventChannel.CreateSubscription();
                await foreach (var ev in subscription.Reader.ReadAllAsync())
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
                        case LlmResponseEvent resp when resp.FinishReason == FinishReason.Streaming
                                                        && !resp.IsThinking:
                            Console.Write(resp.Content);
                            Interlocked.Add(ref streamedChars[0], resp.Content?.Length ?? 0);
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
            systemTemplateString: """
                Eres un experto asistente de archivos.

                Tu directorio principal de trabajo es: {{ context.mainpath }}
                Las rutas relativas (y un parametro de directorio vacio) se resuelven SIEMPRE contra ese
                directorio principal, nunca contra los demas.

                Ademas tienes permiso para acceder a estos otros directorios si lo necesitas:
                {{ context.otherpaths | json indent }}
                Para trabajar en ellos DEBES indicar su ruta absoluta en el parametro correspondiente;
                dejarlo vacio te devolvera al directorio principal.

                Si el usuario te pide trabajar sobre una ruta concreta, usa esa ruta tal cual, aunque
                sea distinta del directorio principal.
                
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

        // The system prompt is re-rendered on every request, so refreshing these variables is all
        // it takes for /setMainPath and /addFolder to be visible to the model.
        void RefreshPathVariables()
        {
            var main = FileToolsConfiguration.RootDirectory;
            agentStep.SetVariable("mainPath", main);
            agentStep.SetVariable(
                "otherPaths",
                FileToolsConfiguration.AllowedDirectories
                    .Where(d => !string.Equals(d, main, StringComparison.OrdinalIgnoreCase))
                    .ToList());
        }

        RefreshPathVariables();

        Console.WriteLine("========================================");
        Console.WriteLine("    AITaskAgent Interactive Demo (TRACE)");
        Console.WriteLine("========================================");
        Console.WriteLine($"Project: {projectDir}");
        Console.WriteLine($"Model:   {role.Model}");
        Console.WriteLine($"Logs:    {Path.Combine(logsDir, "trace.log")}");
        Console.WriteLine($"Tools:   {string.Join(", ", allTools.Select(t => t.Name))}");
        Console.WriteLine("----------------------------------------");
        Console.WriteLine("Comandos: /setMainPath <ruta>, /addFolder <ruta>, /folders, salir.\n");

        Console.WriteLine("[Agente]: Hola, ¿en qué puedo ayudarte hoy con tus archivos?\n");

        while (true)
        {
            Console.Write("[Usuario]: ");
            var input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input) || input.ToLower() is "salir" or "exit")
                break;

            if (input.StartsWith("/addFolder", StringComparison.OrdinalIgnoreCase))
            {
                var folder = input[("/addFolder".Length)..].Trim().Trim('"');

                if (string.IsNullOrWhiteSpace(folder))
                {
                    Console.WriteLine("[Sistema]: Uso: /addFolder <ruta>");
                }
                else if (!Directory.Exists(folder))
                {
                    Console.WriteLine($"[Sistema]: El directorio no existe: {folder}");
                }
                else
                {
                    var added = FileToolsConfiguration.AddAllowedDirectory(folder);
                    var resolved = Path.GetFullPath(folder);
                    RefreshPathVariables();
                    Console.WriteLine(added
                        ? $"[Sistema]: Directorio permitido: {resolved}"
                        : $"[Sistema]: Ya estaba permitido: {resolved}");
                    logger.LogInformation("Allowed directory added: {Directory} (new: {Added})", resolved, added);
                }

                Console.WriteLine();
                continue;
            }

            if (input.StartsWith("/setMainPath", StringComparison.OrdinalIgnoreCase))
            {
                var folder = input[("/setMainPath".Length)..].Trim().Trim('"');

                if (string.IsNullOrWhiteSpace(folder))
                {
                    Console.WriteLine("[Sistema]: Uso: /setMainPath <ruta>");
                }
                else if (!Directory.Exists(folder))
                {
                    Console.WriteLine($"[Sistema]: El directorio no existe: {folder}");
                }
                else
                {
                    // Becoming the primary root grants access on its own, so this replaces
                    // /addFolder followed by a second step.
                    var keptPrevious = FileToolsConfiguration.SetRootDirectory(folder);
                    var resolved = FileToolsConfiguration.RootDirectory;
                    RefreshPathVariables();

                    Console.WriteLine($"[Sistema]: Directorio principal: {resolved}");
                    if (keptPrevious != null)
                    {
                        Console.WriteLine($"[Sistema]: El anterior sigue permitido: {keptPrevious}");
                    }

                    logger.LogInformation(
                        "Main path set to {Directory} (previous kept: {Previous})",
                        resolved, keptPrevious ?? "(covered)");
                }

                Console.WriteLine();
                continue;
            }

            if (input.Equals("/folders", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("[Sistema]: Directorios permitidos:");
                var mainPath = FileToolsConfiguration.RootDirectory;
                foreach (var dir in FileToolsConfiguration.AllowedDirectories)
                {
                    var isMain = string.Equals(dir, mainPath, StringComparison.OrdinalIgnoreCase);
                    Console.WriteLine($"  - {dir}{(isMain ? "   (principal)" : "")}");
                }

                Console.WriteLine();
                continue;
            }

            var turnContext = new PipelineContext(eventChannel, serviceProvider) 
            { 
                Conversation = conversation 
            };

            try
            {
                Interlocked.Exchange(ref streamedChars[0], 0);
                logger.LogTrace("Executing step with input: {Input}", input);
                var stepInput = new LlmStringStepResult(agentStep) { Content = input };
                var result = await ((IStep)agentStep).ExecuteAsync(stepInput, turnContext, 1, null, CancellationToken.None);
                
                if (result.HasError)
                {
                    Console.WriteLine($"\n[ERROR]: {result.Error?.Message}");
                }
                else
                {
                    // The final answer arrives as FinishReason.Stop, which the event listener
                    // ignores, so print it here unless streaming already wrote it out.
                    var streamed = Interlocked.CompareExchange(ref streamedChars[0], 0, 0) > 0;
                    var answer = result.Value?.ToString();

                    if (!streamed && !string.IsNullOrWhiteSpace(answer))
                    {
                        Console.WriteLine($"\n[Agente]: {answer}");
                    }
                    else
                    {
                        Console.WriteLine();
                    }
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
        Log.CloseAndFlush();
    }
}
