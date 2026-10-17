using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.LLM.Tools.Implementation;
using AITaskAgent.Observability;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysis.Core.Support;
using McpIntegration.Configuration;
using McpIntegration.Extensions;
using McpIntegration.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Filters;
using System.Text;

namespace Samples.CodebaseAnalysisPipeline;

internal static class Program
{
    /// <summary>Propiedad de Serilog de los mensajes que van al fichero de log y no a la consola.</summary>
    private const string FileOnlyProperty = "FileOnly";

    private static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║     Codebase Analysis Pipeline Runner — AITaskFramework & Azure OpenAI       ║
║     Fases: 1.Detección -> 2.Enriquecimiento -> 3.Facetas -> 4.Informes -> 5.Síntesis ║
╚══════════════════════════════════════════════════════════════════════════════╝
");

        // 1. Cargar la configuración del proyecto (solo desde el directorio del ejecutable). --validate-reports y --verify-synthesis se
        //    quitan antes, porque sin valor se comerían el argumento siguiente.
        args = AnalysisRunOptions.ExtractSwitch(args, AnalysisRunOptions.ValidateReportsSwitch, out var validateOnly);
        args = AnalysisRunOptions.ExtractSwitch(args, VerificationCommand.SynthesisSwitch, out var verifySynthesis);
        // Un flag que exige valor y llega sin él se ignoraría en silencio y arrancaría un análisis de pago con otra intención: se aborta antes de nada.
        if (AnalysisRunOptions.FindFlagsMissingValue(args) is { Count: > 0 } missingValue)
        {
            Console.WriteLine($"❌ Falta el valor de: {string.Join(", ", missingValue)}. Uso: --flag <valor> (p. ej. --verify all, --repo <ruta>). No se ha ejecutado nada.");
            Environment.ExitCode = 2;
            return;
        }

        IConfiguration configuration = AnalysisConfiguration.Build(
            AnalysisConfiguration.DefaultConfigFilePath, args, AnalysisConfiguration.GetEnvVar, Console.WriteLine);

        // 2. Resolver ubicación del repositorio target y configurar Serilog
        var repoPath = AnalysisRunOptions.ResolveRepositoryPath(configuration);

        // --validate-reports: valida los informes que ya hay y termina, sin LLM, sin pipeline y sin crear logs/ en el repositorio.
        if (validateOnly)
        {
            Environment.ExitCode = ReportValidationCommand.Run(configuration, repoPath, ConsoleAnalysisReporter.Instance);
            return;
        }

        // --verify <informes>: verificación independiente a demanda de informes ya escritos (la «Verificar» de la web), sin analizar de nuevo.
        var verifyReports = VerificationCommand.ResolveReports(
            configuration, AnalysisRunOptions.ResolveReportsPath(configuration, repoPath), Console.WriteLine);
        if (verifyReports is { Count: 0 })
        {
            Console.WriteLine($"❌ --{VerificationCommand.VerifyKey}: no hay informes que verificar en '{AnalysisRunOptions.ResolveReportsPath(configuration, repoPath)}'.");
            Environment.ExitCode = 1;
            return;
        }

        var logDir = AnalysisRunOptions.ResolveLogsPath(configuration, repoPath);
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
            // Lo marcado con FileOnlyProperty va solo al fichero: ya sale en la consola por el reporter del pipeline.
            .WriteTo.Logger(console => console
                .Filter.ByExcluding(Matching.WithProperty(FileOnlyProperty))
                .WriteTo.Console(restrictedToMinimumLevel: logEventLevel,
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Origin:l}{Message:lj}{NewLine}{Exception}"))
            .WriteTo.File(Path.Combine(logDir, "analysis-pipeline-.log"), rollingInterval: RollingInterval.Day, restrictedToMinimumLevel: logEventLevel,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Origin:l}{Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Console.WriteLine($"🔍 Nivel de logging configurado a: {msLogLevel}");
        if (AnalysisRunOptions.DebugLogWarning(msLogLevel, logDir) is { } debugWarning)
            Console.WriteLine(debugWarning);

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

        // 5. Proveedores y roles LLM de AITaskAgent:LlmProviders, que enlaza el framework. Cada rol usa el proveedor que nombra en `Provider`.
        var roles = LlmRoleFactory.BuildRoles(serviceProvider, AnalysisConfiguration.GetEnvVar, Console.WriteLine);

        // 6. Configurar Parámetros de Entrada del Análisis
        var input = AnalysisRunOptions.BuildInput(configuration, repoPath, Console.WriteLine);

        Console.WriteLine($"📁 Repositorio Target: {input.RepositoryPath}");
        Console.WriteLine($"🗄️ Base de Datos SQLite: {input.DatabasePath}");
        Console.WriteLine($"📊 Carpeta de Informes: {input.OutputReportsPath}");
        Console.WriteLine(verifyReports != null
            ? $"🔎 Informes a verificar: {string.Join(", ", verifyReports)}."
            : $"📋 Informes Solicitados: {input.RequestedReports.Count} tipos seleccionados.");
        Console.WriteLine();

        // 7. Registrar tools nativas del grafo, Office y FileTools, antes de construir el pipeline.
        AnalysisToolRegistration.Register(input);
        Console.WriteLine("🛠️ Herramientas nativas del grafo, FileTools (view_file, view_code_item) y Office registradas.");

        if (verifyReports != null)
        {
            await RunVerificationAsync(input, verifyReports, verifySynthesis, roles, serviceProvider);
            return;
        }

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

        // Un canal solo para contar el consumo LLM de cada llamada (LlmUsageTracker): los mensajes siguen yendo a la consola
        // por el reporter, y el canal no registra nada (LogLevel.None).
        var usageTracker = new LlmUsageTracker();
        var usageChannel = new EventChannel(LogLevel.None, serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<EventChannel>());
        var usageSubscription = usageChannel.Subscribe((evt, _) =>
        {
            usageTracker.Observe(evt);
            return Task.CompletedTask;
        });
        var startedAt = DateTimeOffset.UtcNow;

        // Los límites de tiempo efectivos: AnalysisRunner los escribe en la consola por el reporter, que no llega al fichero
        // de log; aquí van también al fichero, que es donde se busca por qué se cortó un paso.
        var fileOnly = Log.ForContext(FileOnlyProperty, true);
        foreach (var line in EffectiveLimits.DescribeAnalysis(serviceProvider, roles))
        {
            fileOnly.Information("{Limits}", line);
        }

        IStepResult? finalResult = null;
        try
        {
            finalResult = await AnalysisRunner.RunAsync(
                input, roles, serviceProvider, usageChannel, ConsoleAnalysisReporter.Instance, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            Console.CancelKeyPress -= onCancelKey;
            // Cerrar el canal espera a que el suscriptor haya visto todos los eventos.
            await usageChannel.DisposeAsync();
            usageSubscription.Dispose();
        }

        // Consumo de la ejecución en reports/_run_meta.json, también si se cancela o falla (lo gastado se gastó).
        var usage = usageTracker.Snapshot();
        var runStatus = cancellation.IsCancellationRequested ? "cancelled"
            : finalResult is AnalysisPipelineFinalResult { HasError: false, Success: true } done
                ? done.FailedReports.Count > 0 ? "completed_with_failures" : "succeeded"
                : "failed";
        if (RunMetaFile.TryWrite(input.OutputReportsPath,
                RunMetaFile.Build(RunMetaFile.KindAnalysis, runId: null, runStatus, startedAt, DateTimeOffset.UtcNow, usage)) is { } metaWarning)
        {
            Console.WriteLine(metaWarning);
        }

        // Regla del runner web: la cancelación se decide por el token, no por el tipo de resultado.
        if (cancellation.IsCancellationRequested)
        {
            Console.WriteLine();
            Console.WriteLine("⚠️ Análisis cancelado por el usuario. Los informes escritos hasta ahora se conservan.");
            PrintUsage(usage);
            Environment.ExitCode = 130;
            return;
        }

        // Validación determinista de los informes escritos (_validation.json): no bloquea ni cambia el código de salida.
        Console.WriteLine();
        ReportValidationSummary.RunAfterPipeline(input, ConsoleAnalysisReporter.Instance);

        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════════════════════════════════════════");
        Console.WriteLine("                       RESUMEN DE EJECUCIÓN DEL PIPELINE                       ");
        Console.WriteLine("══════════════════════════════════════════════════════════════════════════════");
        PrintUsage(usage);

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

    /// <summary>
    /// <c>--verify</c>: verifica los informes pedidos (<see cref="VerificationCommand"/>) y termina. Como el análisis, el primer Ctrl+C
    /// cancela ordenadamente y el segundo termina el proceso. El código de salida es 0 si se verificó todo, 1 si el pipeline falló o
    /// algún informe no se pudo verificar (su informe sigue siendo el de antes) y 130 si se canceló.
    /// </summary>
    private static async Task RunVerificationAsync(
        AnalysisPipelineInput input,
        IReadOnlyList<ReportType> reports,
        bool synthesis,
        Dictionary<string, AITaskAgent.LLM.Configuration.LlmRole> roles,
        IServiceProvider serviceProvider)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancelKey = (_, e) =>
        {
            if (cancellation.IsCancellationRequested)
                return;

            e.Cancel = true;
            Console.WriteLine();
            Console.WriteLine("⏹️ Cancelando la verificación (Ctrl+C otra vez para salir sin esperar)...");
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancelKey;

        VerificationCommandResult outcome;
        try
        {
            outcome = await VerificationCommand.RunAsync(
                input, reports, synthesis, roles, serviceProvider, ConsoleAnalysisReporter.Instance, cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onCancelKey;
        }

        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════════════════════════════════════════");
        Console.WriteLine("                      RESUMEN DE LA VERIFICACIÓN                               ");
        Console.WriteLine("══════════════════════════════════════════════════════════════════════════════");
        PrintUsage(outcome.Usage);
        if (outcome.Cancelled)
        {
            Console.WriteLine("⚠️ Verificación cancelada por el usuario. Los informes ya verificados se conservan.");
        }
        else if (outcome.Unverified > 0)
        {
            Console.WriteLine($"⚠️ {outcome.Unverified} informe(s) no se pudieron verificar: siguen siendo los de antes.");
        }
        else if (outcome.ExitCode == 0)
        {
            Console.WriteLine("✅ Verificación completada.");
        }
        else
        {
            Console.WriteLine($"❌ La verificación terminó con errores: {outcome.Result?.Error?.Message ?? outcome.Result?.GetType().Name ?? "sin resultado"}");
        }
        Console.WriteLine("══════════════════════════════════════════════════════════════════════════════");
        Environment.ExitCode = outcome.ExitCode;
    }

    /// <summary>Consumo LLM de la ejecución: el total y una línea por paso que llamó al modelo (auditores, síntesis, fallback).</summary>
    private static void PrintUsage(LlmUsageSummary usage)
    {
        Console.WriteLine($"💰 Consumo LLM: {usage.Total.Describe()}");
        foreach (var source in usage.BySource)
        {
            Console.WriteLine($"   • {source.Source}: {source.Describe()}");
        }
        Console.WriteLine($"   (detalle en {RunMetaFile.FileName}, en la carpeta de informes)");
    }
}
