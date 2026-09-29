using AITaskAgent.Core.Abstractions;
using AITaskAgent.Observability;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysisWeb.Hosting;
using Microsoft.Data.Sqlite;

namespace CodebaseAnalysisWeb.Analysis;

/// <summary>El pipeline real, igual que lo ejecuta la CLI (<see cref="AnalysisRunner"/>).</summary>
public sealed class PipelineAnalysisWorkflow(IServiceProvider serviceProvider, AnalysisHost host) : IAnalysisWorkflow
{
    public Task<IStepResult> RunAsync(
        AnalysisPipelineInput input,
        IEventChannel eventChannel,
        IAnalysisReporter reporter,
        CancellationToken cancellationToken)
    {
        // Las tools del grafo (también las del chat) abren la BD con pool: una conexión agrupada mantiene
        // abierto el fichero que la indexación sobrescribe o que ForceReindex mueve a backups/.
        SqliteConnection.ClearAllPools();

        return AnalysisRunner.RunAsync(
            input, host.Roles, serviceProvider, eventChannel, reporter, cancellationToken, host.TemplateProvider);
    }

    public Task<IStepResult> RegenerateAsync(
        AnalysisPipelineInput input,
        IReadOnlyList<ReportType> reports,
        bool synthesis,
        IEventChannel eventChannel,
        IAnalysisReporter reporter,
        CancellationToken cancellationToken)
    {
        // Como al lanzar: la regeneración retira y reincorpora fuentes del grafo que el chat puede tener abierto con pool.
        SqliteConnection.ClearAllPools();

        return AnalysisRunner.RegenerateAsync(
            input, reports, synthesis, host.Roles, serviceProvider, eventChannel, reporter, cancellationToken, host.TemplateProvider);
    }
}
