using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Support;

namespace CodebaseAnalysisWeb.Api;

/// <summary><c>ReportSummaryDto.kind</c> (plan v2 §5.2).</summary>
public static class ReportKinds
{
    /// <summary>El <c>00</c> o uno de los nueve de los auditores. En una <see cref="SourceRef"/>, tipo <c>report</c>.</summary>
    public const string Catalog = "catalog";

    /// <summary>Informe propio de <c>reports/custom/</c>. En una <see cref="SourceRef"/>, tipo <c>custom</c>.</summary>
    public const string Custom = "custom";
}

/// <summary><c>ReportSummaryDto.origin</c>: quién creó el informe. Los del catálogo son siempre <c>pipeline</c>.</summary>
public static class ReportOrigins
{
    public const string Pipeline = "pipeline";
    public const string Chat = "chat";
}

/// <summary>
/// Informe en los listados (plan §4.1 y plan v2 §5.2). Los campos de v1 no cambian. Los de V2 salen siempre:
/// <list type="bullet">
/// <item><c>kind</c>: <c>catalog</c> | <c>custom</c>.</item>
/// <item><c>fileName</c>: nombre del fichero sin carpeta (<c>05_errores_y_bugs.md</c>, <c>estudio-de-colas.md</c>).</item>
/// <item><c>id</c>: el de la ruta del cliente, <c>fileName</c> en el catálogo y <c>custom/&lt;fileName&gt;</c> en los propios.</item>
/// <item><c>reportType</c>: null en el <c>00</c> y en los propios. <c>health</c> y <c>criticalFindings</c>, de la línea
/// <c>analysis-meta</c> si la hay (sin ella, <c>Unknown</c> y null).</item>
/// <item><c>origin</c>: <c>pipeline</c> | <c>chat</c>. <c>modifiedByChat</c>: el contenido actual es una versión de
/// origen chat (§6.4). <c>versionCount</c>: versiones del historial, 0 si no tiene.</item>
/// <item><c>knowledge</c>: estado en grafo y embeddings; con su fuente <c>{ kind: report|custom, id: fileName }</c>.</item>
/// </list>
/// </summary>
public sealed record ReportSummaryDto(
    string FileName,
    string? ReportType,
    string Title,
    string Health,
    int? CriticalFindings,
    long SizeBytes,
    DateTimeOffset LastModified,
    string Kind,
    string Id,
    string Origin,
    bool ModifiedByChat,
    int VersionCount,
    KnowledgeState Knowledge)
{
    /// <summary>
    /// Un informe del catálogo sin historial ni conocimiento: lo que hay mientras V2-B y V2-D no existen. B-back y
    /// D-back sustituyen <c>modifiedByChat</c>, <c>versionCount</c> y <c>knowledge</c> por los reales.
    /// </summary>
    public static ReportSummaryDto FromCatalog(ReportSummary report) => new(
        report.FileName,
        report.ReportType?.ToString(),
        report.Title,
        report.Health,
        report.CriticalFindings,
        report.SizeBytes,
        report.LastModified,
        ReportKinds.Catalog,
        report.FileName,
        ReportOrigins.Pipeline,
        ModifiedByChat: false,
        VersionCount: 0,
        KnowledgeState.Absent);
}

/// <summary><c>ReportVersionDto.action</c> (plan v2 §5.2, §6.4).</summary>
public static class ReportVersionActions
{
    /// <summary>Contenido que había antes de la primera escritura del chat o que regeneró el pipeline (origen <c>pipeline</c>).</summary>
    public const string Baseline = "baseline";

    /// <summary>Informe propio creado por <c>create_report</c>.</summary>
    public const string Created = "created";

    /// <summary>Escritura de <c>update_report</c>.</summary>
    public const string Updated = "updated";

    /// <summary>Restauración de una versión anterior (también es lo que hace «Deshacer»).</summary>
    public const string Restored = "restored";

    /// <summary>Último contenido de un informe propio borrado desde la UI.</summary>
    public const string Deleted = "deleted";
}

/// <summary><c>ReportVersionOriginDto.type</c>.</summary>
public static class ReportVersionOriginTypes
{
    public const string Pipeline = "pipeline";
    public const string Chat = "chat";
    public const string Ui = "ui";
}

/// <summary>Quién escribió una versión. <c>sessionId</c> y <c>turnId</c> solo con <c>type = chat</c>; si no, null.</summary>
public sealed record ReportVersionOriginDto(string Type, string? SessionId, string? TurnId);

/// <summary>
/// Una versión del historial (plan v2 §5.2). <c>versionId</c> es <c>v0001</c>, <c>v0002</c>… (<c>^v\d{4,}$</c>), en orden
/// de creación; el listado va de la más reciente a la más antigua. <c>isCurrent</c>: su SHA-256 coincide con el del
/// fichero actual (a lo sumo una lo es; ninguna si el pipeline regeneró el informe después). <c>note</c> puede ser null.
/// </summary>
public sealed record ReportVersionDto(
    string VersionId,
    DateTimeOffset CreatedAt,
    string Action,
    ReportVersionOriginDto Origin,
    string? Note,
    long SizeBytes,
    string Sha256,
    bool IsCurrent);
