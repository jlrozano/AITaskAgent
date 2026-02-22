using YamlPipelineDemo.Logging;
using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using AITaskAgent.Support.Template;
using AITaskAgent.YAML.Compilation;
using AITaskAgent.YAML.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OpenAILLmService;

var baseDir = AppContext.BaseDirectory;

// ── Configuration ──────────────────────────────────────────────────────────────
var isDevelopment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development"
    || Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Development";

var configBuilder = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(baseDir, "appsettings.json"), optional: false);

//if (isDevelopment)
//{
configBuilder.AddJsonFile(Path.Combine(baseDir, "appsettings.development.json"), optional: true);
//}

configBuilder.AddEnvironmentVariables();
var config = configBuilder.Build();

// ── Services ───────────────────────────────────────────────────────────────────
var services = new ServiceCollection();

// Make IConfiguration available for AddAITaskAgent's Options infrastructure
services.AddSingleton<IConfiguration>(config);

var logsDir = Path.Combine(baseDir, "logs");
services.AddLogging(b => b
    //.AddConsole()
    .AddProvider(new YamlFileLoggerProvider(logsDir, LogLevel.Trace))
    .SetMinimumLevel(LogLevel.Trace));

// OpenAI-compatible LLM service (handles OpenRouter, OpenAI, Azure, etc.)
services.AddSingleton<ILlmService, OpenAILlmService>();

// Register all framework services (Pipeline, LlmProviderResolver, EventChannel, etc.)
services.AddAITaskAgent();

// Template provider: resolves @templatename references in YAML step prompts
services.AddSingleton<ITemplateProvider>(
    new FileTemplateProvider(Path.Combine(baseDir, "prompts")));

// YAML pipeline engine
services.AddSingleton<SchemaCompiler>();
services.AddSingleton<YamlPipelineFactory>();

var sp = services.BuildServiceProvider();
sp.GetRequiredService<PipelineContextFactory>(); // Force static constructor to set up PipelineContext defaults
Console.WriteLine("=== YAML Pipeline Engine Demo with LLM Reasoning ===\n");

// ── Event Subscriber for LLM Reasoning ──────────────────────────────────────────
var eventChannel = sp.GetService<IEventChannel>();
var eventSubscription = eventChannel?.CreateSubscription(capacity: 500);

// Task that consumes and displays reasoning events in parallel with pipeline execution
var reasoningTask = Task.CompletedTask;
if (eventSubscription != null)
{
    reasoningTask = Task.Run(async () =>
    {
        await foreach (var evt in eventSubscription.Reader.ReadAllAsync())
        {
            //  if (evt is LlmResponseEvent llmEvent && llmEvent.IsThinking)
            // {
            Console.ForegroundColor = ConsoleColor.Yellow;
            //Console.WriteLine($"\n[💭 LLM Thinking - {llmEvent.StepName}]");
            if (evt is LlmResponseEvent res)
            {
                Console.Write(res.Content);
            }
            else
            {
                Console.WriteLine(JsonConvert.SerializeObject(evt, Formatting.Indented));
            }
            Console.ResetColor();
            //}
        }
    });
}

// ── 1. Compile JSON schemas → in-memory C# types ──────────────────────────────
Console.WriteLine("1. Compilando schemas JSON con Roslyn...");
var compiler = sp.GetRequiredService<SchemaCompiler>();
await compiler.CompileFromFolderAsync(Path.Combine(baseDir, "schemas"));

// GetCompiledType returns the result wrapper (e.g. DocumentInput : StepResult<DocumentInputData>).
// The POCO data type is the generic argument of StepResult<T>.
var documentInputType = compiler.GetCompiledType("DocumentInput");
var classificationOutputType = compiler.GetCompiledType("ClassificationOutput");
var extractionOutputType = compiler.GetCompiledType("ExtractionOutput");
var finalOutputType = compiler.GetCompiledType("FinalOutput");

// Helper: extracts the T from StepResult<T> so we can reflect on Data properties
static Type GetDataType(Type wrapperType) =>
    wrapperType.BaseType?.GenericTypeArguments.FirstOrDefault() ?? wrapperType;

var classificationDataType = GetDataType(classificationOutputType);
var extractionDataType = GetDataType(extractionOutputType);
var finalDataType = GetDataType(finalOutputType);
var documentInputDataType = GetDataType(documentInputType);

Console.WriteLine($"   Schemas compilados: {documentInputType.Name}, {classificationOutputType.Name}, {extractionOutputType.Name}, {finalOutputType.Name}");

// ── 2. Build pipeline from YAML (DAG validation + step wiring) ─────────────────
Console.WriteLine("\n2. Construyendo pipeline desde YAML...");
var factory = sp.GetRequiredService<YamlPipelineFactory>();
var pipeline = await factory.CreateFromFolderAsync(Path.Combine(baseDir, "pipelines"));
Console.WriteLine($"   Pipeline '{pipeline.Name}' construido con {pipeline.Steps.Count} step(s)");

// ── 3. Create typed input by deserializing JSON → DocumentInputData POCO → DocumentInput wrapper ──
var inputJson = """
    {
      "content": "Invoice from Acme Corporation dated March 1, 2026 for consulting services. Amount: $1500.00. Services rendered in March 2026 for business transformation consulting.",
      "source": "email_attachment"
    }
    """;

// Step 1: deserialize JSON into the pure POCO (DocumentInputData)
var documentInputData = JsonConvert.DeserializeObject(inputJson, documentInputDataType)
    ?? throw new InvalidOperationException("No se pudo deserializar DocumentInputData");

// Step 2: construct the result wrapper DocumentInput(IStep step, DocumentInputData? value)
// We pass a null IStep sentinel — the pipeline engine will set Step when it executes.
var documentInput = (IStepResult)(Activator.CreateInstance(documentInputType, [null!, documentInputData])
    ?? throw new InvalidOperationException("No se pudo crear DocumentInput"));

Console.WriteLine($"\n3. Input creado: {documentInputType.Name}");
Console.WriteLine($"   Implementa IStepResult: {documentInput is IStepResult}");

// ── 4. Execute pipeline ────────────────────────────────────────────────────────
Console.WriteLine("\n4. Ejecutando pipeline (observando razonamiento en tiempo real)...\n");

var result = await pipeline.ExecuteAsync(documentInput);

// ── 5. Wait for events to be processed and clean up ──────────────────────────────
await Task.Delay(500); // Allow queued events to process
if (eventSubscription != null)
{
    await eventSubscription.DisposeAsync();
}

// ── 6. Print result ────────────────────────────────────────────────────────────
Console.WriteLine("\n\n=== Pipeline Results ===");
Console.WriteLine($"HasError: {result.HasError}");

if (result.HasError)
{
    Console.WriteLine($"Error: {result.Error?.Message}");
}
else
{
    // Final result is FinalOutput from step_3_summarize.
    // result.Value is now FinalOutputData (the typed POCO), so we reflect on finalDataType.
    var finalData = result.Value;
    var summary = finalDataType.GetProperty("summary")?.GetValue(finalData);
    var documentType = finalDataType.GetProperty("document_type")?.GetValue(finalData);
    var recommendation = finalDataType.GetProperty("recommendation")?.GetValue(finalData);

    Console.WriteLine("\n[Step 3 - Final Output]");
    Console.WriteLine($"Document Type: {documentType}");
    Console.WriteLine($"Summary: {summary}");
    Console.WriteLine($"Recommendation: {recommendation}");

    // Access intermediate results from context if available
    //if (context.StepResults.TryGetValue("step_1_classify", out var classifyResult))
    //{
    //    if (!classifyResult.HasError)
    //    {
    //        var classifyData = classifyResult.Value;
    //        var type = classificationDataType.GetProperty("type")?.GetValue(classifyData);
    //        var confidence = classificationDataType.GetProperty("confidence")?.GetValue(classifyData);
    //        Console.WriteLine($"\n[Step 1 - Classification]");
    //        Console.WriteLine($"Type: {type}, Confidence: {confidence}");
    //    }
    //}

    //if (context.StepResults.TryGetValue("step_2_extract", out var extractResult))
    //{
    //    if (!extractResult.HasError)
    //    {
    //        var extractData = extractResult.Value;
    //        var keyFields = extractionDataType.GetProperty("key_fields")?.GetValue(extractData);
    //        Console.WriteLine($"\n[Step 2 - Extraction]");
    //        Console.WriteLine($"Key Fields: {JsonConvert.SerializeObject(keyFields)}");
    //    }
    //}
}
