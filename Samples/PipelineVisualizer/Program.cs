using AITaskAgent.Configuration;
using AITaskAgent.Core.Execution;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.Observability;
using PipelineVisualizer.Middleware;
using PipelineVisualizer.Models;
using PipelineVisualizer.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog with SSE sink
var sseSink = new SerilogSseSink();
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.Sink(sseSink)
    .WriteTo.File(
        Path.Combine(Directory.GetCurrentDirectory(), "logs", "general.log"),
        rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// Register services
builder.Services.AddSingleton(sseSink);
builder.Services.AddAITaskAgent();
builder.Services.AddSingleton<ILlmService, OpenAILLmService.OpenAILlmService>();

// Register EventChannel concrete type (required by SseEventBroadcaster)
// Forward from IEventChannel registration
builder.Services.AddSingleton(sp => (EventChannel)sp.GetRequiredService<IEventChannel>());

builder.Services.AddSingleton<SseEventBroadcaster>();
builder.Services.AddSingleton<PipelineExecutor>();
builder.Services.AddSingleton<ContextStore>();
builder.Services.AddSingleton<ContextBroadcastMiddleware>();
builder.Services.AddSingleton<PipelineSchemaGenerator>();

// Configure CORS for frontend development
var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(corsOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()));

// Add Controllers support with Newtonsoft.Json
builder.Services.AddControllers()
    .AddNewtonsoftJson(options =>
    {
        options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
        options.SerializerSettings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
    });

// Force port 5000 for frontend proxy
builder.WebHost.UseUrls("http://localhost:5000");

var app = builder.Build();

app.Services.RegisterPipelineMiddleware<ContextBroadcastMiddleware>();


app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// Map controller routes
app.MapControllers();

// === API ENDPOINTS ===

// GET /api/pipeline - Returns pipeline definition JSON
app.MapGet("/api/pipeline", async (IWebHostEnvironment env) =>
{
    var path = Path.Combine(env.WebRootPath, "pipeline-definition.json");
    if (!File.Exists(path))
    {
        return Results.NotFound("Pipeline definition not found");
    }
    var json = await File.ReadAllTextAsync(path);
    return Results.Content(json, "application/json");
})
.WithName("GetPipelineDefinition");


// GET /api/events - SSE endpoint for real-time events
app.MapGet("/api/events", async (
    HttpContext httpContext,
    SseEventBroadcaster broadcaster,
    CancellationToken cancellationToken) =>
{
    await broadcaster.StreamAsync(httpContext.Response, cancellationToken);
})
.WithName("EventStream");

// Health check
app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }))
.WithName("HealthCheck");

Log.Information("Pipeline Visualizer starting on {Urls}", string.Join(", ", app.Urls));

try
{
    await app.RunAsync();
}
finally
{
    await Log.CloseAndFlushAsync();
}
