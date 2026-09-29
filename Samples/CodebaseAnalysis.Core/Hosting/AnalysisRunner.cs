using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.Observability;
using AITaskAgent.Support.Template;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;

namespace CodebaseAnalysis.Core.Hosting;

/// <summary>
/// Construye el pipeline y su contexto y lo ejecuta. Las tools deben estar ya registradas
/// (<see cref="AnalysisToolRegistration.Register"/>).
/// </summary>
public static class AnalysisRunner
{
    public const string SourceName = nameof(AnalysisRunner);

    /// <summary>
    /// Contexto de la ejecución con las claves que leen los steps y las tools. Sin
    /// <paramref name="eventChannel"/>, los mensajes solo van al <paramref name="reporter"/>.
    /// </summary>
    public static PipelineContext CreateContext(
        AnalysisPipelineInput input,
        IServiceProvider serviceProvider,
        IEventChannel? eventChannel,
        IAnalysisReporter reporter)
    {
        var context = new PipelineContext(eventChannel, serviceProvider);
        context.Set(AnalysisContextKeys.PipelineInput, input);
        context.Set(AnalysisContextKeys.CbmDbPath, input.DatabasePath);
        context.Set(AnalysisContextKeys.ReportsPath, input.OutputReportsPath);
        context.Set(AnalysisContextKeys.Reporter, reporter);
        context.Set(AnalysisContextKeys.PipelineStartedAt, DateTimeOffset.UtcNow);
        return context;
    }

    /// <summary>
    /// Ejecuta el pipeline completo. Sin <c>templateProvider</c> usa las plantillas junto al ejecutable;
    /// sin <c>engine</c>, el <see cref="IPipeline"/> del contenedor o uno nuevo.
    /// </summary>
    public static async Task<IStepResult> RunAsync(
        AnalysisPipelineInput input,
        Dictionary<string, LlmRole> roles,
        IServiceProvider serviceProvider,
        IEventChannel? eventChannel,
        IAnalysisReporter reporter,
        CancellationToken cancellationToken = default,
        ITemplateProvider? templateProvider = null,
        IPipeline? engine = null)
    {
        templateProvider ??= AnalysisPipelineFactory.CreateDefaultTemplateProvider();
        var pipeline = AnalysisPipelineFactory.CreatePipeline(templateProvider, roles, input, serviceProvider, engine);

        var context = CreateContext(input, serviceProvider, eventChannel, reporter);

        context.Report(SourceName, AnalysisLogLevel.Info, "🚀 Ejecutando AnalysisPipeline completo con AITaskFramework...");
        return await pipeline.ExecuteAsync(input, context, cancellationToken);
    }

    /// <summary>
    /// Vuelve a generar <paramref name="reports"/> sobre el grafo que ya existe
    /// (<see cref="AnalysisPipelineFactory.CreateRegenerationPipeline"/>): preparación, facetas, esos auditores y, con
    /// <paramref name="synthesis"/>, la síntesis ejecutiva. <paramref name="input"/> no se modifica: se trabaja sobre una
    /// copia con esos informes y sin <c>ForceReindex</c>.
    /// </summary>
    /// <exception cref="ArgumentException">Si <paramref name="reports"/> está vacío.</exception>
    public static async Task<IStepResult> RegenerateAsync(
        AnalysisPipelineInput input,
        IReadOnlyList<ReportType> reports,
        bool synthesis,
        Dictionary<string, LlmRole> roles,
        IServiceProvider serviceProvider,
        IEventChannel? eventChannel,
        IAnalysisReporter reporter,
        CancellationToken cancellationToken = default,
        ITemplateProvider? templateProvider = null,
        IPipeline? engine = null)
    {
        if (reports.Count == 0)
            throw new ArgumentException("La regeneración necesita al menos un informe.", nameof(reports));

        var regeneration = ForRegeneration(input, reports);
        templateProvider ??= AnalysisPipelineFactory.CreateDefaultTemplateProvider();
        var pipeline = AnalysisPipelineFactory.CreateRegenerationPipeline(templateProvider, roles, regeneration, synthesis, serviceProvider, engine);

        var context = CreateContext(regeneration, serviceProvider, eventChannel, reporter);

        context.Report(SourceName, AnalysisLogLevel.Info,
            $"♻️ Regenerando {string.Join(", ", reports)} {(synthesis ? "y el resumen ejecutivo" : "sin el resumen ejecutivo")} con AITaskFramework...");
        return await pipeline.ExecuteAsync(regeneration, context, cancellationToken);
    }

    /// <summary>Copia de <paramref name="input"/> con otros informes pedidos y sin reindexar.</summary>
    internal static AnalysisPipelineInput ForRegeneration(AnalysisPipelineInput input, IReadOnlyList<ReportType> reports) => new()
    {
        RepositoryPath = input.RepositoryPath,
        DatabasePath = input.DatabasePath,
        OutputReportsPath = input.OutputReportsPath,
        ForceReindex = false,
        RequestedReports = reports.Distinct().ToList(),
        AuditorMaxToolIterations = input.AuditorMaxToolIterations,
        AuditorMaxToolIterationsByRole = new Dictionary<string, int>(input.AuditorMaxToolIterationsByRole, StringComparer.OrdinalIgnoreCase)
    };
}
