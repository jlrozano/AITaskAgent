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
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Samples.AdvancedLlmPipeline;
using Serilog;
using Serilog.Events;
using Serilog.Filters;
using System.ComponentModel;

// --- Configuración ---

var services = new ServiceCollection();

var environmentName =
    Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ??
    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
    "Production";

IConfiguration configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .Build();


services.AddSingleton(configuration);

// Limpiar archivos de log al iniciar (cada ejecución empieza limpia)
var logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "logs");
if (Directory.Exists(logDirectory))
{
    foreach (var file in Directory.GetFiles(logDirectory))
    {
        try { File.Delete(file); } catch { }
    }
}
Directory.CreateDirectory(logDirectory);

// Configurar Serilog

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    // 1. Logs Generales (TODO, incluyendo eventos de observabilidad)
    .WriteTo.File(Path.Combine(logDirectory, "general.log"), rollingInterval: RollingInterval.Day)
    // 2. Observability separado (solo EventChannel, para análisis específico)
    .WriteTo.Logger(l => l
        .Filter.ByIncludingOnly(Matching.FromSource("AITaskAgent.Observability.EventChannel"))
        .WriteTo.File(Path.Combine(logDirectory, "observability.log"), rollingInterval: RollingInterval.Day))
    // 3. Console deshabilitado (solo Console.WriteLine del usuario)
    .CreateLogger();

services.AddLogging(loggingBuilder =>
{
    loggingBuilder.ClearProviders(); // Elimina ConsoleLoggerProvider default
    loggingBuilder.AddSerilog(dispose: true);
});

services.AddAITaskAgent();
services.AddSingleton<ILlmService, OpenAILLmService.OpenAILlmService>();

services.AddSingleton<IStepTracer, NullStepTracer>();

// Use FileConversationStorage for persistent conversations (saved to disk)
services.AddSingleton<IConversationStorage>(new FileConversationStorage("conversations"));

var serviceProvider = services.BuildServiceProvider();

// --- EVENT LOGGING (Events.jsonl & Console) ---
var eventChannel = serviceProvider.GetService<IEventChannel>() as EventChannel;
if (eventChannel != null)
{
    var reader = eventChannel.Subscribe(capacity: 1000);
    _ = Task.Run(async () =>
    {
        var logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "logs");
        Directory.CreateDirectory(logDirectory);
        var eventFile = Path.Combine(logDirectory, $"events-{DateTime.Now:yyyyMMdd}.jsonl");

        await foreach (var evt in reader.ReadAllAsync())
        {
            // 1. Siempre persistir a archivo
            var json = System.Text.Json.JsonSerializer.Serialize(evt);
            await File.AppendAllTextAsync(eventFile, json + Environment.NewLine);

            // 2. Solo enviar a consola los eventos de LLM y de comienzo de paso
            if (evt is StepStartedEvent started)
            {
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine($"\n[🚀 {started.StepName}] Iniciando... (Intento {started.AttemptNumber})");
                Console.ResetColor();
            }
            else if (evt is LlmResponseEvent llm)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                // FinishReason.Streaming = chunk, any other value = final
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

var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
var jsonParser = serviceProvider.GetRequiredService<JsonResponseParser>();
var conversationStorage = serviceProvider.GetRequiredService<IConversationStorage>();
var llmService = serviceProvider.GetRequiredService<ILlmService>();
var profileResolver = serviceProvider.GetRequiredService<ILlmProviderResolver>();

// --- RESOLVER PROFILES ---
var intentionAnalyzerProfile = profileResolver.GetProvider("IntentionAnalyzer");
var outlinerProfile = profileResolver.GetProvider("Outliner");
var writersProfile = profileResolver.GetProvider("Writers");
var criticProfile = profileResolver.GetProvider("Critic");
var rewriterProfile = profileResolver.GetProvider("Rewriter");
var publisherProfile = profileResolver.GetProvider("Publisher");
var storyReviewerProfile = profileResolver.GetProvider("StoryReviewer");
var questionsProfile = profileResolver.GetProvider("QuestionsAssistant");

// --- CONSTANTES ---

const string USER_ID = "user_demo_001";
string? lastStory = null;

// --- CREATE PIPELINES USING FACTORY ---

IStep newStoryProcessStep = StoryPipelineFactory.CreateNewStoryPipeline(
    llmService,
    outlinerProfile,
    writersProfile,
    criticProfile,
    rewriterProfile,
    publisherProfile,
    story => lastStory = story
);

IStep reviewProcessStep = StoryPipelineFactory.CreateReviewPipeline(
    llmService,
    storyReviewerProfile,
    story => lastStory = story
);

IStep questionsProcessStep = StoryPipelineFactory.CreateQuestionsPipeline(
    llmService,
    questionsProfile
);

IStep otherProcessStep = StoryPipelineFactory.CreateOtherPipeline();

// --- INTENTION ANALYSIS & ROUTING ---

var intentionStep = new IntentionAnalyzerStep<StepResult, UserIntention>(
    llmService,
    intentionAnalyzerProfile,
    systemPrompt: "You classify user intentions for a story creation system. Always respond in Spanish."
);

var routerStep = new IntentionRouterStep<UserIntention>(
    routes: new Dictionary<UserIntention, IStep>
    {
        { UserIntention.NewStory, newStoryProcessStep },
        { UserIntention.ReviewStory, reviewProcessStep },
        { UserIntention.Questions, questionsProcessStep },
        { UserIntention.Other, otherProcessStep }
    },
    defaultRoute: questionsProcessStep
);

List<IStep> mainPipeline = [intentionStep, routerStep];

// --- Ejecución ---

Console.WriteLine("=== MÁQUINA DE HISTORIAS DE IA CON MEMORIA ===");
Console.WriteLine("Este sistema puede:");
Console.WriteLine("1. 📝 Crear cuentos nuevos");
Console.WriteLine("2. ✏️  Revisar y modificar el cuento anterior");
Console.WriteLine("--------------------------------------------------");

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
    if (string.IsNullOrWhiteSpace(input))
    {
        break;
    }
    Console.WriteLine($"📖 {conversation.History.Messages.Count} mensajes.");
    try
    {

        if (!string.IsNullOrEmpty(lastStory) && conversation.History.Messages.Count == 0)
        {
            conversation.AddSystemMessage("You are a professional story editor.");
            conversation.AddUserMessage($"Este es el cuento actual:\n\n{lastStory}");
        }

        _ = serviceProvider.GetRequiredService<PipelineContextFactory>(); // Initialize factory
        var context = PipelineContextFactory.Create() with { Conversation = conversation };
        var bookmark = conversation.CreateBookmark();
        // Execute Pipeline
        var result = await Pipeline.ExecuteAsync(
            "StoryMachine",
            mainPipeline,
            new UserInputResult(input),
            context
        );
        conversation.RestoreBookmark(bookmark);
        if (result.HasError)
        {
            Console.WriteLine($"Error: {result.Error?.Message}");
        }
        else
        {
            // Extract final content from result
            var finalContent = result is StepResult<string> finalResult && !string.IsNullOrEmpty(finalResult.Value)
                ? finalResult.Value
                : result is LlmStringStepResult llmResult && !string.IsNullOrEmpty(llmResult.Content)
                    ? llmResult.Content
                    : (result.Value?.ToString());

            // Save conversation: user message + assistant response
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

// Internal types
internal enum UserIntention
{
    [Description("User wants to create a completely new story from scratch")]
    NewStory,

    [Description("User wants to review, modify, or improve the existing story")]
    ReviewStory,

    [Description("User is asking questions about the story, the creative process, or requesting clarification")]
    Questions,

    [Description("User request is out of scope (not related to story creation, review, or questions)")]
    Other
}

/// <summary>
/// Wrapper for user input to match IStepResult interface.
/// </summary>
internal class UserInputResult(string input) : StepResult(new EmptyStep("User"), input)
{
    /// <summary>
    /// Gets the message content.
    /// </summary>
    public string Message => Value?.ToString() ?? string.Empty;
}

internal class UserIntentionResponse(IStep step) : LlmStepResult<Intention<UserIntention>>(step);

