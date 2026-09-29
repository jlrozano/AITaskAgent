namespace CodebaseAnalysis.Core.Models;

/// <summary>
/// Un informe del catálogo: su tipo y todo lo que se deriva de él. <c>Alias</c> es el alias principal en
/// <c>--reports</c> (README §5).
/// </summary>
public sealed record ReportDescriptor(
    ReportType Type,
    string Alias,
    string FileName,
    string Title,
    string RoleName,
    string TemplatePrefix);

/// <summary>
/// Los nueve informes con su alias principal, fichero, título, rol y prefijo de plantilla. Fichero,
/// rol y prefijo salen de los <c>switch</c> de <see cref="AnalysisPipelineInput"/>, que siguen siendo
/// la fuente; aquí solo se añaden el alias principal y el título para mostrar.
/// </summary>
public static class ReportCatalog
{
    public static IReadOnlyList<ReportDescriptor> All { get; } = Enum.GetValues<ReportType>()
        .Select(type => new ReportDescriptor(
            type,
            GetAlias(type),
            AnalysisPipelineInput.GetReportFileName(type),
            GetTitle(type),
            AnalysisPipelineInput.GetRoleName(type),
            AnalysisPipelineInput.GetTemplatePrefix(type)))
        .ToList();

    public static string ExecutiveReportFileName => AnalysisPipelineInput.ExecutiveReportFileName;

    public const string ExecutiveReportTitle = "Resumen ejecutivo";

    public static ReportDescriptor Get(ReportType type) => All.First(d => d.Type == type);

    /// <summary>Busca por nombre de fichero (sin distinguir mayúsculas); no incluye el resumen ejecutivo.</summary>
    public static bool TryGetByFileName(string fileName, out ReportDescriptor descriptor)
    {
        descriptor = All.FirstOrDefault(d => string.Equals(d.FileName, fileName, StringComparison.OrdinalIgnoreCase))!;
        return descriptor != null;
    }

    private static string GetAlias(ReportType type) => type switch
    {
        ReportType.AnalisisFuncional => "funcional",
        ReportType.CodeSmells => "smells",
        ReportType.CodigoMuerto => "deadcode",
        ReportType.FlujosEndToEnd => "flujos",
        ReportType.ErroresYBugs => "errores",
        ReportType.ArquitecturaAcoplamiento => "arquitectura",
        ReportType.InventarioApisSeguridad => "apis",
        ReportType.DeudaTecnicaHotspots => "deuda",
        ReportType.InyeccionDependenciasExtensiones => "di",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    private static string GetTitle(ReportType type) => type switch
    {
        ReportType.AnalisisFuncional => "Análisis funcional",
        ReportType.CodeSmells => "Code smells",
        ReportType.CodigoMuerto => "Código muerto",
        ReportType.FlujosEndToEnd => "Flujos end-to-end",
        ReportType.ErroresYBugs => "Errores y bugs",
        ReportType.ArquitecturaAcoplamiento => "Arquitectura y acoplamiento",
        ReportType.InventarioApisSeguridad => "Inventario de APIs y seguridad",
        ReportType.DeudaTecnicaHotspots => "Deuda técnica y hotspots",
        ReportType.InyeccionDependenciasExtensiones => "Inyección de dependencias y extensiones",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };
}
