using CodebaseAnalysis.Core.Documents;
using CodebaseAnalysis.Core.Documents.Extraction;
using CodebaseAnalysis.Core.Reports;

namespace CodebaseAnalysis.Core.Knowledge;

/// <summary>
/// El contenido de una fuente tal como lo ven el grafo y los embeddings: su texto normalizado (<c>content.md</c> de un
/// documento, el fichero de un informe), secciones, título y el SHA-256 de los bytes en disco, que decide si una fuente
/// indexada está al día.
/// </summary>
public sealed record KnowledgeSourceContent(
    SourceRef Source,
    string Title,
    string Content,
    string Sha256,
    string MediaType,
    string Family,
    string ContentRelativePath,
    IReadOnlyList<DocumentSection> Sections);

public static class KnowledgeSourceReader
{
    /// <summary>El contenido actual de la fuente, o null si ya no existe (o un documento no tiene texto).</summary>
    public static KnowledgeSourceContent? TryRead(KnowledgePaths paths, SourceRef source)
    {
        if (source.Kind == SourceKind.Document)
        {
            var store = new DocumentStore(paths);
            var record = store.TryGet(source.Id);
            var contentPath = store.ContentPath(source.Id);
            if (record == null || !File.Exists(contentPath))
                return null;

            var bytes = File.ReadAllBytes(contentPath);
            return new KnowledgeSourceContent(
                source,
                record.Title,
                System.Text.Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n"),
                ReportHistoryStore.Sha256(bytes),
                record.MediaType,
                record.Family,
                Relative(paths, contentPath),
                record.Sections);
        }

        var path = ReportLibrary.GetFilePath(paths, source);
        if (!File.Exists(path))
            return null;

        var reportBytes = File.ReadAllBytes(path);
        var content = System.Text.Encoding.UTF8.GetString(reportBytes).TrimStart('﻿').Replace("\r\n", "\n");
        var title = ReportLibrary.GetTitle(paths, source);
        return new KnowledgeSourceContent(
            source,
            title,
            content,
            ReportHistoryStore.Sha256(reportBytes),
            "text/markdown",
            DocumentFamilies.Markdown,
            Relative(paths, path),
            MarkdownSections.Parse(content, title));
    }

    /// <summary>SHA-256 actual de la fuente (con <see cref="FileShaCache"/>); null si no existe.</summary>
    public static string? CurrentSha(KnowledgePaths paths, SourceRef source) => source.Kind == SourceKind.Document
        ? FileShaCache.Get(new DocumentStore(paths).ContentPath(source.Id))
        : FileShaCache.Get(ReportLibrary.GetFilePath(paths, source));

    private static string Relative(KnowledgePaths paths, string fullPath) =>
        Path.GetRelativePath(paths.RepositoryPath, fullPath).Replace('\\', '/');
}
