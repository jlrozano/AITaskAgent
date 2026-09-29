using CodebaseAnalysis.Core.Documents;
using CodebaseAnalysis.Core.Documents.Extraction;

namespace CodebaseAnalysis.Core.Knowledge.Embeddings;

/// <summary>Un trozo para embeddings: texto con su prefijo de contexto y las líneas de <c>content.md</c> que cubre.</summary>
public sealed record Chunk(int Ordinal, string Heading, string Text, int StartLine, int EndLine);

/// <summary>
/// Troceo para embeddings (plan v2 §7.3): por sección; dentro, por párrafos hasta <c>MaxChunkChars</c> con
/// <c>ChunkOverlapChars</c> de solape; código en ventanas de 80 líneas con 10 de solape; tablas por filas repitiendo la
/// cabecera. Cada trozo lleva delante «título › sección». Como mucho <see cref="MaxChunksPerSource"/> por fuente.
/// </summary>
public static class Chunker
{
    public const int MaxChunksPerSource = 2000;
    public const int CodeWindowLines = 80;
    public const int CodeOverlapLines = 10;

    public static IReadOnlyList<Chunk> Split(KnowledgeSourceContent source, int maxChars = 1800, int overlapChars = 200)
    {
        var lines = source.Content.Split('\n');
        var sections = source.Sections.Count > 0 ? source.Sections : MarkdownSections.Whole(source.Content, source.Title);
        var chunks = new List<Chunk>();
        foreach (var section in sections)
        {
            var start = Math.Clamp(section.StartLine - 1, 0, lines.Length);
            var end = Math.Clamp(section.EndLine, start, lines.Length);
            var sectionLines = lines[start..end];
            var prefix = $"{source.Title} › {section.Heading}\n\n";
            var pieces = IsCode(source, sectionLines) ? CodeWindows(sectionLines)
                : IsTable(sectionLines) ? TableRows(sectionLines, maxChars)
                : Paragraphs(sectionLines, maxChars, overlapChars);
            foreach (var (text, from, to) in pieces)
            {
                if (text.Trim().Length == 0)
                    continue;
                chunks.Add(new Chunk(chunks.Count + 1, section.Heading, prefix + text.Trim('\n'), start + from + 1, start + to + 1));
                if (chunks.Count >= MaxChunksPerSource)
                    return chunks;
            }
        }
        return chunks;
    }

    private static bool IsCode(KnowledgeSourceContent source, string[] lines) =>
        source.Family is DocumentFamilies.Code or DocumentFamilies.Structured && lines.Length > 0 && lines[0].StartsWith("```", StringComparison.Ordinal);

    private static bool IsTable(string[] lines)
    {
        var nonEmpty = lines.Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')).ToList();
        return nonEmpty.Count >= 3 && nonEmpty.Count(l => l.TrimStart().StartsWith('|')) >= nonEmpty.Count * 0.8;
    }

    /// <summary>Ventanas de líneas (índices relativos a la sección, ambos inclusive).</summary>
    private static IEnumerable<(string Text, int From, int To)> CodeWindows(string[] lines)
    {
        for (var from = 0; from < lines.Length; from += CodeWindowLines - CodeOverlapLines)
        {
            var to = Math.Min(lines.Length, from + CodeWindowLines) - 1;
            yield return (string.Join('\n', lines[from..(to + 1)]), from, to);
            if (to == lines.Length - 1)
                yield break;
        }
    }

    private static IEnumerable<(string Text, int From, int To)> TableRows(string[] lines, int maxChars)
    {
        var headerIndex = Array.FindIndex(lines, l => l.TrimStart().StartsWith('|'));
        if (headerIndex < 0 || headerIndex + 1 >= lines.Length)
        {
            yield return (string.Join('\n', lines), 0, Math.Max(0, lines.Length - 1));
            yield break;
        }

        var header = lines[headerIndex] + "\n" + lines[headerIndex + 1] + "\n";
        var body = new System.Text.StringBuilder(header);
        var from = headerIndex + 2;
        for (var i = headerIndex + 2; i < lines.Length; i++)
        {
            if (body.Length + lines[i].Length + 1 > maxChars && body.Length > header.Length)
            {
                yield return (body.ToString(), from, i - 1);
                body.Clear().Append(header);
                from = i;
            }
            body.Append(lines[i]).Append('\n');
        }
        if (body.Length > header.Length)
            yield return (body.ToString(), from, lines.Length - 1);
    }

    private static IEnumerable<(string Text, int From, int To)> Paragraphs(string[] lines, int maxChars, int overlapChars)
    {
        var current = new System.Text.StringBuilder();
        var from = 0;
        var overlap = string.Empty;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            // Una línea más larga que el máximo se corta a trozos.
            while (line.Length > maxChars)
            {
                if (current.Length > 0)
                {
                    yield return (current.ToString(), from, Math.Max(from, i - 1));
                    overlap = Tail(current.ToString(), overlapChars);
                    current.Clear();
                }
                yield return (overlap + line[..maxChars], i, i);
                overlap = Tail(line[..maxChars], overlapChars);
                line = line[maxChars..];
                from = i;
            }

            var paragraphBreak = line.Trim().Length == 0;
            if (current.Length + line.Length + 1 > maxChars && current.Length > 0)
            {
                yield return (current.ToString(), from, Math.Max(from, i - 1));
                overlap = Tail(current.ToString(), overlapChars);
                current.Clear().Append(overlap);
                from = i;
            }
            else if (paragraphBreak && current.Length >= maxChars * 0.75)
            {
                yield return (current.ToString(), from, i);
                overlap = Tail(current.ToString(), overlapChars);
                current.Clear().Append(overlap);
                from = i + 1;
                continue;
            }

            if (current.Length == 0)
                from = i;
            current.Append(line).Append('\n');
        }

        if (current.ToString().Trim().Length > overlap.Trim().Length || overlap.Length == 0 && current.Length > 0)
            yield return (current.ToString(), from, Math.Max(from, lines.Length - 1));
    }

    private static string Tail(string text, int chars)
    {
        if (chars <= 0 || text.Length <= chars)
            return chars <= 0 ? string.Empty : text;
        var tail = text[^chars..];
        var space = tail.IndexOf(' ');
        return space > 0 && space < tail.Length - 1 ? tail[(space + 1)..] : tail;
    }
}
