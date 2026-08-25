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
    "Production";

// Leer appsettings.json y expandir variables de entorno ${VAR}
var jsonConfig = File.ReadAllText("appsettings.json");
jsonConfig = Regex.Replace(jsonConfig, @"\$\{(\w+)\}", m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);
using var configStream = new MemoryStream(Encoding.UTF8.GetBytes(jsonConfig));

IConfiguration configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonStream(configStream)
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();
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

// Registro de Servicios LLM
services.AddSingleton<ILlmService, OpenAILLmService.OpenAILlmService>();
services.AddSingleton<GeminiProxy>();

services.AddSingleton<IStepTracer, NullStepTracer>();
services.AddSingleton<IConversationStorage>(new FileConversationStorage("conversations"));

var serviceProvider = services.BuildServiceProvider();
serviceProvider.UseAITaskAgent();

// --- LLM ENVIRONMENT & ENGINES ---
// Manually populate LlmEnvironment to avoid binding errors with new object-based roles/providers
var configSection = configuration.GetSection("AITaskAgent:LlmProviders");
var rawEnv = new LlmEnvironment();
// Note: We don't use BindEx here because it's what fails. We'll do it partially or manually.
configuration.GetSection("AITaskAgent:LlmProviders").Bind(rawEnv); // Standard bind for simple properties

var openAiService = serviceProvider.GetRequiredService<ILlmService>();
var geminiProxy = serviceProvider.GetRequiredService<GeminiProxy>();

// Manual Hydration of Providers
foreach (var kvp in rawEnv.Providers)
{
    var provider = kvp.Value;
    ILlmService service = (kvp.Key.Contains("Gemini", StringComparison.OrdinalIgnoreCase) || kvp.Key.Contains("Google", StringComparison.OrdinalIgnoreCase)) 
        ? geminiProxy : openAiService;
    typeof(LlmProvider).GetProperty("Service")?.SetValue(provider, service);
}

// Manual Hydration of Roles (Mapping provider names to objects)
var rolesSection = configSection.GetSection("Roles");
foreach (var roleKvp in rawEnv.Roles)
{
    var role = roleKvp.Value;
    var roleName = roleKvp.Key;
    var providerName = rolesSection.GetSection(roleName).GetValue<string>("Provider");
    
    if (!string.IsNullOrEmpty(providerName) && rawEnv.Providers.TryGetValue(providerName, out var provider))
    {
        typeof(LlmRole).GetProperty("Provider")?.SetValue(role, provider);
    }
    else if (rawEnv.Providers.Count > 0)
    {
        // Default to OpenRouterBase if available, otherwise first
        var defaultProvider = rawEnv.Providers.ContainsKey("OpenRouterBase") 
            ? rawEnv.Providers["OpenRouterBase"] 
            : rawEnv.Providers.Values.First();
        typeof(LlmRole).GetProperty("Provider")?.SetValue(role, defaultProvider);
    }

    if (string.IsNullOrEmpty(role.Model))
         typeof(LlmRole).GetProperty("Model")?.SetValue(role, "gpt-4o");

    var fallbacks = rawEnv.FallbackProviders
        .Where(f => rawEnv.Providers.ContainsKey(f.Provider))
        .Select(f => new LlmFallback(f.Model, rawEnv.Providers[f.Provider]))
        .ToList();
    typeof(LlmRole).GetProperty("Fallbacks")?.SetValue(role, fallbacks);
}

// Override the broken framework options
services.AddSingleton<IOptions<LlmEnvironment>>(new OptionsWrapper<LlmEnvironment>(rawEnv));
// Re-build SP to include the override
serviceProvider = services.BuildServiceProvider();
serviceProvider.UseAITaskAgent();

// --- EVENT LOGGING ---
var eventChannel = serviceProvider.GetService<IEventChannel>() as EventChannel;
if (eventChannel != null)
{
    var reader = eventChannel.Subscribe(capacity: 1000);
    _ = Task.Run(async () =>
    {
        var logDir = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "logs");
        Directory.CreateDirectory(logDir);
        var eventFile = Path.Combine(logDir, $"events-{DateTime.Now:yyyyMMdd}.jsonl");

        await foreach (var evt in reader.ReadAllAsync())
        {
            var json = System.Text.Json.JsonSerializer.Serialize(evt);
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
var roles = rawEnv.Roles;
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

public class GeminiProxy(ILlmService openAiService) : ILlmService
{
    public string Name => "gemini";
    public Task<LlmResponse> InvokeAsync(LlmRequest request, CancellationToken ct) => openAiService.InvokeAsync(request, ct);
    public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken ct) => openAiService.InvokeStreamingAsync(request, ct);
    public int EstimateTokenCount(string text) => openAiService.EstimateTokenCount(text);
    public int GetMaxContextTokens(string? model) => openAiService.GetMaxContextTokens(model);
}

internal class UserInputResult(string input) : StepResult(new EmptyStep("User"), input)
{
    public string Message => Value?.ToString() ?? string.Empty;
}

internal class UserIntentionResponse(IStep step) : LlmStepResult<Intention>(step);
