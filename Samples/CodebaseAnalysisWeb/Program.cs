using AITaskAgent.Configuration;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysisWeb.Analysis;
using CodebaseAnalysisWeb.Api;
using CodebaseAnalysisWeb.Chat;
using CodebaseAnalysisWeb.Hosting;
using CodebaseAnalysisWeb.Knowledge;
using CodebaseAnalysisWeb.Sse;
using CodebaseAnalysisWeb.Workspace;
using Serilog;
using Serilog.Events;
using Serilog.Filters;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;

// Los mensajes del pipeline llevan emojis, como en la CLI.
Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);

// La configuración del análisis (roles, proveedores, rutas) es la misma que la de la CLI: el JSON expandido
// junto al ejecutable y, otra vez y en este orden, variables de entorno, user secrets y línea de comandos,
// para que se impongan al JSON igual que en la CLI.
builder.Configuration.AddAnalysisConfiguration(
    AnalysisConfiguration.DefaultConfigFilePath, args, AnalysisConfiguration.GetEnvVar, log: null);

WebSecurity.EnsureLoopbackUrls(builder.Configuration);

// Serilog: consola y, dentro de un análisis, <repo>/logs/analysis-pipeline-<fecha>.log como la CLI. El
// runner hace LogContext.PushProperty(AnalysisLogDir) y Map abre un fichero por carpeta. Solo llegan a Map
// los eventos con la propiedad (sin clave por defecto que ocupe sitio) y con sinkMapCountLimit: 1 queda
// abierto únicamente el fichero del último repositorio analizado: cambiar de repositorio cierra el anterior.
builder.Services.AddSerilog((services, lc) => lc
    .ReadFrom.Configuration(builder.Configuration)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.Logger(runLog => runLog
        .Filter.ByIncludingOnly(Matching.WithProperty(AnalysisRunManager.LogDirProperty))
        .WriteTo.Map(AnalysisRunManager.LogDirProperty, string.Empty, (logDir, wt) =>
        {
            if (!string.IsNullOrEmpty(logDir))
            {
                wt.File(Path.Combine(logDir, "analysis-pipeline-.log"), rollingInterval: RollingInterval.Day, shared: true);
            }
        }, sinkMapCountLimit: 1)), preserveStaticLogger: true);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

builder.Services.Configure<RouteOptions>(options => options.ConstraintMap[NotApiRouteConstraint.Name] = typeof(NotApiRouteConstraint));

// Un cuerpo que no se puede leer (JSON mal formado, tipos que no casan, Content-Type distinto de JSON) lanza
// BadHttpRequestException en lugar de dejar un 400/415 sin cuerpo; el middleware de abajo le da el formato
// {error, message} de la API.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

builder.Services.AddCodebaseAnalysisLlmServices();
builder.Services.AddAITaskAgent();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => SseOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<AnalysisHost>();
builder.Services.AddSingleton<WorkspaceSettingsStore>();
builder.Services.AddSingleton<WorkspaceState>();
builder.Services.AddSingleton<IAnalysisWorkflow, PipelineAnalysisWorkflow>();
builder.Services.AddSingleton<AnalysisRunManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AnalysisRunManager>());
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<KnowledgeService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton(sp => CodebaseAnalysis.Core.Knowledge.Embeddings.EmbeddingsRuntime.FromConfiguration(
    sp.GetRequiredService<IConfiguration>(), AnalysisConfiguration.GetEnvVar,
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("embeddings")));

var app = builder.Build();

app.Services.UseAITaskAgent();

// Roles y plantillas se construyen ya: un error de configuración aborta el arranque con el mensaje de la CLI.
_ = app.Services.GetRequiredService<AnalysisHost>();

// El último repositorio abierto, si sigue existiendo; si falla, la web arranca sin workspace (con aviso).
app.Services.GetRequiredService<WorkspaceState>().TryRestoreLast();

// Errores de lectura del cuerpo con el formato de la API (plan §4.1).
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (BadHttpRequestException ex) when (!context.Response.HasStarted && ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
    {
        // Kestrel corta un cuerpo mayor que su límite (o que el RequestSizeLimit de la ruta) al leerlo: el 413 lleva
        // también {error, message} (plan v2 §5.1, hueco 1 del frontend).
        context.Response.Clear();
        await new ApiError(ApiErrorCodes.FileTooLarge, "El cuerpo de la petición supera el tamaño máximo admitido.")
            .ToResult(StatusCodes.Status413PayloadTooLarge)
            .ExecuteAsync(context);
    }
    catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
    {
        context.Response.Clear();
        await new ApiError(ApiErrorCodes.InvalidRequest,
                $"El cuerpo de la petición no es JSON válido o no tiene el formato esperado: {(ex.InnerException ?? ex).Message}")
            .ToResult(ex.StatusCode is >= 400 and < 500 ? ex.StatusCode : StatusCodes.Status400BadRequest)
            .ExecuteAsync(context);
    }
});

// Una ruta /api desconocida es un 404 con el cuerpo de error de la API, y un Content-Type que no es JSON
// (415, que el enlace del cuerpo fija sin excepción) lleva también {error, message}.
app.Use(async (context, next) =>
{
    await next(context);
    if (context.Response.HasStarted || !context.Request.Path.StartsWithSegments("/api"))
        return;

    if (context.Response.StatusCode == StatusCodes.Status404NotFound && context.GetEndpoint() == null)
    {
        await ApiErrorsFactory.NotFound("Ruta de API desconocida.").ToResult(StatusCodes.Status404NotFound).ExecuteAsync(context);
    }
    else if (context.Response.StatusCode == StatusCodes.Status413PayloadTooLarge)
    {
        await new ApiError(ApiErrorCodes.FileTooLarge, "El cuerpo de la petición supera el tamaño máximo admitido.")
            .ToResult(StatusCodes.Status413PayloadTooLarge).ExecuteAsync(context);
    }
    else if (context.Response.StatusCode == StatusCodes.Status415UnsupportedMediaType)
    {
        await new ApiError(ApiErrorCodes.InvalidRequest, "El cuerpo tiene que ser JSON (Content-Type: application/json).")
            .ToResult(StatusCodes.Status415UnsupportedMediaType).ExecuteAsync(context);
    }
});

// Peticiones de otro sitio que cambian estado: 403 antes de llegar a ningún endpoint (plan v2 §5.6).
app.UseCrossSiteGuard();

// index.html (por "/" y por el fallback) lleva la CSP de la SPA; los assets y la API, no (plan v2, auditoría V2).
var spaFiles = SpaSecurityHeaders.StaticFileOptions();
app.UseDefaultFiles();
app.UseStaticFiles(spaFiles);

app.MapMetaEndpoints();
app.MapWorkspaceEndpoints();
app.MapAnalysisEndpoints();
app.MapReportsEndpoints();
app.MapDocumentsEndpoints();
app.MapKnowledgeEndpoints();
app.MapChatEndpoints();
app.MapSourceEndpoints();

// La SPA navega por hash, pero cualquier otra ruta que no sea /api sirve index.html (si existe wwwroot).
// Un comodín que aceptara también /api se quedaría las peticiones sin JSON y taparía el 415.
app.MapFallbackToFile($"{{*path:{NotApiRouteConstraint.Name}:nonfile}}", SpaSecurityHeaders.IndexFileName, spaFiles);

app.Run();

public partial class Program;
