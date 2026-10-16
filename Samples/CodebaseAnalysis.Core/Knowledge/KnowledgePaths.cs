namespace CodebaseAnalysis.Core.Knowledge;

/// <summary>
/// Rutas de V2 en el repositorio analizado (plan v2 §4.1). <c>.codebase</c> es fijo y no depende de
/// <c>DatabasePath</c>: la ingesta de Office y la de Markdown lo excluyen por nombre de segmento, y así los
/// documentos subidos no se analizan como parte del repositorio aunque la BD se configure en otro sitio.
/// Los informes propios cuelgan de <c>OutputReportsPath</c>, que el indexador y la ingesta Markdown ya excluyen.
/// Toda ruta que dependa de un nombre llega por un <see cref="SourceRef"/> validado.
/// </summary>
public sealed record KnowledgePaths
{
    public const string CodebaseFolderName = ".codebase";
    public const string CustomReportsFolderName = "custom";
    public const string DocumentOriginalFileName = "original.bin";
    public const string DocumentContentFileName = "content.md";
    public const string DocumentMetadataFileName = "document.json";
    public const string ReportHistoryFileName = "history.json";

    public KnowledgePaths(string repositoryPath, string reportsPath)
    {
        RepositoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        ReportsPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(reportsPath));
    }

    public string RepositoryPath { get; }

    /// <summary><c>OutputReportsPath</c> resuelto.</summary>
    public string ReportsPath { get; }

    /// <summary><c>&lt;repo&gt;/.codebase</c>.</summary>
    public string CodebaseRoot => Path.Combine(RepositoryPath, CodebaseFolderName);

    /// <summary><c>&lt;OutputReportsPath&gt;/custom</c>: informes propios (V2-B).</summary>
    public string CustomReportsPath => Path.Combine(ReportsPath, CustomReportsFolderName);

    /// <summary><c>&lt;repo&gt;/.codebase/report-history</c>, con <c>catalog/</c> y <c>custom/</c> debajo (V2-B).</summary>
    public string ReportHistoryPath => Path.Combine(CodebaseRoot, "report-history");

    /// <summary><c>&lt;repo&gt;/.codebase/documents</c>: una carpeta por documento (V2-C).</summary>
    public string DocumentsPath => Path.Combine(CodebaseRoot, "documents");

    /// <summary>Rutas de lectura adicionales del chat (<c>&lt;repo&gt;/.codebase/chat-read-roots.json</c>). Fuera de <c>chat/</c>, donde cada JSON es una sesión.</summary>
    public string ChatReadRootsFile => Path.Combine(CodebaseRoot, "chat-read-roots.json");

    /// <summary><c>documents/.incoming</c>: temporales de subida, antes de validar y extraer (§7.1).</summary>
    public string IncomingDocumentsPath => Path.Combine(DocumentsPath, ".incoming");

    /// <summary><c>&lt;repo&gt;/.codebase/knowledge</c> (V2-D).</summary>
    public string KnowledgePath => Path.Combine(CodebaseRoot, "knowledge");

    /// <summary>Registro de fuentes marcadas para el grafo (§8.1.3).</summary>
    public string GraphSourcesFile => Path.Combine(KnowledgePath, "graph-sources.json");

    /// <summary>Almacén de vectores (§8.2.3).</summary>
    public string EmbeddingsDatabasePath => Path.Combine(KnowledgePath, "embeddings.db");

    /// <summary>Carpeta de un documento. Solo acepta fuentes de tipo documento.</summary>
    public string DocumentFolder(SourceRef document)
    {
        if (document.Kind != SourceKind.Document)
            throw new ArgumentException($"'{document}' no es un documento.", nameof(document));
        return Inside(DocumentsPath, document.Id);
    }

    /// <summary>Fichero de un informe propio (<c>custom/&lt;fichero&gt;.md</c>).</summary>
    public string CustomReportFile(SourceRef report)
    {
        if (report.Kind != SourceKind.Custom)
            throw new ArgumentException($"'{report}' no es un informe propio.", nameof(report));
        return Inside(CustomReportsPath, report.Id);
    }

    /// <summary>Carpeta del historial de un informe: <c>report-history/catalog/&lt;fichero&gt;/</c> o <c>report-history/custom/&lt;fichero&gt;/</c>.</summary>
    public string ReportHistoryFolder(SourceRef report) => report.Kind switch
    {
        SourceKind.Report => Inside(Path.Combine(ReportHistoryPath, "catalog"), report.Id),
        SourceKind.Custom => Inside(Path.Combine(ReportHistoryPath, CustomReportsFolderName), report.Id),
        _ => throw new ArgumentException($"'{report}' no es un informe.", nameof(report))
    };

    /// <summary>
    /// Documentos presentes: carpetas de <see cref="DocumentsPath"/> con id válido y <c>document.json</c>. Es la
    /// semántica de <c>WorkspaceDto.documentCount</c>; <c>DocumentStore</c> (V2-C) tiene que dar el mismo número.
    /// </summary>
    public int CountDocuments()
    {
        if (!Directory.Exists(DocumentsPath))
            return 0;

        return Directory.EnumerateDirectories(DocumentsPath)
            .Count(d => SourceRef.DocumentIdPattern().IsMatch(Path.GetFileName(d))
                        && File.Exists(Path.Combine(d, DocumentMetadataFileName)));
    }

    /// <summary>
    /// Informes propios presentes: ficheros de <see cref="CustomReportsPath"/> cuyo nombre cumple
    /// <see cref="SourceRef.CustomReportFileNamePattern"/>. Semántica de <c>WorkspaceDto.customReportCount</c>; el
    /// listado de <c>ReportLibrary</c> (V2-B) tiene que dar el mismo número.
    /// </summary>
    public int CountCustomReports()
    {
        if (!Directory.Exists(CustomReportsPath))
            return 0;

        return Directory.EnumerateFiles(CustomReportsPath, "*.md")
            .Count(f => SourceRef.CustomReportFileNamePattern().IsMatch(Path.GetFileName(f)));
    }

    /// <summary>
    /// Une y comprueba que el resultado queda dentro de <paramref name="root"/>, con la técnica de
    /// <c>ChatSessionStore</c>. Con un <see cref="SourceRef"/> validado no puede fallar; es la segunda barrera.
    /// </summary>
    private static string Inside(string root, string name)
    {
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, name));
        var prefix = Path.EndsInDirectorySeparator(rootFull) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"El nombre '{name}' sale de '{root}'.", nameof(name));
        return full;
    }
}
