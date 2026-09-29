using CodebaseAnalysis.Core.Models;

namespace CodebaseAnalysis.Core.Support;

/// <summary>
/// Un informe del catálogo que existe en disco. <c>ReportType</c> es null para el resumen ejecutivo;
/// <c>Health</c> y <c>CriticalFindings</c> salen de la línea <c>analysis-meta</c>.
/// </summary>
public sealed record ReportSummary(
    string FileName,
    ReportType? ReportType,
    string Title,
    string Health,
    int? CriticalFindings,
    long SizeBytes,
    DateTimeOffset LastModified);

/// <summary>
/// Acceso de solo lectura a la carpeta de informes, limitado a los nombres del catálogo (los 9 y el
/// <c>00</c>): la carpeta es configurable y puede contener Markdown ajeno al pipeline.
/// </summary>
public static class ReportFiles
{
    /// <summary>Nombres válidos, en el orden de presentación: el <c>00</c> primero y después 01..09.</summary>
    public static IReadOnlyList<string> CatalogFileNames { get; } =
        [ReportCatalog.ExecutiveReportFileName, .. ReportCatalog.All.Select(d => d.FileName).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Nombre canónico del catálogo que corresponde a <paramref name="fileName"/> (sin distinguir
    /// mayúsculas); false para cualquier otro nombre, incluidas rutas.
    /// </summary>
    public static bool TryGetCatalogName(string? fileName, out string canonicalName)
    {
        canonicalName = CatalogFileNames.FirstOrDefault(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase))!;
        return canonicalName != null;
    }

    /// <summary>Ruta del informe si el nombre es del catálogo y el fichero existe.</summary>
    public static bool TryResolve(string reportsPath, string? fileName, out string fullPath)
    {
        fullPath = string.Empty;
        if (!TryGetCatalogName(fileName, out var canonical))
            return false;

        var candidate = Path.Combine(reportsPath, canonical);
        if (!File.Exists(candidate))
            return false;

        fullPath = candidate;
        return true;
    }

    /// <summary>Los informes del catálogo presentes en <paramref name="reportsPath"/>.</summary>
    public static IReadOnlyList<ReportSummary> List(string reportsPath)
    {
        var result = new List<ReportSummary>();
        if (!Directory.Exists(reportsPath))
            return result;

        foreach (var name in CatalogFileNames)
        {
            var path = Path.Combine(reportsPath, name);
            if (!File.Exists(path))
                continue;

            var info = new FileInfo(path);
            string content;
            try
            {
                content = File.ReadAllText(path);
            }
            catch (IOException)
            {
                // Un informe que el pipeline está escribiendo en este momento: se lista sin semáforo.
                content = string.Empty;
            }

            var meta = new SingleReportResult { MarkdownContent = content };
            meta.PopulateDerivedMetadata();

            ReportType? type = ReportCatalog.TryGetByFileName(name, out var descriptor) ? descriptor.Type : null;
            result.Add(new ReportSummary(
                name,
                type,
                type is null ? ReportCatalog.ExecutiveReportTitle : descriptor.Title,
                meta.QualityHealth,
                meta.CriticalFindings,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
        }

        return result;
    }
}
