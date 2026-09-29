using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.LLM.Tools.Implementation;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using McpIntegration.Configuration;
using McpIntegration.Extensions;
using McpIntegration.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Text;

namespace Samples.CodebaseAnalysisPipeline;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║     Codebase Analysis Pipeline Runner — AITaskFramework & Azure OpenAI       ║
║     Fases: 1.Detección -> 2.Enriquecimiento -> 3.Facetas -> 4.Informes -> 5.Síntesis ║
╚══════════════════════════════════════════════════════════════════════════════╝
");

        // 1. Cargar la configuración del proyecto (solo desde el directorio del ejecutable).
        IConfiguration configuration = AnalysisConfiguration.Build(
            AnalysisConfiguration.DefaultConfigFilePath, args, AnalysisConfiguration.GetEnvVar, Console.WriteLine);

        // 2. Resolver ubicación del repositorio target y configurar Serilog
        var repoPath = AnalysisRunOptions.ResolveRepositoryPath(configuration);

        var logDir = Path.Combine(repoPath, "logs");
        Directory.CreateDirectory(logDir);

        var msLogLevel = AnalysisRunOptions.ResolveLogLevel(configuration, args);
        var logEventLevel = msLogLevel switch
        {
            LogLevel.Trace => Serilog.Events.LogEventLevel.Verbose,
            LogLevel.Debug => Serilog.Events.LogEventLevel.Debug,
            LogLevel.Warning => Serilog.Events.LogEventLevel.Warning,
            LogLevel.Error => Serilog.Events.LogEventLevel.Error,
            _ => Serilog.Events.LogEventLevel.Information
        };

        // Las plantillas por defecto de Serilog con el paso o auditor que emite cada línea delante del mensaje (StepOriginEnricher).
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(logEventLevel)
            .Enrich.FromLogContext()
            .Enrich.With<StepOriginEnricher>()
            .WriteTo.Console(restrictedToMinimumLevel: logEventLevel,
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Origin:l}{Message:lj}{NewLine}{Exception}")
            .WriteTo.File(Path.Combine(logDir, "analysis-pipeline-.log"), rollingInterval: RollingInterval.Day, restrictedToMinimumLevel: logEventLevel,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Origin:l}{Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Console.WriteLine($"🔍 Nivel de logging configurado a: {msLogLevel}");

        // 3. Configurar Contenedor de Inyección de Dependencias
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(msLogLevel);
            builder.AddSerilog(dispose: true);
        });

        services.AddCodebaseAnalysisLlmServices();
        services.AddAITaskAgent();

        var serviceProvider = services.BuildServiceProvider();
        serviceProvider.UseAITaskAgent();

        // 4. Conexión opcional al servidor MCP CodebaseMemory. Desactivada por defecto: sus tools
        //    duplican graph_search y las demás graph_*, y ningún auditor las tiene asignadas. El
        //    ejecutable se sigue usando como indexador (paso 1) aunque la conexión esté desactivada.
        var mcpSection = configuration.GetSection("McpServers:CodebaseMemory");
        if (mcpSection.Exists() && mcpSection.GetValue("ConnectMcp", false))
        {
            try
            {
                // Relativos al repositorio analizado, como el resto de rutas: con el directorio
                // actual, CODEBASE_MEMORY_DB apuntaba a la carpeta desde la que se lanzaba el sample.
                var rawWorkingDir = mcpSection["WorkingDirectory"];
                var serverConfig = new McpServerConfig
                {
                    Name = "CodebaseMemory",
                    TransportType = McpTransportType.Stdio,
                    Command = CodebaseMemoryLocator.Locate(configuration).ExePath
                        ?? throw new InvalidOperationException("No se encontró el ejecutable de codebase-memory-mcp."),
                    Arguments = mcpSection.GetSection("Arguments").Get<List<string>>() ?? ["mcp"],
                    WorkingDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(rawWorkingDir) ? "." : rawWorkingDir, repoPath),
                    EnvironmentVariables = mcpSection.GetSection("EnvironmentVariables").Get<Dictionary<string, string>>(),
                    TimeoutSeconds = mcpSection.GetValue<int>("TimeoutSeconds", 30)
                };

                var mcpLogger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<McpToolProvider>();
                var mcpProvider = new McpToolProvider(serverConfig, mcpLogger);

                Console.WriteLine("📡 Intentando conexión con servidor MCP CodebaseMemory...");
                await mcpProvider.ConnectAsync();
                var registered = await AvailableTools.Instance.RegisterMcpToolsAsync(mcpProvider);
                Console.WriteLine($"✅ Servidor MCP conectado. {registered} herramientas registradas.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ℹ️ Conexión MCP omitida ({ex.Message}). El pipeline utilizará consultas directas a SQLite.");
            }
        }

        // 5. Configurar proveedores y roles LLM. Cada rol usa el proveedor que nombra en `Provider`.
        var roles = LlmRoleFactory.BuildRoles(configuration, serviceProvider, AnalysisConfiguration.GetEnvVar, Console.WriteLine);

        // 6. Configurar Parámetros de Entrada del Análisis
        var input = AnalysisRunOptions.BuildInput(configuration, repoPath, Console.WriteLine);

        Console.WriteLine($"📁 Repositorio Target: {input.RepositoryPath}");
        Console.WriteLine($"🗄️ Base de Datos SQLite: {input.DatabasePath}");
        Console.WriteLine($"📊 Carpeta de Informes: {input.OutputReportsPath}");
        Console.WriteLine($"📋 Informes Solicitados: {input.RequestedReports.Count} tipos seleccionados.");
        Console.WriteLine();

        // 7. Registrar tools nativas del grafo, Office y FileTools, antes de construir el pipeline.
        AnalysisToolRegistration.Register(input);
        Console.WriteLine("🛠️ Herramientas nativas del grafo, FileTools (view_file, view_code_item) y Office registradas.");

        // 8. Construir y ejecutar el pipeline. Sin canal de eventos: los mensajes van a la consola.
        //    El primer Ctrl+C cancela el pipeline (que mata el árbol del indexador, ProcessCancellation) y deja
        //    que termine ordenadamente; el segundo termina el proceso como siempre.
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancelKey = (_, e) =>
        {
            if (cancellation.IsCancellationRequested)
                return;

            e.Cancel = true;
            Console.WriteLine();
            Console.WriteLine("⏹️ Cancelando el análisis (Ctrl+C otra vez para salir sin esperar)...");
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancelKey;

        IStepResult? finalResult = null;
        try
        {
            finalResult = await AnalysisRunner.RunAsync(
                input, roles, serviceProvider, eventChannel: null, ConsoleAnalysisReporter.Instance, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            Console.CancelKeyPress -= onCancelKey;
        }

        // Regla del runner web: la cancelación se decide por el token, no por el tipo de resultado.
        if (cancellation.IsCancellationRequested)
        {
            Console.WriteLine();
            Console.WriteLine("⚠️ Análisis cancelado por el usuario. Los informes escritos hasta ahora se conservan.");
            Environment.ExitCode = 130;
            return;
        }

        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════════════════════════════════════════");
        Console.WriteLine("                       RESUMEN DE EJECUCIÓN DEL PIPELINE                       ");
        Console.WriteLine("══════════════════════════════════════════════════════════════════════════════");

        if (finalResult is AnalysisPipelineFinalResult res && !res.HasError && res.Success)
        {
            Console.WriteLine($"⏱️ Duración Total: {res.Duration.TotalSeconds:F2} segundos");
            Console.WriteLine($"📄 Informe Ejecutivo: {res.ExecutiveReportPath}");
            Console.WriteLine($"📑 Informes incluidos en el resumen: {res.IndividualReports.Count}");

            foreach (var r in res.IndividualReports)
            {
                Console.WriteLine($"   • {r.ReportFileName} [{r.QualityHealth}] — {r.ReportType}");
            }

            foreach (var failed in res.FailedReports)
            {
                Console.WriteLine($"   ❌ {failed}: el auditor falló en esta ejecución; el resumen ejecutivo lo indica.");
            }

            Console.WriteLine("══════════════════════════════════════════════════════════════════════════════");
            if (res.FailedReports.Count > 0)
            {
                Console.WriteLine($"⚠️ Proceso completado con {res.FailedReports.Count} informe(s) sin generar.");
                Environment.ExitCode = 1;
            }
            else
            {
                Console.WriteLine("✅ Proceso completado exitosamente.");
            }
        }
        else
        {
            Console.WriteLine($"Estado final: {finalResult?.GetType().Name ?? "null"}");
            if (finalResult?.HasError == true)
            {
                Console.WriteLine($"❌ Error: {finalResult.Error?.Message}");
                if (finalResult.Error?.OriginalException != null)
                {
                    Console.WriteLine($"Detalle: {finalResult.Error.OriginalException}");
                }
            }
            Console.WriteLine("══════════════════════════════════════════════════════════════════════════════");
            Console.WriteLine("❌ Proceso terminado con errores.");
            Environment.ExitCode = 1;
        }
    }
}
