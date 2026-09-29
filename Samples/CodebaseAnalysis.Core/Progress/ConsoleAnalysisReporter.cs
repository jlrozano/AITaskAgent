namespace CodebaseAnalysis.Core.Progress;

/// <summary>
/// Escribe cada mensaje en consola exactamente como llega, sin prefijos ni nivel: es la salida que la
/// CLI mostraba antes de que los steps pasaran por un reporter.
/// </summary>
public sealed class ConsoleAnalysisReporter : IAnalysisReporter
{
    public static ConsoleAnalysisReporter Instance { get; } = new();

    public void Report(AnalysisLogLevel level, string source, string message) => Console.WriteLine(message);
}
