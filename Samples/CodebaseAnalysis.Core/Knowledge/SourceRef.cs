using CodebaseAnalysis.Core.Support;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Knowledge;

/// <summary>Tipo de una fuente de conocimiento (plan v2 §5.1, §8.1.1).</summary>
public enum SourceKind
{
    /// <summary>Documento subido: <c>&lt;repo&gt;/.codebase/documents/&lt;id&gt;/</c>.</summary>
    Document,

    /// <summary>Informe del catálogo (el <c>00</c> y los nueve de los auditores).</summary>
    Report,

    /// <summary>Informe propio: <c>&lt;OutputReportsPath&gt;/custom/&lt;slug&gt;.md</c>.</summary>
    Custom
}

/// <summary>
/// Una fuente que se puede añadir al grafo o a los embeddings, identificada como en la API:
/// <c>{ "kind": "document" | "report" | "custom", "id": "…" }</c>. <see cref="Id"/> es el id del documento, el nombre
/// canónico del informe del catálogo o el nombre del fichero del informe propio (sin <c>custom/</c>). Solo se
/// construye validada, con <see cref="TryCreate"/> o <see cref="TryParseKey"/>, así que su id nunca es una ruta.
/// </summary>
public sealed partial record SourceRef
{
    public const string DocumentKindName = "document";
    public const string ReportKindName = "report";
    public const string CustomKindName = "custom";

    private SourceRef(SourceKind kind, string id)
    {
        Kind = kind;
        Id = id;
    }

    public SourceKind Kind { get; }

    public string Id { get; }

    /// <summary>Nombre del tipo en la API: <c>document</c>, <c>report</c> o <c>custom</c>.</summary>
    public string KindName => Kind switch
    {
        SourceKind.Document => DocumentKindName,
        SourceKind.Report => ReportKindName,
        _ => CustomKindName
    };

    /// <summary>Clave de la fuente en el registro del grafo y en los embeddings: <c>doc:</c>, <c>report:</c> o <c>custom:</c> más el id.</summary>
    public string Key => Kind switch
    {
        SourceKind.Document => "doc:" + Id,
        SourceKind.Report => "report:" + Id,
        _ => "custom:" + Id
    };

    /// <summary>Id de documento: la forma del chat (<c>yyyyMMdd-HHmmss-xxxx</c>) y, en general, <c>^[a-z0-9-]{1,64}$</c>.</summary>
    [GeneratedRegex("^[a-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    public static partial Regex DocumentIdPattern();

    /// <summary>Nombre de fichero de un informe propio: <c>^[a-z0-9][a-z0-9-]{0,62}\.md$</c>.</summary>
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,62}\.md$", RegexOptions.CultureInvariant)]
    public static partial Regex CustomReportFileNamePattern();

    public static SourceRef Document(string id) =>
        TryCreate(DocumentKindName, id, out var source, out var error) ? source : throw new ArgumentException(error, nameof(id));

    public static SourceRef Report(string fileName) =>
        TryCreate(ReportKindName, fileName, out var source, out var error) ? source : throw new ArgumentException(error, nameof(fileName));

    public static SourceRef Custom(string fileName) =>
        TryCreate(CustomKindName, fileName, out var source, out var error) ? source : throw new ArgumentException(error, nameof(fileName));

    /// <summary>
    /// Valida tipo e id tal como llegan de la API. El tipo distingue mayúsculas (<c>document</c>, <c>report</c>,
    /// <c>custom</c>); el nombre de un informe del catálogo se acepta sin distinguirlas y se guarda canónico.
    /// </summary>
    public static bool TryCreate(string? kind, string? id, [NotNullWhen(true)] out SourceRef? source, [NotNullWhen(false)] out string? error)
    {
        source = null;
        switch (kind)
        {
            case DocumentKindName when id != null && DocumentIdPattern().IsMatch(id):
                source = new SourceRef(SourceKind.Document, id);
                break;
            case ReportKindName when ReportFiles.TryGetCatalogName(id, out var canonical):
                source = new SourceRef(SourceKind.Report, canonical);
                break;
            case CustomKindName when id != null && CustomReportFileNamePattern().IsMatch(id):
                source = new SourceRef(SourceKind.Custom, id);
                break;
            case DocumentKindName or ReportKindName or CustomKindName:
                error = $"El id '{id}' no es válido para una fuente de tipo '{kind}'.";
                return false;
            default:
                error = $"El tipo de fuente '{kind}' no existe: tiene que ser document, report o custom.";
                return false;
        }

        error = null;
        return true;
    }

    /// <summary>Lee una clave de <see cref="Key"/> (<c>doc:&lt;id&gt;</c>, <c>report:&lt;fichero&gt;</c>, <c>custom:&lt;fichero&gt;</c>).</summary>
    public static bool TryParseKey(string? key, [NotNullWhen(true)] out SourceRef? source)
    {
        source = null;
        var colon = key?.IndexOf(':') ?? -1;
        if (colon <= 0)
            return false;

        var kind = key![..colon] switch
        {
            "doc" => DocumentKindName,
            "report" => ReportKindName,
            "custom" => CustomKindName,
            _ => null
        };
        return kind != null && TryCreate(kind, key[(colon + 1)..], out source, out _);
    }

    public override string ToString() => Key;
}
