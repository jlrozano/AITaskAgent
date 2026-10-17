using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.JSON;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Conversation.Storage;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Samples.AdvancedLlmPipeline;
using Serilog;
using Serilog.Events;
using Serilog.Filters;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;

// --- Configuración con Inyección de Secretos ---

var environmentName =
    Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ??
    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
    "Development";

// Reads a settings file next to the binary, expanding ${VAR} placeholders from the
// environment. Returns null when the file is absent (the environment overlay is optional).
static MemoryStream? ReadSettings(string fileName)
{
    var path = Path.Combine(Directory.GetCurrentDirectory(), fileName);
    if (!File.Exists(path)) return null;

    var json = Regex.Replace(
        File.ReadAllText(path),
        @"\$\{(\w+)\}",
        m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);
    return new MemoryStream(Encoding.UTF8.GetBytes(json));
}

using var configStream = ReadSettings("appsettings.json")
    ?? throw new FileNotFoundException("appsettings.json not found in the working directory.");
// The environment overlay carries the API keys (gitignored) and must win over the base file.
using var envConfigStream = ReadSettings($"appsettings.{environmentName}.json");

var configBuilder = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonStream(configStream);

if (envConfigStream != null)
{
    configBuilder.AddJsonStream(envConfigStream);
}

IConfiguration configuration = configBuilder
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();
services.AddSingleton(configuration);

// Limpiar archivos de log al iniciar (cada ejecución empieza limpia).
// La base es AppContext.BaseDirectory (carpeta del binario), no el cwd: bajo `dotnet run` el
// cwd es la carpeta del proyecto, así que una ruta relativa "..\..\..\logs" se sale del repo.
var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logDirectory);
foreach (var file in Directory.GetFiles(logDirectory))
{
    try
    {
        File.Delete(file);
    }
    catch (IOException)
    {
        // Fichero bloqueado (p.ej. por otro proceso con el log abierto); se deja para la próxima limpieza.
    }
    catch (UnauthorizedAccessException)
    {
        // Sin permisos para borrar; no es un fallo fatal del arranque.
    }
}

// Configurar Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.File(Path.Combine(logDirectory, "general.log"), rollingInterval: RollingInterval.Day)
    .WriteTo.Logger(l => l
        .Filter.ByIncludingOnly(Matching.FromSource("AITaskAgent.Observability.EventChannel"))
        .WriteTo.File(Path.Combine(logDirectory, "observability.log"), rollingInterval: RollingInterval.Day))
    .CreateLogger();

services.AddLogging(loggingBuilder =>
{
    loggingBuilder.ClearProviders();
    loggingBuilder.AddSerilog(dispose: true);
});

services.AddAITaskAgent();

// Registro de Servicios LLM: ambos como ILlmService (implementaciones múltiples), igual que
// SimpleChatDemo/McpIntegrationDemo. La resolución por proveedor la hace el framework
// (ConfigureLlmEnvironmentOptions) emparejando el "ServiceType" de appsettings.json con
// ILlmService.Name ("openai"/"gemini") — no hace falta hidratación manual.
services.AddSingleton<ILlmService, OpenAILLmService.OpenAILlmService>();
services.AddSingleton<ILlmService, GeminiLlmService.GeminiLlmService>();

// IStepTracer is obsolete: the framework never invokes it, so this registration has no effect.
#pragma warning disable CS0618
services.AddSingleton<IStepTracer, NullStepTracer>();
#pragma warning restore CS0618
services.AddSingleton<IConversationStorage>(new FileConversationStorage("conversations"));

var serviceProvider = services.BuildServiceProvider();
serviceProvider.UseAITaskAgent();

// --- LLM ENVIRONMENT & ENGINES ---
var env = serviceProvider.GetRequiredService<IOptions<LlmEnvironment>>().Value;

// --- EVENT LOGGING ---
var eventChannel = serviceProvider.GetService<IEventChannel>() as EventChannel;
if (eventChannel != null)
{
    // Subscribe(handler) is the callback form now; CreateSubscription exposes the reader.
    var subscription = eventChannel.CreateSubscription(capacity: 1000);
    var reader = subscription.Reader;
    _ = Task.Run(async () =>
    {
        var eventFile = Path.Combine(logDirectory, $"events-{DateTime.Now:yyyyMMdd}.jsonl");

        await foreach (var evt in reader.ReadAllAsync())
        {
            // Serializar por el tipo concreto (no por IProgressEvent): la interfaz no expone
            // Content/TokensUsed/FinishReason/etc. de los eventos derivados.
            var json = System.Text.Json.JsonSerializer.Serialize(evt, evt.GetType());
            await File.AppendAllTextAsync(eventFile, json + Environment.NewLine);

            if (evt is StepStartedEvent started)
            {
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine($"\n[🚀 {started.StepName}] Iniciando... (Intento {started.AttemptNumber})");
                Console.ResetColor();
            }
            else if (evt is LlmResponseEvent llm)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                if (llm.FinishReason != AITaskAgent.LLM.Constants.FinishReason.Streaming)
                {
                    Console.WriteLine($"\n[🏁 {llm.StepName}] Finalizado. Razón: {llm.FinishReason}, Tokens: {llm.TokensUsed}");
                }
                else
                {
                    Console.Write(string.IsNullOrEmpty(llm.Content) ? "" : llm.Content);
                }
                Console.ResetColor();
            }
            else if (evt is StepRoutingEvent routing)
            {
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"\n[🔀 {routing.StepName}] Ruta seleccionada: {routing.SelectedRoute}");
                Console.ResetColor();
            }
        }
    });
}

// Resolver Roles
var roles = env.Roles;
var intentionAnalyzerRole = roles["IntentionAnalyzer"];
var outlinerRole = roles["Outliner"];
var writersRole = roles["Writers"];
var criticRole = roles["Critic"];
var rewriterRole = roles["Rewriter"];
var publisherRole = roles["Publisher"];
var storyReviewerRole = roles["StoryReviewer"];
var questionsRole = roles["QuestionsAssistant"];

const string USER_ID = "user_demo_001";
string? lastStory = null;

// --- CREATE PIPELINES USING FACTORY ---
IStep newStoryProcessStep = StoryPipelineFactory.CreateNewStoryPipeline(
    outlinerRole,
    writersRole,
    criticRole,
    rewriterRole,
    publisherRole,
    story => lastStory = story
);

IStep reviewProcessStep = StoryPipelineFactory.CreateReviewPipeline(
    storyReviewerRole,
    story => lastStory = story
);

IStep questionsProcessStep = StoryPipelineFactory.CreateQuestionsPipeline(
    questionsRole
);

IStep otherProcessStep = StoryPipelineFactory.CreateOtherPipeline();

// --- INTENTION ANALYSIS & ROUTING ---
var options = new List<IntentionOption>
{
    new() { Id = "NewStory", Description = "User wants to create a completely new story from scratch" },
    new() { Id = "ReviewStory", Description = "User wants to review, modify, or improve the existing story" },
    new() { Id = "Questions", Description = "User is asking questions about the story, the creative process, or requesting clarification" },
    new() { Id = "Other", Description = "User request is out of scope" }
};

var intentionStep = new IntentionAnalyzerStep<UserInputResult>(
    name: "IntentionAnalyzer",
    role: intentionAnalyzerRole,
    options: options,
    promptTemplateName: "{{ input }}"
);

var routerStep = new RouterStep(
    name: "Router",
    routes: new Dictionary<string, IStep>
    {
        { "NewStory", newStoryProcessStep },
        { "ReviewStory", reviewProcessStep },
        { "Questions", questionsProcessStep },
        { "Other", otherProcessStep }
    },
    defaultRoute: questionsProcessStep
);

List<IStep> mainPipeline = [intentionStep, routerStep];

// --- Ejecución ---
Console.WriteLine("=== MÁQUINA DE HISTORIAS DE IA CON MEMORIA ===");
Console.WriteLine("Este sistema puede:");
Console.WriteLine("1. 📝 Crear cuentos nuevos");
Console.WriteLine("2. ✏️  Revisar y modificar el cuento anterior");
Console.WriteLine("Comandos: /clear (nueva conversación), /exit (salir)");
Console.WriteLine("--------------------------------------------------");

var conversationStorage = serviceProvider.GetRequiredService<IConversationStorage>();
var conversation = await conversationStorage.GetConversationAsync(USER_ID);
if (conversation == null)
{
    conversation = new ConversationContext { ConversationId = USER_ID };
    Console.WriteLine("📝 Nueva conversación creada.");
}
else
{
    Console.WriteLine($"📖 Conversación cargada ({conversation.History.Messages.Count} mensajes).");
}

while (true)
{
    Console.Write("\n💬 Tú: ");
    var input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input)) break;

    var command = input.Trim().ToLowerInvariant();
    if (command == "/exit")
    {
        Console.WriteLine("👋 ¡Hasta luego!");
        break;
    }

    if (command == "/clear")
    {
        // Overwrite the persisted file too - otherwise the next launch reloads the same
        // history this command was meant to drop.
        conversation = new ConversationContext { ConversationId = USER_ID };
        lastStory = null;
        await conversationStorage.SaveConversationAsync(conversation);
        Console.WriteLine("🧹 Conversación reiniciada.");
        continue;
    }

    try
    {
        if (!string.IsNullOrEmpty(lastStory) && conversation.History.Messages.Count == 0)
        {
            conversation.AddSystemMessage("You are a professional story editor.");
            conversation.AddUserMessage($"Este es el cuento actual:\n\n{lastStory}");
        }

        var context = PipelineContextFactory.Create(conversation);
        var bookmark = conversation.CreateBookmark();

        var pipeline = serviceProvider.GetRequiredService<IPipeline>();
        var result = await pipeline.ExecuteAsync(
            "StoryMachine",
            mainPipeline,
            new UserInputResult(input),
            context,
            pipelineTimeout: null,
            cancellationToken: default
        );

        conversation.RestoreBookmark(bookmark);

        if (result.HasError)
        {
            Console.WriteLine($"Error: {result.Error?.Message}");
        }
        else
        {
            var finalContent = result is StepResult<string> finalResult && !string.IsNullOrEmpty(finalResult.Value)
                ? finalResult.Value
                : result is LlmStringStepResult llmResult && !string.IsNullOrEmpty(llmResult.Content)
                    ? llmResult.Content
                    : (result.Value?.ToString());

            if (context.Conversation != null && !string.IsNullOrEmpty(finalContent))
            {
                context.Conversation.AddUserMessage(input);
                context.Conversation.AddAssistantMessage(finalContent);
                await conversationStorage.SaveConversationAsync(context.Conversation);
            }

            Console.WriteLine("\n==================================================");
            Console.WriteLine("📚 RESULTADO");
            Console.WriteLine("==================================================");
            Console.WriteLine(finalContent ?? "Resultado vacío");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Error: {ex.Message}");
    }
}

// --- Internal classes ---

internal class UserInputResult(string input) : StepResult(new EmptyStep("User"), input)
{
    public string Message => Value?.ToString() ?? string.Empty;
}

internal class UserIntentionResponse(IStep step) : LlmStepResult<Intention>(step);
