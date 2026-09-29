using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
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

        var logsDir = Path.Combine(projectDir, "logs");
        Directory.CreateDirectory(logsDir);

        // 2. One log file per run, previous runs kept. The evidence of a model misbehaving lives
        // in these files and used to be wiped by the very next start, before anyone read it.
        var logFile = Path.Combine(logsDir, $"trace_{DateTime.Now:yyyyMMdd_HHmmss}.log");

        // 3. Load configuration
        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development";

        // ${VAR} placeholders in AITaskAgent:LlmProviders (and any other section read via
        // GetEx/BindEx) are expanded automatically by the framework itself
        // (ConfigurationPatchExtensions.ExpandEnvVars) - no need to pre-process the raw JSON here.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            // The environment overlay carries the API keys and must win over the base file.
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        // 4. Configure Serilog for MAX VERBOSITY
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose() // Serilog Verbose = Trace
            .Enrich.FromLogContext()
            .WriteTo.File(
                logFile,
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

        // Register every provider's LLM service as ILlmService. AddAITaskAgent() below wires
        // ConfigureLlmEnvironmentOptions, which matches each appsettings.json provider's
        // ServiceType against these services' .Name (exact, then substring) and throws at
        // startup if a provider's ServiceType matches none of them - see SimpleChatDemo for the
        // same pattern with two services.
        services.AddSingleton<OpenAILLmService.OpenAILlmService>();
        services.AddSingleton<ILlmService>(sp => sp.GetRequiredService<OpenAILLmService.OpenAILlmService>());
        services.AddSingleton<ILlmService, OpenAILLmService.AzureOpenAILlmService>();
        services.AddSingleton<ILlmService, OpenAILLmService.OpenRouterLlmService>();
        services.AddSingleton<ILlmService, GeminiLlmService.GeminiLlmService>();

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
                        // Every FileTools tool already reports what it's doing via
                        // NotifyProgressAsync (e.g. "📄 Reading file 'X'") - nothing was listening
                        // for it, so it never reached the console.
                        case StepProgressEvent progress:
                            Console.WriteLine($"    {progress.Message}");
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger.LogError(ex, "Event loop error"); }
        });

        // 8. Resolve LLM environment (providers/roles) through the framework's own mechanism.
        // AddAITaskAgent() registered ConfigureLlmEnvironmentOptions, which builds LlmProvider and
        // LlmRole - both `required init` - with Service/Provider already set at construction, and
        // validates at startup (throws with a clear message if BaseUrl is missing, if a role names
        // a provider that does not exist, or if a provider's ServiceType matches no registered
        // ILlmService). No reflection needed to work around `init`, and no silent fallback to
        // "whichever provider happens to be first" on a name mismatch.
        var llmEnv = serviceProvider.GetRequiredService<IOptions<LlmEnvironment>>().Value;

        // 9. Shared State
        var conversation = new ConversationContext();
        var handlers = new List<ITagHandler>
        {
            new WriteFileTagHandler(serviceProvider.GetRequiredService<ILogger<WriteFileTagHandler>>())
        };

        // 11. Define Agent Step

        // Lets a run be pointed at any configured role (and therefore any provider/model) without
        // editing appsettings.json: a role name on the command line skips the prompt; otherwise,
        // with more than one role configured, ask which to use.
        string ChooseRoleName()
        {
            var requested = args.FirstOrDefault(a => !a.StartsWith('/'));
            if (requested != null && llmEnv.Roles.ContainsKey(requested))
            {
                return requested;
            }

            if (llmEnv.Roles.Count <= 1)
            {
                return llmEnv.DefaultRole;
            }

            var names = llmEnv.Roles.Keys.ToList();
            Console.WriteLine("Selecciona el modelo a usar:");
            for (var i = 0; i < names.Count; i++)
            {
                var isDefault = names[i].Equals(llmEnv.DefaultRole, StringComparison.OrdinalIgnoreCase);
                Console.WriteLine($"  {i + 1}. {names[i]}{(isDefault ? " (default)" : "")}");
            }
            Console.Write($"Opción [1-{names.Count}] (Enter = default): ");
            var input = Console.ReadLine()?.Trim();
            return int.TryParse(input, out var choice) && choice >= 1 && choice <= names.Count
                ? names[choice - 1]
                : llmEnv.DefaultRole;
        }

        var defaultProvider = llmEnv.Providers.Values.FirstOrDefault()
            ?? new LlmProvider { Service = serviceProvider.GetRequiredService<OpenAILLmService.OpenAILlmService>() };

        var role = llmEnv.Roles.GetValueOrDefault(ChooseRoleName())
            ?? llmEnv.Roles.GetValueOrDefault(llmEnv.DefaultRole ?? string.Empty)
            ?? new LlmRole { Provider = defaultProvider, Model = "gpt-4o" };

        var agentStep = new TemplateLlmStep<IStepResult, LlmStringStepResult>(
            name: "FileAgent",
            role: role,
            templateProvider: null,
            tools: allTools.Where(t => t.Name != "write_to_file").Select(t => t.Name).ToList(),
            promtTemplateString: "{{input}}", // Property access is case-insensitive (TinyTemplateRendered.SelectTokenIgnoreCase); lowercase here just matches its own naming convention
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
                1. Para crear o sobrescribir un archivo escribe el tag <write_file> en tu respuesta,
                   siguiendo al pie de la letra la sección "IN-TEXT ACTION TAGS" de este mismo prompt.
                   No existe ninguna herramienta llamada write_file: no la invoques como tool.
                2. Para listar, leer o buscar, USA las herramientas (tools) disponibles.
                3. Si el usuario te pide listar archivos, USA la herramienta 'list_dir'.
                4. Responde siempre en español de forma concisa.
                """
        )
        {
            MaxToolIterations = 500,
            TagHandlers = handlers
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
        Console.WriteLine($"Model:   {role.Model} ({role.Provider.Service.Name})");
        Console.WriteLine($"Logs:    {logFile}");
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
                var step = (IStep)agentStep;
                IStepResult? result = null;
                try
                {
                    result = await step.ExecuteAsync(stepInput, turnContext, 1, null, CancellationToken.None);
                }
                finally
                {
                    // Calling the step directly bypasses RetryMiddleware, which is what normally
                    // finalizes it. Without this the conversation never compacts to its canonical
                    // [user / assistant] pair: every tool call and result of every turn stays in the
                    // history and the prompt grows without bound (100 K+ tokens after a few turns).
                    await step.FinalizeAsync(
                        result ?? ErrorStepResult.FromMessage(agentStep, "Step threw before producing a result"),
                        turnContext,
                        CancellationToken.None);
                }

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
