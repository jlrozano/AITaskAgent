using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Support;

/// <summary>
/// Correcciones deterministas sobre el Markdown que devuelve el modelo antes de guardarlo: lo que se puede
/// comprobar con el propio texto no se deja a que el modelo lo haga bien.
/// </summary>
public static partial class ReportMarkdown
{
    [GeneratedRegex(@"<!--\s*analysis-meta\s*:(?<body>.*?)-->", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnalysisMetaRegex();

    // Cabecera de una columna de severidad: "Severidad", "Criticidad", "Prioridad (P0-P3)", "Severity"...
    [GeneratedRegex(@"\b(severidad|criticidad|prioridad|severity|priority)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeverityHeaderRegex();

    [GeneratedRegex(@"^(id|identificador|c[oó]digo|ref|referencia|hallazgo)$", RegexOptions.IgnoreCase)]
    private static partial Regex IdHeaderRegex();

    [GeneratedRegex(@"\bP([0-3])\b")]
    private static partial Regex SeverityValueRegex();

    [GeneratedRegex(@"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?$")]
    private static partial Regex SeparatorRowRegex();

    [GeneratedRegex(@"^([A-Z]+)[-_ ]?0*(\d+)$")]
    private static partial Regex FindingIdRegex();

    // H1 que solo es un nombre de fichero: "# 00_resumen_ejecutivo.md" o "# `00_resumen_ejecutivo.md`".
    [GeneratedRegex(@"^#\s+`?[\w.\-]+\.md`?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FileNameHeadingRegex();

    /// <summary>
    /// Hallazgos P0 o P1 de las tablas de severidad del informe (tablas Markdown con una columna cuya cabecera
    /// es severidad, criticidad o prioridad). Con columna de identificador, cada hallazgo cuenta una vez
    /// aunque aparezca en varias tablas o con otra numeración (<c>SEC-001</c> y <c>SEC-01</c> son el mismo).
    /// Null si el informe no tiene ninguna tabla de severidad.
    /// </summary>
    public static int? CountCriticalFindings(string markdown)
    {
        var tables = ParseTables(markdown)
            .Select(t => (Table: t, Severity: t.Header.FindIndex(h => SeverityHeaderRegex().IsMatch(CleanCell(h))), Id: t.Header.FindIndex(h => IdHeaderRegex().IsMatch(CleanCell(h)))))
            .Where(t => t.Severity >= 0)
            .ToList();
        if (tables.Count == 0)
            return null;

        var withIds = tables.Where(t => t.Id >= 0).ToList();
        if (withIds.Count > 0)
        {
            var critical = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (table, severity, id) in withIds)
            {
                foreach (var row in table.Rows)
                {
                    if (IsCritical(Cell(row, severity)) && NormalizeId(Cell(row, id)) is { Length: > 0 } key)
                        critical.Add(key);
                }
            }
            return critical.Count;
        }

        // Sin identificadores no se puede deduplicar entre tablas: vale la tabla con más filas críticas.
        return tables.Max(t => t.Table.Rows.Count(r => IsCritical(Cell(r, t.Severity))));
    }

    /// <summary>La última línea <c>analysis-meta</c> (la que vale), entera; null si no hay.</summary>
    public static string? GetLastMetaLine(string markdown)
    {
        var matches = AnalysisMetaRegex().Matches(markdown);
        return matches.Count == 0 ? null : matches[^1].Value;
    }

    /// <summary>Índice de la última línea <c>analysis-meta</c>, o -1 si no hay.</summary>
    public static int LastMetaIndex(string markdown)
    {
        var matches = AnalysisMetaRegex().Matches(markdown);
        return matches.Count == 0 ? -1 : matches[^1].Index;
    }

    /// <summary>
    /// Fija <c>critical=<paramref name="critical"/></c> en la última línea <c>analysis-meta</c> (la que vale),
    /// conservando el resto de claves; si no hay línea, la añade al final.
    /// </summary>
    public static string SetCriticalInMeta(string markdown, int critical)
    {
        var matches = AnalysisMetaRegex().Matches(markdown);
        if (matches.Count == 0)
            return markdown.TrimEnd() + $"\n\n<!-- analysis-meta: critical={critical} -->\n";

        var last = matches[^1];
        var pairs = last.Groups["body"].Value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p =>
            {
                var separator = p.IndexOf('=');
                return separator <= 0 || !p[..separator].Trim().Equals("critical", StringComparison.OrdinalIgnoreCase);
            })
            .Append($"critical={critical}");
        var line = $"<!-- analysis-meta: {string.Join("; ", pairs)} -->";
        return string.Concat(markdown.AsSpan(0, last.Index), line, markdown.AsSpan(last.Index + last.Length));
    }

    /// <summary>
    /// Quita un H1 inicial que solo repite el nombre del fichero cuando detrás hay otro H1: el modelo a veces
    /// abre con "# 00_resumen_ejecutivo.md" y después pone el título real.
    /// </summary>
    public static string RemoveFileNameHeading(string markdown)
    {
        var lines = markdown.Split('\n');
        var first = Array.FindIndex(lines, l => !string.IsNullOrWhiteSpace(l));
        if (first < 0 || !FileNameHeadingRegex().IsMatch(lines[first].TrimEnd('\r')))
            return markdown;

        var hasAnotherH1 = lines.Skip(first + 1).Any(l => l.StartsWith("# ", StringComparison.Ordinal));
        if (!hasAnotherH1)
            return markdown;

        var next = first + 1;
        while (next < lines.Length && string.IsNullOrWhiteSpace(lines[next]))
            next++;

        return string.Join('\n', lines.Take(first).Concat(lines.Skip(next)));
    }

    private sealed record MarkdownTable(List<string> Header, List<List<string>> Rows);

    private static List<MarkdownTable> ParseTables(string markdown)
    {
        var tables = new List<MarkdownTable>();
        var lines = markdown.Split('\n').Select(l => l.TrimEnd('\r').Trim()).ToArray();
        var inFence = false;

        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence || !lines[i].StartsWith('|') || i + 1 >= lines.Length || !SeparatorRowRegex().IsMatch(lines[i + 1]))
                continue;

            var header = SplitRow(lines[i]);
            var rows = new List<List<string>>();
            var j = i + 2;
            for (; j < lines.Length && lines[j].StartsWith('|'); j++)
                rows.Add(SplitRow(lines[j]));

            tables.Add(new MarkdownTable(header, rows));
            i = j - 1;
        }

        return tables;
    }

    private static List<string> SplitRow(string line)
    {
        var body = line.Trim();
        if (body.StartsWith('|'))
            body = body[1..];
        if (body.EndsWith('|'))
            body = body[..^1];
        return body.Split('|').Select(c => c.Trim()).ToList();
    }

    private static string Cell(List<string> row, int index) => index < row.Count ? row[index] : string.Empty;

    private static string CleanCell(string cell) => cell.Replace("*", "").Replace("`", "").Trim();

    /// <summary>P0 o P1 en la celda; con varias (p. ej. "P1/P2") vale la más grave.</summary>
    private static bool IsCritical(string cell)
    {
        var levels = SeverityValueRegex().Matches(cell).Select(m => m.Groups[1].Value[0] - '0').ToList();
        return levels.Count > 0 && levels.Min() <= 1;
    }

    private static string NormalizeId(string cell)
    {
        var clean = new string(CleanCell(cell).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ').ToArray()).Trim().ToUpperInvariant();
        var match = FindingIdRegex().Match(clean);
        return match.Success ? $"{match.Groups[1].Value}-{int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)}" : clean;
    }
}
