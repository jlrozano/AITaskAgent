using AITaskAgent.Core.Abstractions;
using AITaskAgent.Observability;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;

namespace CodebaseAnalysisWeb.Analysis;

/// <summary>
/// Lo que ejecuta un análisis. Existe para que los tests de la web inyecten una ejecución falsa, sin
/// indexador ni LLM, que emite eventos del framework por el canal que recibe.
/// </summary>
public interface IAnalysisWorkflow
{
    /// <param name="input">Entrada del pipeline; las tools ya están registradas.</param>
    /// <param name="eventChannel">Canal propio de la ejecución; el llamante lo cierra al terminar.</param>
    /// <param name="reporter">Destino de los mensajes de progreso.</param>
    /// <param name="cancellationToken">Se cancela con <c>POST /cancel</c> o al parar el servidor.</param>
    Task<IStepResult> RunAsync(
        AnalysisPipelineInput input,
        IEventChannel eventChannel,
        IAnalysisReporter reporter,
        CancellationToken cancellationToken);

    /// <summary>
    /// Vuelve a generar <paramref name="reports"/> sobre el grafo de <paramref name="input"/>, sin detección, indexado ni
    /// enriquecimiento; con <paramref name="synthesis"/>, también el resumen ejecutivo. Mismos canal, reporter y token que
    /// <see cref="RunAsync"/>.
    /// </summary>
    /// <param name="input">Entrada de la ejecución que se regenera; no se modifica.</param>
    /// <param name="reports">Informes que se regeneran; al menos uno.</param>
    /// <param name="synthesis">Si se regenera también el resumen ejecutivo.</param>
    /// <param name="eventChannel">Canal propio de la regeneración; el llamante lo cierra al terminar.</param>
    /// <param name="reporter">Destino de los mensajes de progreso.</param>
    /// <param name="cancellationToken">Se cancela con <c>POST /cancel</c> o al parar el servidor.</param>
    Task<IStepResult> RegenerateAsync(
        AnalysisPipelineInput input,
        IReadOnlyList<ReportType> reports,
        bool synthesis,
        IEventChannel eventChannel,
        IAnalysisReporter reporter,
        CancellationToken cancellationToken);
}
