using CodebaseAnalysis.Core.Reports;

namespace CodebaseAnalysis.Core.Documents.Extraction;

/// <summary>Lo que produce un extractor: el <c>content.md</c> normalizado, sus secciones y el estado (plan v2 §7.2, §7.3).</summary>
public sealed record ExtractionResult(
    string Content,
    IReadOnlyList<DocumentSection> Sections,
    string Status,
    int? PageCount,
    bool Truncated,
    IReadOnlyList<string> Warnings)
{
    public DocumentExtractionInfo ToInfo() => new(Status, Content.Length, Sections.Count, PageCount, Truncated, Warnings);

    public static ExtractionResult Failed(string reason) =>
        new(string.Empty, [], ExtractionStatuses.Failed, null, false, [reason]);
}

/// <summary>
/// Secciones de un Markdown (plan v2 §7.3): encabezados de nivel 1 a 3 fuera de bloques de código, con su rango de
/// líneas. El texto previo al primer encabezado es una sección con el título del documento; sin encabezados, una sola.
/// </summary>
public static class MarkdownSections
{
    public static IReadOnlyList<DocumentSection> Parse(string content, string fallbackTitle)
    {
        var lines = content.Split('\n');
        var headings = MarkdownHeadings.Scan(lines).Where(h => h.Level <= 3).ToList();
        var starts = new List<(int Index, int Level, string Text)>();
        var firstHeading = headings.Count > 0 ? headings[0].Index : lines.Length;
        if (lines.Take(firstHeading).Any(l => l.Trim().Length > 0))
            starts.Add((0, 1, fallbackTitle));
        starts.AddRange(headings);

        var result = new List<DocumentSection>();
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1].Index : lines.Length;
            result.Add(new DocumentSection(i + 1, starts[i].Text, starts[i].Level, starts[i].Index + 1, Math.Max(starts[i].Index + 1, end)));
        }

        return result;
    }

    /// <summary>Secciones de un texto sin encabezados: párrafos agrupados hasta unos <paramref name="maxChars"/> caracteres («Parte N»).</summary>
    public static IReadOnlyList<DocumentSection> GroupParagraphs(string content, int maxChars = 4000)
    {
        var lines = content.Split('\n');
        var result = new List<DocumentSection>();
        var start = 0;
        var chars = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            chars += lines[i].Length + 1;
            var paragraphEnd = lines[i].Trim().Length == 0;
            if (chars >= maxChars && paragraphEnd || i == lines.Length - 1)
            {
                result.Add(new DocumentSection(result.Count + 1, $"Parte {result.Count + 1}", 2, start + 1, i + 1));
                start = i + 1;
                chars = 0;
            }
        }

        return result;
    }

    /// <summary>Una sección con todo el contenido.</summary>
    public static IReadOnlyList<DocumentSection> Whole(string content, string heading) =>
        [new DocumentSection(1, heading, 1, 1, Math.Max(1, content.Split('\n').Length))];

    /// <summary>Texto de una sección (sus líneas).</summary>
    public static string Slice(string content, DocumentSection section)
    {
        var lines = content.Split('\n');
        var start = Math.Clamp(section.StartLine - 1, 0, lines.Length);
        var end = Math.Clamp(section.EndLine, start, lines.Length);
        return string.Join('\n', lines[start..end]);
    }
}
