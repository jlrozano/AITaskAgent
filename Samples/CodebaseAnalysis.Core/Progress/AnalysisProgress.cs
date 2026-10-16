using AITaskAgent.Core.Models;
using AITaskAgent.Observability.Events;
using CodebaseAnalysis.Core.Models;
using Key = CodebaseAnalysis.Core.Progress.AnalysisEventTypes.MetadataKeys;

namespace CodebaseAnalysis.Core.Progress;

/// <summary>
/// Mensajes de dominio del pipeline. Cada mensaje va al <see cref="IAnalysisReporter"/> del contexto
/// (la consola si no hay ninguno) y, además, sale como <see cref="StepProgressEvent"/> por el canal de
/// eventos del contexto, que no hace nada cuando el contexto no tiene canal.
/// </summary>
public static class AnalysisProgress
{
    public static IAnalysisReporter GetReporter(this PipelineContext context) =>
        context.Metadata.TryGetValue(AnalysisContextKeys.Reporter, out var value) && value is IAnalysisReporter reporter
            ? reporter
            : ConsoleAnalysisReporter.Instance;

    /// <summary>
    /// Informa de un mensaje de progreso. Es síncrono a propósito: se llama también desde los
    /// manejadores de salida de procesos externos. <c>EventChannel.SendAsync</c> escribe sin bloquear
    /// y <c>SendEventAsync</c> se traga sus errores, así que el evento no puede tumbar el step.
    /// </summary>
    /// <remarks>
    /// <c>source</c> es el step o componente que emite el mensaje, y el <c>StepName</c> del evento;
    /// <c>channel</c> es uno de <see cref="AnalysisEventTypes.Channels"/>.
    /// </remarks>
    public static void Report(
        this PipelineContext context,
        string source,
        AnalysisLogLevel level,
        string message,
        string channel = AnalysisEventTypes.Channels.Pipeline)
    {
        context.GetReporter().Report(level, source, message);

        _ = context.SendEventAsync(new StepProgressEvent
        {
            StepName = source,
            EventType = AnalysisEventTypes.Log,
            Message = message,
            CorrelationId = context.CorrelationId,
            Metadata = new Dictionary<string, object?>
            {
                [Key.Level] = level.ToWireName(),
                [Key.Channel] = channel
            }
        });
    }

    /// <summary>
    /// Notifica que un informe se ha escrito en disco. No escribe nada en el reporter: los mensajes
    /// visibles de ese momento ya los emite el step que lo escribe.
    /// </summary>
    /// <remarks>
    /// <c>reportType</c> es null para el resumen ejecutivo; <c>truncated</c> indica que el modelo cortó
    /// la respuesta por <c>MaxOutputTokens</c>.
    /// </remarks>
    public static void ReportWritten(
        this PipelineContext context,
        string source,
        string filePath,
        ReportType? reportType,
        string health,
        int? criticalFindings,
        bool truncated)
    {
        var fileName = Path.GetFileName(filePath);
        _ = context.SendEventAsync(new StepProgressEvent
        {
            StepName = source,
            EventType = AnalysisEventTypes.ReportWritten,
            Message = $"{fileName} [{health}]",
            CorrelationId = context.CorrelationId,
            Metadata = new Dictionary<string, object?>
            {
                [Key.FileName] = fileName,
                [Key.FilePath] = filePath,
                [Key.ReportType] = reportType?.ToString(),
                [Key.Health] = health,
                [Key.CriticalFindings] = criticalFindings,
                [Key.Truncated] = truncated
            }
        });
    }

    /// <summary>
    /// Notifica el resultado de verificar un informe. <c>outcome</c> es <c>verified</c>, <c>corrected</c>, <c>warnings</c> o
    /// <c>failed</c>; las versiones solo vienen si el informe se reescribió.
    /// </summary>
    public static void ReportVerified(
        this PipelineContext context,
        string source,
        ReportType reportType,
        string fileName,
        string outcome,
        int changes,
        string message,
        string? previousVersionId = null,
        string? versionId = null,
        string? error = null)
    {
        _ = context.SendEventAsync(new StepProgressEvent
        {
            StepName = source,
            EventType = AnalysisEventTypes.ReportVerified,
            Message = message,
            CorrelationId = context.CorrelationId,
            Metadata = new Dictionary<string, object?>
            {
                [Key.FileName] = fileName,
                [Key.ReportType] = reportType.ToString(),
                [Key.Outcome] = outcome,
                [Key.Changes] = changes,
                [Key.PreviousVersionId] = previousVersionId,
                [Key.VersionId] = versionId,
                [Key.Error] = error
            }
        });
    }
}
