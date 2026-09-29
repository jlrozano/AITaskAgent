using CodebaseAnalysis.Core.Documents;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Knowledge.Graph;
using Microsoft.Data.Sqlite;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Steps;

/// <summary>
/// Faceta <c>ProvidedDocumentation</c> (plan v2 §7.8): JSON con los documentos de <c>useInAnalysis</c> (30 como
/// máximo y <c>AnalysisIndexMaxChars</c> en total), con id, título, familia, títulos de sección, los 600 primeros
/// caracteres de <c>content.md</c> y, si está en el grafo, los símbolos enlazados por <c>DOC_DESCRIBES</c>. Cualquier
/// <c>@[</c> se neutraliza con un espacio de anchura cero, porque el renderer resuelve <c>@[…]</c> sobre el resultado
/// ya interpolado. Sin documentos, <c>[]</c>.
/// </summary>
public static class ProvidedDocumentationFacet
{
    public const int MaxDocuments = 30;
    public const int ExcerptChars = 600;
    public const int MaxSymbols = 20;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<string> BuildAsync(KnowledgePaths paths, SqliteConnection? graph, int maxChars, CancellationToken ct)
    {
        var store = new DocumentStore(paths);
        var documents = store.List().Where(d => d.UseInAnalysis).OrderBy(d => d.UploadedAt).Take(MaxDocuments).ToList();
        if (documents.Count == 0)
            return "[]";

        var entries = new List<Dictionary<string, object?>>();
        var used = 2;
        foreach (var d in documents)
        {
            var content = store.ReadContent(d.Id).Trim();
            var excerpt = content.Length > ExcerptChars ? content[..ExcerptChars] + "…" : content;
            IReadOnlyList<string> symbols = [];
            if (graph != null)
            {
                try
                {
                    symbols = await KnowledgeGraphIngestor.LinkedSymbolsAsync(graph, SourceRef.Document(d.Id), MaxSymbols, ct);
                }
                catch (SqliteException)
                {
                }
            }

            var entry = new Dictionary<string, object?>
            {
                ["id"] = d.Id,
                ["title"] = d.Title,
                ["family"] = d.Family,
                ["sections"] = d.Sections.Select(s => s.Heading).Take(30).ToList(),
                ["excerpt"] = excerpt,
                ["linked_symbols"] = symbols
            };

            var size = JsonSerializer.Serialize(entry, Json).Length + 1;
            if (used + size > maxChars)
            {
                // Sin extracto cabe más índice; si ni así cabe, se para.
                entry["excerpt"] = null;
                size = JsonSerializer.Serialize(entry, Json).Length + 1;
                if (used + size > maxChars)
                    break;
            }
            entries.Add(entry);
            used += size;
        }

        return Neutralize(JsonSerializer.Serialize(entries, Json));
    }

    /// <summary>Rompe cualquier <c>@[</c> con un espacio de anchura cero.</summary>
    public static string Neutralize(string text) => text.Replace("@[", "@​[", StringComparison.Ordinal);
}
