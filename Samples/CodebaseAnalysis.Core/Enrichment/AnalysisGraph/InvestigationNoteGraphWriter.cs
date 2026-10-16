using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodebaseAnalysis.Core.Knowledge.Graph;

namespace CodebaseAnalysis.Core.Enrichment.AnalysisGraph;

/// <summary>Las notas finales de la investigación de un subsistema para un informe.</summary>
public sealed record InvestigationNoteRecord
{
    /// <summary>El informe para el que se investigó (nombre del <c>ReportType</c>).</summary>
    public required string Report { get; init; }

    public required string SubsystemId { get; init; }

    public string SubsystemName { get; init; } = "";

    /// <summary>Las notas (las de la sesión y la respuesta final), con sus citas <c>ruta:línea</c>.</summary>
    public required string Text { get; init; }

    /// <summary>Inicio de la ejecución del análisis que la produjo; null si no se conoce.</summary>
    public DateTimeOffset? RunStartedAt { get; init; }

    /// <summary>La respuesta se cortó al alcanzar el máximo de salida: las notas están incompletas.</summary>
    public bool Truncated { get; init; }
}

/// <summary>Una cita <c>ruta:línea</c> (o <c>ruta:inicio-fin</c>) de una nota.</summary>
/// <param name="Path">La ruta tal como se escribió, con <c>/</c>.</param>
/// <param name="Line">La primera línea citada.</param>
/// <param name="Reference">El texto de la cita (<c>src/A.java:12-18</c>).</param>
/// <param name="Excerpt">La línea de la nota en la que aparece, recortada.</param>
public sealed record NoteCitation(string Path, int Line, string Reference, string Excerpt);

/// <summary>Recuentos de <see cref="InvestigationNoteGraphWriter.WriteAsync"/>.</summary>
public sealed record InvestigationNoteStats
{
    public long NoteId { get; init; }
    public int Citations { get; init; }
    public int CitationsResolved { get; init; }

    /// <summary>Citas sin nodo que contenga la línea, ancladas en el nodo del fichero.</summary>
    public int CitationsFileLevel { get; init; }

    /// <summary>Citas de una ruta que no está en el grafo (o ambigua): se cuentan y se ignoran.</summary>
    public int CitationsUnresolved { get; init; }

    public int CitesEdges { get; init; }

    /// <summary>Si el subsistema tenía nodo y se escribió la arista <c>ABOUT</c>.</summary>
    public bool LinkedToSubsystem { get; init; }

    /// <summary>Nodos de una nota anterior del mismo informe y subsistema que se sustituyeron.</summary>
    public int PreviousRemoved { get; init; }
}

/// <summary>
/// E3 (plan 2026-10-06 §4.5): guarda en la copia del grafo las notas de la investigación de un subsistema. Un nodo
/// <c>InvestigationNote</c> (informe, subsistema, ejecución, texto completo en <c>text</c> y su principio en <c>docstring</c>, que es lo
/// que enseña <c>graph_search</c>), una arista <c>ABOUT</c> hacia el <c>Subsystem</c> (E1) y, por cada nodo de código que contiene una
/// <c>ruta:línea</c> citada (<see cref="CodeNodeLocator"/>), una arista <c>CITES</c> con las citas (<c>refs</c>) y la línea de la nota en
/// que aparecen (<c>excerpts</c>). Las citas de rutas que no están en el grafo se cuentan y se ignoran.
/// </summary>
/// <remarks>
/// Idempotente por (análisis, informe, subsistema): el análisis es la copia del grafo, y el nombre cualificado de la nota
/// (<see cref="AnalysisGraphLabels.NoteQualifiedName"/>) es el informe y el subsistema, así que una investigación nueva sustituye a la
/// anterior. Las escrituras pasan por <see cref="KnowledgeGraphIngestor.WriteGate"/>: las investigaciones corren en paralelo y la
/// ingesta de documentos escribe en la misma BD.
/// </remarks>
public static partial class InvestigationNoteGraphWriter
{
    /// <summary>Caracteres del principio de la nota que van a <c>docstring</c>.</summary>
    public const int DocstringChars = 400;

    /// <summary>Caracteres de cada línea de la nota que se guardan como extracto de una cita.</summary>
    public const int MaxExcerptChars = 300;

    /// <summary>Extractos por arista <c>CITES</c>.</summary>
    public const int MaxExcerptsPerEdge = 3;

    /// <summary>Citas que se procesan por nota; el resto se cuentan como no resueltas.</summary>
    public const int MaxCitations = 500;

    /// <summary>Las citas <c>ruta:línea</c> del texto, en orden de aparición (la ruta tiene que llevar extensión).</summary>
    public static IReadOnlyList<NoteCitation> ParseCitations(string text)
    {
        var citations = new List<NoteCitation>();
        foreach (var line in text.Split('\n'))
        {
            foreach (Match m in CitationPattern().Matches(line))
            {
                if (!int.TryParse(m.Groups["start"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var start) || start <= 0)
                    continue;
                var excerpt = line.Trim();
                if (excerpt.Length > MaxExcerptChars)
                    excerpt = excerpt[..MaxExcerptChars] + "…";
                citations.Add(new NoteCitation(m.Groups["path"].Value.Replace('\\', '/'), start, m.Value, excerpt));
            }
        }
        return citations;
    }

    /// <exception cref="InvalidOperationException">Si la BD es la de cbm.</exception>
    public static async Task<InvestigationNoteStats> WriteAsync(string databasePath, InvestigationNoteRecord note, CancellationToken ct, DateTimeOffset? at = null)
    {
        var when = at ?? DateTimeOffset.UtcNow;
        var citations = ParseCitations(note.Text);
        var processed = citations.Take(MaxCitations).ToList();

        await KnowledgeGraphIngestor.WriteGate.WaitAsync(ct);
        try
        {
            using var store = await AnalysisGraphStore.OpenAsync(databasePath, ct);
            var qualifiedName = AnalysisGraphLabels.NoteQualifiedName(note.Report, note.SubsystemId);
            var (previous, _) = await EnrichmentMarks.RemoveNodesAsync(store.Connection, store.Transaction,
                "SELECT id FROM nodes WHERE label = @label AND qualified_name = @qn", p =>
                {
                    p.AddWithValue("@label", AnalysisGraphLabels.InvestigationNote);
                    p.AddWithValue("@qn", qualifiedName);
                }, ct);

            var locator = await CodeNodeLocator.LoadForCitationsAsync(store.Connection, store.Transaction, processed.Select(c => c.Path), ct);
            var byNode = new Dictionary<long, (SortedSet<string> Refs, List<string> Excerpts)>();
            int resolved = 0, fileLevel = 0, unresolved = citations.Count - processed.Count;
            foreach (var citation in processed)
            {
                if (locator.Locate(citation.Path, citation.Line) is not { } node)
                {
                    unresolved++;
                    continue;
                }
                if (node.FileLevel)
                    fileLevel++;
                else
                    resolved++;
                if (!byNode.TryGetValue(node.Id, out var entry))
                    byNode[node.Id] = entry = (new SortedSet<string>(StringComparer.Ordinal), []);
                entry.Refs.Add(citation.Reference);
                if (entry.Excerpts.Count < MaxExcerptsPerEdge && !entry.Excerpts.Contains(citation.Excerpt))
                    entry.Excerpts.Add(citation.Excerpt);
            }

            var text = note.Text.Trim();
            var name = string.IsNullOrWhiteSpace(note.SubsystemName) ? $"{note.Report} · {note.SubsystemId}" : $"{note.Report} · {note.SubsystemName}";
            var noteId = await store.InsertNodeAsync(AnalysisGraphLabels.InvestigationNote, name, qualifiedName, AnalysisGraphLabels.NoteEnricher, when,
                new JsonObject
                {
                    ["report"] = note.Report,
                    ["subsystem_id"] = note.SubsystemId,
                    ["subsystem_name"] = note.SubsystemName,
                    ["run_started_at"] = note.RunStartedAt?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                    ["written_at"] = when.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                    ["truncated"] = note.Truncated,
                    ["char_count"] = text.Length,
                    ["docstring"] = text.Length > DocstringChars ? text[..DocstringChars] + "…" : text,
                    ["text"] = text,
                    ["citations"] = citations.Count,
                    ["citations_resolved"] = resolved,
                    ["citations_file_level"] = fileLevel,
                    ["citations_unresolved"] = unresolved
                }, ct);

            var linked = false;
            using (var find = store.Command("SELECT id FROM nodes WHERE label = @label AND qualified_name = @qn LIMIT 1"))
            {
                find.Parameters.AddWithValue("@label", AnalysisGraphLabels.Subsystem);
                find.Parameters.AddWithValue("@qn", AnalysisGraphLabels.SubsystemQualifiedName(note.SubsystemId));
                if (await find.ExecuteScalarAsync(ct) is long subsystemId)
                    linked = await store.InsertEdgeAsync(noteId, subsystemId, AnalysisGraphLabels.About, AnalysisGraphLabels.NoteEnricher, null, ct);
            }

            var cites = 0;
            foreach (var (target, (refs, excerpts)) in byNode.OrderBy(e => e.Key))
            {
                if (await store.InsertEdgeAsync(noteId, target, AnalysisGraphLabels.Cites, AnalysisGraphLabels.NoteEnricher, new JsonObject
                    {
                        ["report"] = note.Report,
                        ["refs"] = AnalysisGraphStore.ToArray(refs),
                        ["excerpts"] = AnalysisGraphStore.ToArray(excerpts),
                        ["count"] = refs.Count
                    }, ct))
                    cites++;
            }

            store.Commit();
            return new InvestigationNoteStats
            {
                NoteId = noteId,
                Citations = citations.Count,
                CitationsResolved = resolved,
                CitationsFileLevel = fileLevel,
                CitationsUnresolved = unresolved,
                CitesEdges = cites,
                LinkedToSubsystem = linked,
                PreviousRemoved = previous
            };
        }
        finally
        {
            KnowledgeGraphIngestor.WriteGate.Release();
        }
    }

    // Una ruta con extensión seguida de :línea o :inicio-fin; no puede ir pegada a otra palabra por delante (un esquema de URL, otra ruta).
    [GeneratedRegex(@"(?<![\w/\\.\-])(?<path>(?:[A-Za-z]:[\\/])?[\w$.\-/\\]*[\w$\-]\.[A-Za-z][A-Za-z0-9]{0,9}):(?<start>\d{1,6})(?:\s?[-–]\s?(?<end>\d{1,6}))?", RegexOptions.CultureInvariant)]
    private static partial Regex CitationPattern();
}
