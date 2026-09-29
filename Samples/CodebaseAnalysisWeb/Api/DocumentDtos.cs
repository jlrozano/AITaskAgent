using CodebaseAnalysis.Core.Knowledge;

namespace CodebaseAnalysisWeb.Api;

/// <summary><c>DocumentExtractionDto.status</c> (plan v2 §5.2).</summary>
public static class DocumentExtractionStatuses
{
    /// <summary>Hay texto en <c>content.md</c>.</summary>
    public const string Ok = "ok";

    /// <summary>La extracción no encontró texto (p. ej. un PDF escaneado).</summary>
    public const string Empty = "empty";

    /// <summary>La extracción falló; el motivo va en <c>warnings[0]</c>. El original se puede ver igual.</summary>
    public const string Failed = "failed";

    /// <summary>Imagen sin describir todavía: <c>content.md</c> vacío hasta «Describir».</summary>
    public const string PendingDescription = "pending_description";
}

/// <summary>
/// Resultado de la extracción. <c>pageCount</c>: páginas del PDF, diapositivas del PPTX u hojas del XLSX; null en el
/// resto. <c>warnings</c>: textos para el usuario, en español (truncado, «probablemente escaneado; no hay OCR», JSON
/// inválido aceptado como texto…); nunca null.
/// </summary>
public sealed record DocumentExtractionDto(
    string Status,
    int CharCount,
    int SectionCount,
    int? PageCount,
    bool Truncated,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Documento aportado en los listados (plan v2 §5.2). <c>id</c>: <c>yyyyMMdd-HHmmss-xxxx</c>. <c>title</c>: el nombre
/// original sin extensión hasta que se renombre. <c>extension</c>: en minúsculas y con punto. <c>family</c> y
/// <c>viewer</c>: valores de <c>DocumentFamilies</c> y <c>DocumentViewers</c> (Core). <c>mediaType</c>: el tipo
/// detectado por contenido, el mismo que lleva <c>/raw</c> en las familias que se sirven inline (pdf, image, html,
/// svg). Su fuente de conocimiento es <c>{ kind: "document", id }</c>.
/// </summary>
public record DocumentSummaryDto
{
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
    public required DocumentExtractionDto Extraction { get; init; }
    public required KnowledgeState Knowledge { get; init; }
}

/// <summary>Sección de <c>content.md</c>: encabezados de nivel 1 a 3, con su rango de líneas (desde 1, ambos incluidos).</summary>
public sealed record DocumentSectionDto(int Ordinal, string Heading, int Level, int StartLine, int EndLine);

/// <summary>Descripción generada de una imagen (§7.4).</summary>
public sealed record DocumentDescriptionDto(string Model, DateTimeOffset GeneratedAt);

/// <summary><c>GET /api/documents/{id}</c>: el resumen más las secciones y, si es una imagen descrita, la descripción (si no, null).</summary>
public sealed record DocumentDetailDto : DocumentSummaryDto
{
    public required IReadOnlyList<DocumentSectionDto> Sections { get; init; }
    public DocumentDescriptionDto? Description { get; init; }
}

/// <summary>
/// <c>PATCH /api/documents/{id}</c>. Un campo ausente o null no cambia. <c>title</c> se recorta; vacío o de más de
/// 200 caracteres es 400 <c>invalid_request</c>.
/// </summary>
public sealed record PatchDocumentRequest(string? Title = null, bool? UseInAnalysis = null);
