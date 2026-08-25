using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Conversation.Context;
using GeminiLlmService;
using Google.GenAI.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

Console.WriteLine("=== GeminiLlmService Demo ===\n");

// 1. Load configuration
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

// Get provider config
var providerConfig = configuration.GetSection("AITaskAgent:LlmProviders:Providers:Gemini")
    .Get<GeminiSettings>()
    ?? throw new InvalidOperationException("Gemini provider configuration not found");

Console.WriteLine($"Model: {providerConfig.Model}");
Console.WriteLine($"BaseUrl: {providerConfig.BaseUrl}\n");

// Validate API key
if (string.IsNullOrEmpty(providerConfig.ApiKey) || providerConfig.ApiKey.Contains("<YOUR_"))
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("⚠️  API Key not configured!");
    Console.WriteLine("Set your API key in one of these ways:");
    Console.WriteLine("  1. Edit appsettings.json and replace <YOUR_GEMINI_API_KEY>");
    Console.WriteLine("  2. Set environment variable: GEMINI_API_KEY");
    Console.WriteLine("\nGet your API key at: https://ai.google.dev/");
    Console.ResetColor();
    return;
}

Console.WriteLine($"API Key: {providerConfig.ApiKey.Substring(0, 5)}... (Configured)");

// 2. Setup logger
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});
var logger = loggerFactory.CreateLogger<GeminiLlmService.GeminiLlmService>();

// 3. Create service
var service = new GeminiLlmService.GeminiLlmService(logger);

Console.WriteLine("Select demo:");
Console.WriteLine("  1. Google Search Grounding");
Console.WriteLine("  2. Context Caching");
Console.WriteLine("  3. Both");
Console.WriteLine("  4. List Available Models (Debug)");
Console.Write("\nChoice (1-4): ");
var choice = Console.ReadLine()?.Trim() ?? "1";

if (choice is "1" or "3")
{
    await DemoGoogleSearchAsync(service, providerConfig);
}

if (choice is "2" or "3")
{
    await DemoContextCachingAsync(service, providerConfig, logger);
}

if (choice is "4")
{
    // Pass API Key and Version explicitly to allow independent execution
    await ListModelsAsync(service, providerConfig.ApiKey, providerConfig.ApiVersion);
}

Console.WriteLine("\n=== Demo Complete ===");

// ============ Helper: List Models ============
async Task ListModelsAsync(GeminiLlmService.GeminiLlmService svc, string apiKey, string? apiVersion)
{
    Console.WriteLine("\n" + new string('=', 50));
    Console.WriteLine("📋 AVAILABLE MODELS");
    Console.WriteLine(new string('=', 50));

    try
    {
        // Use the service's debug method with explicit credentials
        await svc.ListModelsAsync(apiKey, apiVersion);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Error listing models: {ex.Message}");
        Console.ResetColor();
    }
}

// ============ Demo: Google Search Grounding ============
async Task DemoGoogleSearchAsync(GeminiLlmService.GeminiLlmService svc, GeminiSettings config)
{
    Console.WriteLine("\n" + new string('=', 50));
    Console.WriteLine("🌐 GOOGLE SEARCH GROUNDING DEMO (STREAMING)");
    Console.WriteLine(new string('=', 50));

    // Create conversation with a question that requires real-time info
    var conversation = new ConversationContext();
    conversation.AddUserMessage("Busca las noticias más importantes sobre Inteligencia Artificial publicadas HOY. Incluye la fecha explícita de cada una.");

    // Enable Google Search via Metadata
    conversation.Metadata["Gemini.EnableGoogleSearch"] = true;

    // Create request with Google Search tool
    var request = new LlmRequest
    {
        Conversation = conversation,
        SystemPrompt = "Eres un redactor de noticias de última hora. Tu prioridad es la ACTUALIDAD. Busca noticias publicadas en las últimas 24 horas. Para cada noticia, DEBES indicar el TÍTULO, la FUENTE y la FECHA/HORA de publicación. Si una noticia no es de hoy, indícalo claramente.",
        Model = config.Model,
        ApiKey = config.ApiKey,
        BaseUrl = config.BaseUrl,
        ApiVersion = config.ApiVersion,
        UseStreaming = true // Enable streaming
    };

    Console.WriteLine("\n📤 Pregunta: ¿Cuáles son las últimas noticias sobre IA hoy?");
    Console.WriteLine("   (Google Search se activará automáticamente)\n");
    Console.WriteLine("📥 Respuesta:");

    try
    {
        // State for thinking block
        bool isThinking = false;

        // Iterate over the stream
        await foreach (var chunk in svc.InvokeStreamingAsync(request))
        {
            // Handle state transitions for thinking
            if (chunk.IsThinking && !isThinking)
            {
                isThinking = true;
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write("\n<think>");
            }
            else if (!chunk.IsThinking && isThinking)
            {
                isThinking = false;
                Console.Write("</think>\n\n");
                Console.ResetColor();
                Console.ForegroundColor = ConsoleColor.Green;
            }

            // Print the content
            Console.Write(chunk.Delta);
        }

        // Close thinking if it was open at the end (unlikely but safe)
        if (isThinking)
        {
            Console.Write("</think>\n");
            Console.ResetColor();
        }

        Console.ResetColor();
        Console.WriteLine("\n\n=== Fin del Stream ===");
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n❌ Error: {ex.Message}");

        if (ex.Message.Contains("Quota exceeded") || ex.Message.Contains("429"))
        {
            Console.WriteLine("\n⚠️  Límite de cuota excedido.");
            Console.WriteLine("   Si estás usando el 'Free Tier' con un modelo experimental o 'pro', es posible que el límite sea 0 o muy bajo.");
            Console.WriteLine("   Sugerencia: Cambia a 'gemini-1.5-flash' en appsettings.json para pruebas generales.");
        }

        Console.ResetColor();
    }
}

// ============ Demo: Context Caching ============
async Task DemoContextCachingAsync(GeminiLlmService.GeminiLlmService svc, GeminiSettings config, ILogger log)
{
    Console.WriteLine("\n" + new string('=', 50));
    Console.WriteLine("🔥 CONTEXT CACHING DEMO");
    Console.WriteLine(new string('=', 50));

    // First, we need to make a request to initialize the client
    var initConversation = new ConversationContext();
    initConversation.AddUserMessage("Hola");

    var initRequest = new LlmRequest
    {
        Conversation = initConversation,
        SystemPrompt = "Di 'hola' brevemente",
        Model = config.Model,
        ApiKey = config.ApiKey,
        BaseUrl = config.BaseUrl,
        ApiVersion = config.ApiVersion,
    };

    Console.WriteLine("\n⏳ Inicializando cliente...");
    await svc.InvokeAsync(initRequest);
    Console.WriteLine("✅ Cliente inicializado");

    // Now we have access to CacheManager
    var cacheManager = svc.CacheManager;

    // Create a large context (repeated to exceed token limit for demo)
    var docContent = System.IO.File.ReadAllText("Program.cs"); // Read self as part of context
    var largeContext = new System.Text.StringBuilder();
    largeContext.AppendLine("Eres un experto en el framework AITaskAgent.");

    // Repeat content to ensure > 1024 tokens (but not excessive like 124k)
    // Program.cs is roughly 300 lines. 5 repetitions should be ~1500 lines or ~3000-5000 tokens.
    for (int i = 0; i < 5; i++)
    {
        largeContext.AppendLine($"--- SECCION REPETIDA {i} ---");
        largeContext.AppendLine(docContent);
        largeContext.AppendLine("AITaskAgent es modular, resiliente y escalable.");
    }

    var contextContents = new List<Content>
    {
        new()
        {
            Role = "user",
            Parts = [new Part { Text = largeContext.ToString() }]
        }
    };

    Console.WriteLine("\n📦 Creando cache con contexto largo (>1000 tokens)...");

    try
    {
        // Create cache with 5 minute TTL (shorter for demo)
        var cache = await cacheManager.CreateAsync(
            model: config.Model,
            contents: contextContents,
            displayName: "demo-large-context",
            ttl: TimeSpan.FromMinutes(5)
        );

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"✅ Cache creado: {cache.Name}");
        Console.WriteLine($"   Tokens cacheados: {cache.UsageMetadata?.TotalTokenCount}");
        Console.WriteLine($"   Expira: {cache.ExpireTime}");
        Console.WriteLine($"   💰 Ahorro habilitado para queries.");
        Console.ResetColor();

        // Now make queries using the cached context
        Console.WriteLine("\n📝 Haciendo preguntas sobre el cache...\n");

        var questions = new[]
        {
            "¿Qué hace este código (Program.cs)?",
        };

        foreach (var question in questions)
        {
            Console.WriteLine($"❓ {question}");

            var qaConversation = new ConversationContext();
            qaConversation.AddUserMessage(question);

            var qaRequest = new LlmRequest
            {
                Conversation = qaConversation,
                SystemPrompt = null, // System prompt is in the cache
                Model = config.Model,
                ApiKey = config.ApiKey,
                BaseUrl = config.BaseUrl,
                ApiVersion = config.ApiVersion
            };

            var answer = await svc.InvokeAsync(qaRequest);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"💬 {answer.Content.Substring(0, Math.Min(200, answer.Content.Length))}...");
            Console.ResetColor();
            Console.WriteLine($"   Tokens: {answer.TokensUsed} | Costo: ${answer.CostUsd:F4}\n");
        }

        // Cleanup - delete cache
        Console.WriteLine("🗑️  Eliminando cache...");
        await cacheManager.DeleteAsync(cache.Name!);
        Console.WriteLine("✅ Cache eliminado");
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Error en caching: {ex.Message}");

        if (ex.Message.Contains("limit exceeded") || ex.Message.Contains("Quota exceeded"))
        {
            Console.WriteLine("\n⚠️  Has excedido los límites de tu plan (posiblemente Free Tier).");
            Console.WriteLine("   Context Caching suele ser una función de pago o tener límites estrictos (0 en free tier para ciertos modelos).");
            Console.WriteLine("   Intenta usar un modelo diferente o actualizar tu plan de Google AI Studio.");
        }

        Console.ResetColor();
    }
}


/// <summary>
/// Connection settings for this test app, bound straight from configuration.
///
/// Declared locally rather than reusing a framework type: this app drives the Gemini SDK
/// directly, without pipelines or DI, so it has no LlmEnvironment to resolve a role from.
/// </summary>
internal sealed class GeminiSettings
{
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string? BaseUrl { get; set; }
    public string? ApiVersion { get; set; }
}
