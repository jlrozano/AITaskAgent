using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using AITaskAgent.Support.Template;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysis.Core.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodebaseAnalysis.Core.Hosting;

/// <summary>El resultado de <see cref="VerificationCommand.RunAsync"/>: el código de salida y lo que la CLI imprime al terminar.</summary>
/// <param name="ExitCode">0 si se verificó todo; 1 si el pipeline falló o algún informe no se pudo verificar; 130 si se canceló.</param>
/// <param name="Unverified">Informes cuyo verificador falló (su informe sigue siendo el de antes).</param>
/// <param name="Usage">El consumo LLM de la verificación, por paso.</param>
/// <param name="Cancelled">Si el llamante canceló: no se valida nada y el código de salida es 130.</param>
/// <param name="Result">El resultado del pipeline de verificación; null si se canceló antes de terminar.</param>
public sealed record VerificationCommandResult(int ExitCode, LlmUsageSummary Usage, int Unverified, bool Cancelled, IStepResult? Result);

/// <summary>
/// El modo <c>--verify &lt;informes&gt;</c> de la CLI: la verificación independiente a demanda de informes ya escritos, que la web ofrece con
/// «Verificar» y que hasta ahora no se podía lanzar por línea de comandos. No añade lógica de verificación: reutiliza
/// <see cref="AnalysisRunner.VerifyAsync"/> y las mismas opciones de repositorio, base de datos e informes que el análisis. El
/// modelo del verificador se cambia como el de cualquier rol: <c>--AITaskAgent:LlmProviders:Roles:ReportVerifier:Model=&lt;modelo&gt;</c>.
/// </summary>
public static class VerificationCommand
{
    /// <summary>Clave de configuración de <c>--verify</c>: la lista de informes (<c>--verify 01,04</c>, <c>--verify flujos,errores</c>) o <c>all</c>.</summary>
    public const string VerifyKey = "verify";

    /// <summary>
    /// Conmutador sin valor que pide, además, regenerar el resumen ejecutivo con lo verificado (en la web, la casilla; apagada por defecto, porque
    /// la síntesis cuesta una llamada más y reescribe el 00). Se quita de los argumentos antes de leer la configuración
    /// (<see cref="AnalysisRunOptions.ExtractSwitch"/>).
    /// </summary>
    public const string SynthesisSwitch = "--verify-synthesis";

    /// <summary>Valores de <c>--verify</c> que piden todos los informes del catálogo que estén en disco.</summary>
    private static readonly string[] AllValues = ["all", "todos", "true", "*"];

    /// <summary>
    /// Los informes de <c>--verify</c>, o null si la CLI no se lanzó con ese modo. Acepta los mismos nombres que <c>--reports</c>
    /// (<c>funcional</c>, <c>deadcode</c>, <c>FlujosEndToEnd</c>...) separados por comas, o <c>all</c>. Un nombre desconocido y un informe
    /// que no está en <paramref name="reportsPath"/> (no hay nada que verificar) se avisan por <paramref name="warn"/> y se descartan; sin
    /// informes que verificar devuelve una lista vacía. Sin repetidos y en el orden pedido (el del catálogo con <c>all</c>).
    /// </summary>
    public static List<ReportType>? ResolveReports(IConfiguration configuration, string reportsPath, Action<string> warn)
    {
        var raw = configuration[VerifyKey];
        if (raw == null)
            return null;

        var names = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var requested = new List<ReportType>();
        var all = names.Length == 0 || names.Any(n => AllValues.Contains(n, StringComparer.OrdinalIgnoreCase));
        if (all)
        {
            requested.AddRange(Enum.GetValues<ReportType>());
        }
        else
        {
            foreach (var name in names)
            {
                try
                {
                    var type = AnalysisPipelineInput.ParseReportType(name);
                    if (!requested.Contains(type))
                        requested.Add(type);
                }
                catch (ArgumentException ex)
                {
                    warn($"⚠️ --{VerifyKey}: {ex.Message.Split(" (Parameter", 2)[0]}; se descarta.");
                }
            }
        }

        var existing = new List<ReportType>();
        foreach (var type in requested)
        {
            var file = AnalysisPipelineInput.GetReportFileName(type);
            if (File.Exists(Path.Combine(reportsPath, file)))
                existing.Add(type);
            else if (!all)
                warn($"⚠️ --{VerifyKey}: no hay '{file}' en '{reportsPath}', no hay nada que verificar de {type}; se descarta.");
        }
        return existing;
    }

    /// <summary>
    /// Verifica <paramref name="reports"/> con <see cref="AnalysisRunner.VerifyAsync"/> y deja lo mismo que la web al terminar: el consumo y
    /// el estado en <c>_run_meta.json</c> (<c>kind: verification</c>) y la validación determinista de los informes en
    /// <c>_validation.json</c>. Las herramientas ya tienen que estar registradas (<see cref="AnalysisToolRegistration.Register"/>). Un informe
    /// que el verificador no pudo verificar no cambia el informe (sigue siendo el de antes) y se cuenta en <c>Unverified</c>.
    /// </summary>
    public static async Task<VerificationCommandResult> RunAsync(
        AnalysisPipelineInput input,
        IReadOnlyList<ReportType> reports,
        bool synthesis,
        Dictionary<string, LlmRole> roles,
        IServiceProvider serviceProvider,
        IAnalysisReporter reporter,
        CancellationToken cancellationToken,
        ITemplateProvider? templateProvider = null,
        IPipeline? engine = null)
    {
        // Un canal solo para contar el consumo LLM y los informes que no se pudieron verificar; los mensajes van al reporter.
        var usageTracker = new LlmUsageTracker();
        var unverified = 0;
        var channel = new EventChannel(LogLevel.None, serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<EventChannel>());
        var subscription = channel.Subscribe((evt, _) =>
        {
            usageTracker.Observe(evt);
            if (evt is StepProgressEvent { EventType: AnalysisEventTypes.ReportVerified } verified
                && verified.Metadata?.GetValueOrDefault(AnalysisEventTypes.MetadataKeys.Outcome) as string == "failed")
            {
                Interlocked.Increment(ref unverified);
            }
            return Task.CompletedTask;
        });
        var startedAt = DateTimeOffset.UtcNow;

        IStepResult? result = null;
        try
        {
            result = await AnalysisRunner.VerifyAsync(
                input, reports, synthesis, roles, serviceProvider, channel, reporter, cancellationToken, templateProvider, engine);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Cerrar el canal espera a que el suscriptor haya visto todos los eventos.
            await channel.DisposeAsync();
            subscription.Dispose();
        }

        var cancelled = cancellationToken.IsCancellationRequested;
        var succeeded = !cancelled && result is { HasError: false } && (result is not AnalysisPipelineFinalResult final || final.Success);
        var usage = usageTracker.Snapshot();
        var status = cancelled ? "cancelled" : succeeded ? "succeeded" : "failed";
        if (RunMetaFile.TryWrite(input.OutputReportsPath,
                RunMetaFile.Build(RunMetaFile.KindVerification, runId: null, status, startedAt, DateTimeOffset.UtcNow, usage)) is { } metaWarning)
        {
            reporter.Report(AnalysisLogLevel.Warning, AnalysisRunner.SourceName, metaWarning);
        }

        // La validación determinista de lo que queda en disco (también refresca _validation.json): no cambia el código de salida.
        if (!cancelled)
            ReportValidationSummary.RunAfterPipeline(input, reporter);

        var exit = cancelled ? 130 : succeeded && unverified == 0 ? 0 : 1;
        return new VerificationCommandResult(exit, usage, unverified, cancelled, result);
    }
}
