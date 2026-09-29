namespace CodebaseAnalysis.Core.Progress;

/// <summary>Gravedad de un mensaje de progreso del análisis.</summary>
public enum AnalysisLogLevel
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// Destino de los mensajes de progreso que emite el pipeline (antes, escrituras directas a consola
/// repartidas por los steps). La CLI los escribe en consola tal cual; otro host puede llevarlos a un logger.
/// </summary>
public interface IAnalysisReporter
{
    /// <param name="level">Gravedad del mensaje.</param>
    /// <param name="source">Nombre del step o componente que lo emite.</param>
    /// <param name="message">Texto listo para mostrar; el reporter no debe alterarlo.</param>
    void Report(AnalysisLogLevel level, string source, string message);
}
