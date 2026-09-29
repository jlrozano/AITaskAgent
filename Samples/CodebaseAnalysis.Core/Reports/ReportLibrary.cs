using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Support;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace CodebaseAnalysis.Core.Reports;

/// <summary>
/// Un informe propio de <c>reports/custom/</c>. <see cref="Title"/> es su primer <c># </c> o, si no tiene, el nombre del
/// fichero sin extensión; <see cref="Health"/> y <see cref="CriticalFindings"/> salen de su <c>analysis-meta</c> si la hay.
/// </summary>
public sealed record CustomReportSummary(
    string FileName,
    string Title,
    string Health,
    int? CriticalFindings,
    long SizeBytes,
    DateTimeOffset LastModified);

/// <summary>
/// Único sitio que valida nombres de informes (plan v2 §6.1): el catálogo por <see cref="ReportFiles"/> y los propios
/// por <see cref="SourceRef.CustomReportFileNamePattern"/>. Ninguna ruta sale de un nombre sin pasar por aquí o por
/// <see cref="KnowledgePaths"/>.
/// </summary>
public static class ReportLibrary
{
    /// <summary>Prefijo de los ids de informes propios en la API y en las tools (<c>custom/estudio.md</c>).</summary>
    public const string CustomIdPrefix = KnowledgePaths.CustomReportsFolderName + "/";

    /// <summary>Informes propios por repositorio (<c>report_limit_reached</c>).</summary>
    public const int MaxCustomReports = 100;

    /// <summary>Caracteres por informe (<c>report_too_large</c>).</summary>
    public const int MaxReportChars = 300_000;

    /// <summary>Longitud máxima del slug (sin <c>.md</c> ni el sufijo de desambiguación).</summary>
    public const int MaxSlugLength = 60;

    /// <summary>
    /// Lee un id de informe tal como lo usan el cliente y las tools: un nombre del catálogo (sin distinguir
    /// mayúsculas) o <c>custom/&lt;fichero&gt;.md</c>. False con cualquier otra cosa, rutas incluidas.
    /// </summary>
    public static bool TryParseId(string? id, [NotNullWhen(true)] out SourceRef? source)
    {
        source = null;
        if (string.IsNullOrWhiteSpace(id))
            return false;

        id = id.Trim();
        if (id.StartsWith(CustomIdPrefix, StringComparison.OrdinalIgnoreCase))
            return SourceRef.TryCreate(SourceRef.CustomKindName, id[CustomIdPrefix.Length..], out source, out _);

        return SourceRef.TryCreate(SourceRef.ReportKindName, id, out source, out _);
    }

    /// <summary>Id de la API de un informe: el nombre en el catálogo, <c>custom/&lt;fichero&gt;</c> en los propios.</summary>
    public static string ToId(SourceRef report) => report.Kind switch
    {
        SourceKind.Report => report.Id,
        SourceKind.Custom => CustomIdPrefix + report.Id,
        _ => throw new ArgumentException($"'{report}' no es un informe.", nameof(report))
    };

    /// <summary>Ruta del fichero de un informe (exista o no).</summary>
    public static string GetFilePath(KnowledgePaths paths, SourceRef report) => report.Kind switch
    {
        SourceKind.Report => Path.Combine(paths.ReportsPath, report.Id),
        SourceKind.Custom => paths.CustomReportFile(report),
        _ => throw new ArgumentException($"'{report}' no es un informe.", nameof(report))
    };

    public static bool Exists(KnowledgePaths paths, SourceRef report) =>
        report.Kind is SourceKind.Report or SourceKind.Custom && File.Exists(GetFilePath(paths, report));

    /// <summary>
    /// Informes propios presentes, por nombre. Da el mismo número que <see cref="KnowledgePaths.CountCustomReports"/>.
    /// </summary>
    public static IReadOnlyList<CustomReportSummary> ListCustom(KnowledgePaths paths)
    {
        var result = new List<CustomReportSummary>();
        if (!Directory.Exists(paths.CustomReportsPath))
            return result;

        foreach (var file in Directory.EnumerateFiles(paths.CustomReportsPath, "*.md").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (!SourceRef.CustomReportFileNamePattern().IsMatch(name))
                continue;

            var summary = TryGetCustom(paths, SourceRef.Custom(name));
            if (summary != null)
                result.Add(summary);
        }

        return result;
    }

    /// <summary>El resumen de un informe propio, o null si no existe.</summary>
    public static CustomReportSummary? TryGetCustom(KnowledgePaths paths, SourceRef report)
    {
        var path = paths.CustomReportFile(report);
        if (!File.Exists(path))
            return null;

        var info = new FileInfo(path);
        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (IOException)
        {
            content = string.Empty;
        }

        var meta = new SingleReportResult { MarkdownContent = content };
        meta.PopulateDerivedMetadata();
        return new CustomReportSummary(
            report.Id,
            TitleOf(content) ?? Path.GetFileNameWithoutExtension(report.Id),
            meta.QualityHealth,
            meta.CriticalFindings,
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }

    /// <summary>Título de un informe: el de su catálogo o el primer <c># </c> de un propio.</summary>
    public static string GetTitle(KnowledgePaths paths, SourceRef report)
    {
        if (report.Kind == SourceKind.Report)
        {
            return ReportCatalog.TryGetByFileName(report.Id, out var descriptor)
                ? descriptor.Title
                : ReportCatalog.ExecutiveReportTitle;
        }

        return TryGetCustom(paths, report)?.Title ?? Path.GetFileNameWithoutExtension(report.Id);
    }

    /// <summary>Texto del primer encabezado de nivel 1 fuera de bloques de código; null si no hay.</summary>
    public static string? TitleOf(string markdown)
    {
        var inFence = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimStart();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence && line.StartsWith("# ", StringComparison.Ordinal))
            {
                var title = line[2..].Trim().TrimEnd('#').Trim();
                if (title.Length > 0)
                    return title;
            }
        }

        return null;
    }

    /// <summary>
    /// Slug de un título: minúsculas, sin tildes, <c>[a-z0-9-]</c>, guiones sin repetir y como mucho
    /// <see cref="MaxSlugLength"/> caracteres. <c>informe</c> si no queda nada.
    /// </summary>
    public static string Slugify(string title)
    {
        var normalized = title.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            var lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
                sb.Append(lower);
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length > MaxSlugLength)
            slug = slug[..MaxSlugLength].TrimEnd('-');
        return slug.Length == 0 ? "informe" : slug;
    }

    /// <summary>
    /// Nombre libre para un informe propio con ese título: <c>slug.md</c>, <c>slug-2.md</c>, <c>slug-3.md</c>… Libre quiere
    /// decir sin fichero y sin carpeta en <c>report-history/custom/</c>: el historial de un propio borrado sobrevive
    /// (§6.4) y reutilizar su nombre mezclaría las versiones de dos informes distintos.
    /// </summary>
    public static string NewCustomFileName(KnowledgePaths paths, string title)
    {
        var slug = Slugify(title);
        for (var n = 1; ; n++)
        {
            var name = n == 1 ? $"{slug}.md" : $"{slug}-{n}.md";
            if (!File.Exists(Path.Combine(paths.CustomReportsPath, name))
                && !Directory.Exists(paths.ReportHistoryFolder(SourceRef.Custom(name))))
                return name;
        }
    }
}
