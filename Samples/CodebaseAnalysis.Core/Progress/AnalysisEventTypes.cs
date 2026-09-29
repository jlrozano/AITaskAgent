namespace CodebaseAnalysis.Core.Progress;

/// <summary>
/// Valores de <c>StepProgressEvent.EventType</c> que emite el pipeline, y las claves de su
/// <c>Metadata</c>.
/// </summary>
public static class AnalysisEventTypes
{
    /// <summary>Mensaje de progreso (<see cref="AnalysisProgress.Report"/>).</summary>
    public const string Log = "analysis.log";

    /// <summary>Un informe se ha escrito en disco (<see cref="AnalysisProgress.ReportWritten"/>).</summary>
    public const string ReportWritten = "analysis.report_written";

    /// <summary>
    /// El chat creó o modificó un informe (<c>create_report</c>, <c>update_report</c>). Su <c>Metadata</c> lleva los campos
    /// de <c>ReportChange</c> (plan v2 §5.3) y <c>ChatEventMapper</c> lo convierte en el evento SSE <c>report.changed</c>.
    /// </summary>
    public const string ChatReportChanged = "chat.report_changed";

    public static class MetadataKeys
    {
        public const string Level = "level";
        public const string Channel = "channel";
        public const string FileName = "fileName";
        public const string FilePath = "filePath";
        public const string ReportType = "reportType";
        public const string Health = "health";
        public const string CriticalFindings = "criticalFindings";
        public const string Truncated = "truncated";
    }

    /// <summary>Valores de <see cref="MetadataKeys.Channel"/>.</summary>
    public static class Channels
    {
        /// <summary>Mensajes del propio pipeline.</summary>
        public const string Pipeline = "pipeline";

        /// <summary>Líneas de stdout/stderr del indexador externo.</summary>
        public const string Indexer = "indexer";
    }

    /// <summary>Nombre del nivel tal como viaja en <see cref="MetadataKeys.Level"/>.</summary>
    public static string ToWireName(this AnalysisLogLevel level) => level switch
    {
        AnalysisLogLevel.Success => "success",
        AnalysisLogLevel.Warning => "warning",
        AnalysisLogLevel.Error => "error",
        _ => "info"
    };
}
