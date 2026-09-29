using System.Globalization;

namespace CodebaseAnalysis.Core.Progress;

/// <summary>
/// Escribe cada mensaje en consola con la hora local (<c>HH:mm:ss</c>) y su origen, el paso o el auditor que lo emite, delante:
/// <c>12:31:39 [Auditor_CodeSmells] 🤖 [LLM] Iniciando auditoría…</c>. El mensaje va tal cual; sin origen, solo la hora. Con
/// nueve auditores en paralelo, sin el origen no se sabía de quién era cada línea.
/// </summary>
public sealed class ConsoleAnalysisReporter(TimeProvider? clock = null) : IAnalysisReporter
{
    public static ConsoleAnalysisReporter Instance { get; } = new();

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public void Report(AnalysisLogLevel level, string source, string message) =>
        Console.WriteLine(Format(_clock.GetLocalNow(), source, message));

    /// <summary>La línea de consola de un mensaje: hora, origen entre corchetes si lo hay, y el mensaje.</summary>
    public static string Format(DateTimeOffset at, string? source, string message)
    {
        var time = at.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(source) ? $"{time} {message}" : $"{time} [{source}] {message}";
    }
}
