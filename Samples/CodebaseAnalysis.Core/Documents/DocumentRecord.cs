namespace CodebaseAnalysis.Core.Documents;

/// <summary>Valores de <see cref="DocumentExtractionInfo.Status"/> (plan v2 §5.2), los mismos que <c>DocumentExtractionStatuses</c> de la web.</summary>
public static class ExtractionStatuses
{
    public const string Ok = "ok";
    public const string Empty = "empty";
    public const string Failed = "failed";
    public const string PendingDescription = "pending_description";
}

/// <summary>Resultado de la extracción tal como se guarda en <c>document.json</c> y sale en <c>DocumentExtractionDto</c>.</summary>
public sealed record DocumentExtractionInfo(
    string Status,
    int CharCount,
    int SectionCount,
    int? PageCount,
    bool Truncated,
    IReadOnlyList<string> Warnings);

/// <summary>Una sección de <c>content.md</c>: encabezado de nivel 1 a 3 (o un tramo sin encabezado) con sus líneas, desde 1 e inclusivas.</summary>
public sealed record DocumentSection(int Ordinal, string Heading, int Level, int StartLine, int EndLine);

/// <summary>Descripción generada de una imagen (§7.4).</summary>
public sealed record DocumentDescriptionInfo(string Model, DateTimeOffset GeneratedAt);

/// <summary>
/// <c>&lt;repo&gt;/.codebase/documents/&lt;id&gt;/document.json</c> (plan v2 §7.1): metadatos, secciones, estado de la
/// extracción, <c>useInAnalysis</c> y la descripción de una imagen. Es la fuente de <c>DocumentSummaryDto</c>.
/// </summary>
public sealed record DocumentRecord
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string OriginalFileName { get; init; }
    public required string Extension { get; init; }
    public required string Family { get; init; }
    public required string MediaType { get; init; }
    public required string Viewer { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public required DateTimeOffset UploadedAt { get; init; }
    public required bool UseInAnalysis { get; init; }
    public required DocumentExtractionInfo Extraction { get; init; }
    public IReadOnlyList<DocumentSection> Sections { get; init; } = [];
    public DocumentDescriptionInfo? Description { get; init; }
}
