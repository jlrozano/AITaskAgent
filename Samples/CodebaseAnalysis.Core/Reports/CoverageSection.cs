using System.Text;
using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Reports;

/// <summary>
/// El apartado «Cobertura» de un informe en modo B (o de un informe en modo A con plan): lo que el validador comprueba
/// (<see cref="ReportValidationChecks.Coverage"/>) y lo que la síntesis ejecutiva lee para no presentar como completo un análisis
/// que no lo fue. Es el último encabezado «Cobertura» del informe fuera de los bloques de código y llega hasta el siguiente de su
/// nivel o superior.
/// </summary>
public static partial class CoverageSection
{
    /// <summary>Encabezado del apartado: «Cobertura», «9. Cobertura» o «Cobertura del análisis», sin distinguir mayúsculas.</summary>
    [GeneratedRegex(@"^(?:\d+[.)]?\s*)?cobertura\b", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingRegex();

    /// <summary>
    /// Dónde está el apartado: el índice (desde 0) de su encabezado y el de la línea donde acaba (la del siguiente encabezado de su
    /// nivel o superior, o el final). Null si el informe no lo lleva.
    /// </summary>
    public static (int Heading, int End)? Find(string[] lines)
    {
        var headings = MarkdownHeadings.Scan(lines);
        var section = headings.LastOrDefault(h => HeadingRegex().IsMatch(h.Text));
        if (section.Text == null)
            return null;

        var next = headings.FirstOrDefault(h => h.Index > section.Index && h.Level <= section.Level);
        return (section.Index, next.Text == null ? lines.Length : next.Index);
    }

    /// <summary>
    /// El texto del apartado sin su encabezado ni la línea <c>analysis-meta</c>, con las líneas recortadas y sin vacías al principio y
    /// al final, limitado a <paramref name="maxChars"/> (con una marca si se recorta). Null si el informe no lo lleva; vacío si
    /// lo lleva sin contenido.
    /// </summary>
    public static string? Extract(string markdown, int maxChars)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        if (Find(lines) is not { } section)
            return null;

        var text = new StringBuilder();
        foreach (var line in lines[(section.Heading + 1)..section.End])
        {
            var trimmed = line.TrimEnd();
            if (trimmed.TrimStart().StartsWith("<!--", StringComparison.Ordinal))
                continue;
            text.Append(trimmed).Append('\n');
        }

        var result = Regex.Replace(text.ToString().Trim(), @"\n{3,}", "\n\n");
        return result.Length <= maxChars ? result : result[..maxChars].TrimEnd() + "\n[…apartado recortado por tamaño]";
    }
}
