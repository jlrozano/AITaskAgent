using Microsoft.Extensions.Logging;

namespace CodebaseAnalysis.Core.Progress;

/// <summary>Lleva los mensajes de progreso a un <see cref="ILogger"/>, con el nivel equivalente.</summary>
public sealed class LoggerAnalysisReporter(ILogger logger) : IAnalysisReporter
{
    public void Report(AnalysisLogLevel level, string source, string message)
    {
        var logLevel = level switch
        {
            AnalysisLogLevel.Warning => LogLevel.Warning,
            AnalysisLogLevel.Error => LogLevel.Error,
            _ => LogLevel.Information
        };

        logger.Log(logLevel, "[{Source}] {Message}", source, message);
    }
}
