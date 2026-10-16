using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Reports;

/// <summary>Un rango de líneas citado en un hallazgo (<c>ruta/F.cs:48-60</c>).</summary>
public sealed record FindingSpan(string Path, int Start, int End);

/// <summary>Un hallazgo de un informe: identificador, severidad (P0-P3, 0 la más grave) y el código que cita.</summary>
public sealed record FindingSummary(string Id, int? Priority, IReadOnlyList<FindingSpan> Spans);

/// <summary>
/// Dos hallazgos de informes distintos que citan el mismo código (o código a menos de <see cref="SeverityContrast.LineGap"/> líneas)
/// con severidades que difieren en <see cref="SeverityContrast.MinLevelGap"/> niveles o más, siendo la grave P0 o P1.
/// </summary>
public sealed record SeverityDiscrepancy(
    string SevereReport, string SevereId, int SeverePriority, FindingSpan SevereSpan,
    string OtherReport, string OtherId, int OtherPriority, FindingSpan OtherSpan);

/// <summary>
/// Contraste determinista de severidades entre informes (sin LLM): los informes se generan en paralelo y cada uno califica con su
/// criterio, así que el mismo código puede salir P0 en uno y P3 en otro. Aquí solo se detectan esas parejas; qué severidad prevalece
/// lo decide quien las lee con el código delante.
/// </summary>
public static partial class SeverityContrast
{
    /// <summary>Distancia máxima entre dos rangos citados del mismo fichero para considerar que hablan del mismo código.</summary>
    public const int LineGap = 40;

    /// <summary>Diferencia mínima de niveles (P1 frente a P3, P0 frente a P2): un nivel de diferencia es una opinión, dos es una contradicción.</summary>
    public const int MinLevelGap = 2;

    [GeneratedRegex(@"(?<path>[A-Za-z0-9_./\\-]+\.[A-Za-z][A-Za-z0-9]{0,4}):(?<start>\d+)(?:\s*[-–]\s*(?<end>\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex SpanRegex();

    /// <summary>Los rangos <c>ruta:línea</c> o <c>ruta:ini-fin</c> de un texto, con la ruta normalizada (<c>/</c>, sin <c>./</c>).</summary>
    public static IReadOnlyList<FindingSpan> SpansOf(string text)
    {
        var spans = new List<FindingSpan>();
        foreach (Match m in SpanRegex().Matches(text))
        {
            var path = m.Groups["path"].Value.Replace('\\', '/').TrimStart('.', '/');
            if (path.Length == 0 || !int.TryParse(m.Groups["start"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var start))
                continue;
            var end = m.Groups["end"].Success && int.TryParse(m.Groups["end"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var e) ? e : start;
            spans.Add(new FindingSpan(path, Math.Min(start, end), Math.Max(start, end)));
        }
        return spans;
    }

    /// <summary>
    /// Las parejas de hallazgos de informes distintos que citan el mismo código con severidades que difieren en dos niveles o más, con un P0 o P1.
    /// Una pareja por par de identificadores, con el primer rango coincidente; orden por severidad del grave y por identificador.
    /// </summary>
    /// <param name="reports">Nombre de fichero del informe y sus hallazgos. El resumen ejecutivo no debe incluirse.</param>
    public static IReadOnlyList<SeverityDiscrepancy> Find(IEnumerable<(string Report, IReadOnlyList<FindingSummary> Findings)> reports)
    {
        var all = reports.SelectMany(r => r.Findings
            .Where(f => f.Priority is not null && f.Spans.Count > 0)
            .Select(f => (r.Report, Finding: f))).ToList();
        var result = new List<SeverityDiscrepancy>();
        var seen = new HashSet<(string, string)>();

        foreach (var severe in all.Where(x => x.Finding.Priority <= 1))
        {
            foreach (var other in all.Where(x => !string.Equals(x.Report, severe.Report, StringComparison.OrdinalIgnoreCase)
                                                 && x.Finding.Priority >= severe.Finding.Priority + MinLevelGap))
            {
                if (!seen.Add((severe.Finding.Id, other.Finding.Id)))
                    continue;
                foreach (var a in severe.Finding.Spans)
                {
                    var b = other.Finding.Spans.FirstOrDefault(s => Near(a, s));
                    if (b is null)
                        continue;
                    result.Add(new SeverityDiscrepancy(
                        severe.Report, severe.Finding.Id, severe.Finding.Priority!.Value, a,
                        other.Report, other.Finding.Id, other.Finding.Priority!.Value, b));
                    break;
                }
            }
        }

        return [.. result.OrderBy(d => d.SeverePriority).ThenBy(d => d.SevereId, StringComparer.Ordinal).ThenBy(d => d.OtherId, StringComparer.Ordinal)];
    }

    private static bool Near(FindingSpan a, FindingSpan b) =>
        string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase) && a.Start <= b.End + LineGap && b.Start <= a.End + LineGap;

    /// <summary>El texto para el digest de la síntesis; vacío si no hay discrepancias.</summary>
    public static string Format(IReadOnlyList<SeverityDiscrepancy> discrepancies)
    {
        if (discrepancies.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("### Discrepancias de severidad entre informes (detectadas por código, sin leer el código fuente)");
        sb.AppendLine("Cada línea es un hallazgo P0 o P1 de un informe que cita el mismo código que otro hallazgo de otro informe con una severidad al menos dos niveles menos grave:");
        foreach (var d in discrepancies)
        {
            sb.AppendLine(
                $"- `{d.SevereId}` (P{d.SeverePriority}, {d.SevereReport}, `{Range(d.SevereSpan)}`) frente a `{d.OtherId}` (P{d.OtherPriority}, {d.OtherReport}, `{Range(d.OtherSpan)}`).");
        }
        sb.AppendLine();
        return sb.ToString();
    }

    private static string Range(FindingSpan span) =>
        span.Start == span.End ? $"{span.Path}:{span.Start}" : $"{span.Path}:{span.Start}-{span.End}";
}
