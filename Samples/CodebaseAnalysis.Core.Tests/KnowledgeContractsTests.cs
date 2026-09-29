using CodebaseAnalysis.Core.Documents;
using CodebaseAnalysis.Core.Knowledge;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>V2-0 (plan v2 §4.1, §5.1, §7.2): identificación de fuentes, rutas y tabla de tipos.</summary>
public sealed class KnowledgeContractsTests
{
    [Theory]
    [InlineData("document", "20260927-111203-a7qk", "doc:20260927-111203-a7qk")]
    [InlineData("report", "05_ERRORES_Y_BUGS.md", "report:05_errores_y_bugs.md")]
    [InlineData("report", "00_resumen_ejecutivo.md", "report:00_resumen_ejecutivo.md")]
    [InlineData("custom", "estudio-de-colas.md", "custom:estudio-de-colas.md")]
    public void Valid_sources_have_a_canonical_id_and_a_key_that_round_trips(string kind, string id, string key)
    {
        Assert.True(SourceRef.TryCreate(kind, id, out var source, out var error));
        Assert.Null(error);
        Assert.Equal(kind, source.KindName);
        Assert.Equal(key, source.Key);
        Assert.True(SourceRef.TryParseKey(key, out var parsed));
        Assert.Equal(source, parsed);
    }

    [Theory]
    [InlineData("document", "../x")]
    [InlineData("document", "ABC")]
    [InlineData("document", "")]
    [InlineData("document", null)]
    [InlineData("report", "NOTAS.md")]
    [InlineData("report", "..\\05_errores_y_bugs.md")]
    [InlineData("custom", "custom/estudio.md")]
    [InlineData("custom", "Estudio.md")]
    [InlineData("custom", "-estudio.md")]
    [InlineData("custom", "estudio.txt")]
    [InlineData("Document", "20260927-111203-a7qk")]
    [InlineData("doc", "20260927-111203-a7qk")]
    [InlineData(null, "x")]
    public void Invalid_sources_are_rejected_with_a_message(string? kind, string? id)
    {
        Assert.False(SourceRef.TryCreate(kind, id, out var source, out var error));
        Assert.Null(source);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("doc:")]
    [InlineData(":x")]
    [InlineData("document:20260927-111203-a7qk")]
    [InlineData("custom:../x.md")]
    [InlineData(null)]
    public void Invalid_keys_are_rejected(string? key)
    {
        Assert.False(SourceRef.TryParseKey(key, out _));
    }

    [Fact]
    public void Paths_hang_from_a_fixed_codebase_folder_and_the_reports_folder()
    {
        var repo = Path.Combine(Path.GetTempPath(), "repo-v2");
        var reports = Path.Combine(repo, "salida", "informes");
        var paths = new KnowledgePaths(repo + Path.DirectorySeparatorChar, reports);

        Assert.Equal(Path.Combine(repo, ".codebase"), paths.CodebaseRoot);
        Assert.Equal(Path.Combine(reports, "custom"), paths.CustomReportsPath);
        Assert.Equal(Path.Combine(repo, ".codebase", "documents", "20260927-111203-a7qk"),
            paths.DocumentFolder(SourceRef.Document("20260927-111203-a7qk")));
        Assert.Equal(Path.Combine(repo, ".codebase", "documents", ".incoming"), paths.IncomingDocumentsPath);
        Assert.Equal(Path.Combine(reports, "custom", "estudio.md"), paths.CustomReportFile(SourceRef.Custom("estudio.md")));
        Assert.Equal(Path.Combine(repo, ".codebase", "report-history", "catalog", "05_errores_y_bugs.md"),
            paths.ReportHistoryFolder(SourceRef.Report("05_errores_y_bugs.md")));
        Assert.Equal(Path.Combine(repo, ".codebase", "report-history", "custom", "estudio.md"),
            paths.ReportHistoryFolder(SourceRef.Custom("estudio.md")));
        Assert.Equal(Path.Combine(repo, ".codebase", "knowledge", "graph-sources.json"), paths.GraphSourcesFile);
        Assert.Equal(Path.Combine(repo, ".codebase", "knowledge", "embeddings.db"), paths.EmbeddingsDatabasePath);
        Assert.Throws<ArgumentException>(() => paths.DocumentFolder(SourceRef.Custom("estudio.md")));
        Assert.Throws<ArgumentException>(() => paths.ReportHistoryFolder(SourceRef.Document("20260927-111203-a7qk")));
    }

    [Fact]
    public void Every_extension_belongs_to_one_family_and_viewers_are_the_five_of_the_contract()
    {
        var extensions = DocumentTypeRegistry.All.SelectMany(t => t.Extensions).ToList();

        Assert.Equal(extensions.Count, extensions.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(extensions, e => Assert.Matches("^\\.[a-z0-9]+$", e));
        Assert.Equal(12, DocumentTypeRegistry.All.Count);
        Assert.Subset(new HashSet<string> { "markdown", "text", "pdf", "html", "image" },
            DocumentTypeRegistry.All.Select(t => t.Viewer).ToHashSet());
        Assert.Equal(25L * 1024 * 1024, DocumentTypeRegistry.MaxFileBytes);
        Assert.True(DocumentTypeRegistry.TryGetByExtension(".PDF", out var pdf));
        Assert.Equal(DocumentFamilies.Pdf, pdf.Family);
        Assert.False(DocumentTypeRegistry.TryGetByExtension(".doc", out _));
        Assert.False(DocumentTypeRegistry.TryGetByExtension(".exe", out _));
    }
}
