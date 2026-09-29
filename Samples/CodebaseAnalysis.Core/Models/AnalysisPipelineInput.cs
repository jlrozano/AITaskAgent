using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using System.Text.Json.Serialization;

namespace CodebaseAnalysis.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReportType
{
    AnalisisFuncional,
    CodeSmells,
    CodigoMuerto,
    FlujosEndToEnd,
    ErroresYBugs,
    ArquitecturaAcoplamiento,
    InventarioApisSeguridad,
    DeudaTecnicaHotspots,
    InyeccionDependenciasExtensiones
}

public class AnalysisPipelineInput : StepResult
{
    public const string ExecutiveReportFileName = "00_resumen_ejecutivo.md";

    public string RepositoryPath { get; set; } = string.Empty;
    public string DatabasePath { get; set; } = string.Empty;
    public string OutputReportsPath { get; set; } = string.Empty;
    public bool ForceReindex { get; set; }
    public List<ReportType> RequestedReports { get; set; } = [];

    /// <summary>Tope de iteraciones de tools de un auditor si su rol no tiene uno propio (el framework trae 5).</summary>
    public const int DefaultAuditorMaxToolIterations = 12;

    /// <summary><c>CodebaseAnalysis:Auditors:MaxToolIterations</c>: tope de iteraciones de tools de todos los auditores.</summary>
    public int AuditorMaxToolIterations { get; set; } = DefaultAuditorMaxToolIterations;

    /// <summary><c>CodebaseAnalysis:Auditors:MaxToolIterationsByRole</c>: tope por rol (<c>FlowsAuditor</c>…), que manda sobre el general.</summary>
    public Dictionary<string, int> AuditorMaxToolIterationsByRole { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>CodebaseAnalysis:Reports:MaxParallelAuditors</c>: auditores que se ejecutan a la vez. 0 = sin tope propio (el de
    /// <c>Parallel.ForEachAsync</c>, uno por procesador), que es lo de siempre; la decisión de bajarlo espera a medir.
    /// </summary>
    public int MaxParallelAuditors { get; set; }

    /// <summary>
    /// <c>JavaAnalyzer:DefaultEncoding</c> tal cual está configurada (vacía si no). Con ella y el <c>pom.xml</c> se resuelve la
    /// codificación con la que las tools leen los ficheros que no son UTF-8 válido (<see cref="Enrichment.SourceEncoding.ResolveReadFallback"/>).
    /// </summary>
    public string SourceDefaultEncoding { get; set; } = string.Empty;

    /// <summary>Copias de <c>backups/</c> que se conservan si falta <c>CodebaseAnalysis:Backups:Keep</c>.</summary>
    public const int DefaultBackupsToKeep = 5;

    /// <summary>
    /// <c>CodebaseAnalysis:Backups:Keep</c>: copias de ForceReindex que se conservan en <c>&lt;carpeta de la BD&gt;/backups/</c>,
    /// las más recientes; 0 = sin límite. Cada una lleva la BD entera, que en un repositorio grande pasa del GB.
    /// </summary>
    public int BackupsToKeep { get; set; } = DefaultBackupsToKeep;

    /// <summary>Tope de iteraciones de tools del auditor de ese rol.</summary>
    public int GetAuditorMaxToolIterations(string roleName) =>
        AuditorMaxToolIterationsByRole.TryGetValue(roleName, out var max) ? max : AuditorMaxToolIterations;

    public AnalysisPipelineInput() 
        : base(new EmptyStep(nameof(AnalysisPipelineInput)))
    {
    }

    public AnalysisPipelineInput(IStep step) 
        : base(step)
    {
    }

    public AnalysisPipelineInput(IStep step, IStepError? error) 
        : base(step)
    {
        Error = error;
    }

    public static string GetReportFileName(ReportType reportType) => reportType switch
    {
        ReportType.AnalisisFuncional => "01_analisis_funcional.md",
        ReportType.CodeSmells => "02_code_smells.md",
        ReportType.CodigoMuerto => "03_codigo_muerto.md",
        ReportType.FlujosEndToEnd => "04_flujos_end_to_end.md",
        ReportType.ErroresYBugs => "05_errores_y_bugs.md",
        ReportType.ArquitecturaAcoplamiento => "06_arquitectura_acoplamiento.md",
        ReportType.InventarioApisSeguridad => "07_inventario_apis_seguridad.md",
        ReportType.DeudaTecnicaHotspots => "08_deuda_tecnica_hotspots.md",
        ReportType.InyeccionDependenciasExtensiones => "09_inyeccion_dependencias.md",
        _ => throw new ArgumentOutOfRangeException(nameof(reportType), reportType, null)
    };

    public static string GetTemplatePrefix(ReportType reportType) => reportType switch
    {
        ReportType.AnalisisFuncional => "report_funcional",
        ReportType.CodeSmells => "report_codesmells",
        ReportType.CodigoMuerto => "report_codigomuerto",
        ReportType.FlujosEndToEnd => "report_flujos",
        ReportType.ErroresYBugs => "report_errores",
        ReportType.ArquitecturaAcoplamiento => "report_arquitectura",
        ReportType.InventarioApisSeguridad => "report_apis",
        ReportType.DeudaTecnicaHotspots => "report_deuda",
        ReportType.InyeccionDependenciasExtensiones => "report_di",
        _ => throw new ArgumentOutOfRangeException(nameof(reportType), reportType, null)
    };

    public static string GetRoleName(ReportType reportType) => reportType switch
    {
        ReportType.AnalisisFuncional => "FunctionalAuditor",
        ReportType.CodeSmells => "CodeSmellsAuditor",
        ReportType.CodigoMuerto => "DeadCodeAuditor",
        ReportType.FlujosEndToEnd => "FlowsAuditor",
        ReportType.ErroresYBugs => "SecurityBugAuditor",
        ReportType.ArquitecturaAcoplamiento => "ArchitectureAuditor",
        ReportType.InventarioApisSeguridad => "ApiSecurityAuditor",
        ReportType.DeudaTecnicaHotspots => "TechnicalDebtAuditor",
        ReportType.InyeccionDependenciasExtensiones => "DiAuditor",
        _ => throw new ArgumentOutOfRangeException(nameof(reportType), reportType, null)
    };

    public static ReportType ParseReportType(string name)
    {
        var normalized = name.Replace("-", "").Replace("_", "").Trim();
        if (Enum.TryParse<ReportType>(normalized, true, out var result))
        {
            return result;
        }

        return normalized.ToLowerInvariant() switch
        {
            "analisisfuncional" or "funcional" => ReportType.AnalisisFuncional,
            "codesmells" or "smells" => ReportType.CodeSmells,
            "codigomuerto" or "deadcode" => ReportType.CodigoMuerto,
            "flujosendtoend" or "flujos" or "flows" => ReportType.FlujosEndToEnd,
            "erroresybugs" or "errores" or "bugs" => ReportType.ErroresYBugs,
            "arquitecturaacoplamiento" or "arquitectura" => ReportType.ArquitecturaAcoplamiento,
            "inventarioapisseguridad" or "apis" or "seguridad" => ReportType.InventarioApisSeguridad,
            "deudatecnicahotspots" or "deuda" or "hotspots" => ReportType.DeudaTecnicaHotspots,
            "inyecciondependenciasextensiones" or "di" => ReportType.InyeccionDependenciasExtensiones,
            _ => throw new ArgumentException($"Tipo de informe desconocido: {name}", nameof(name))
        };
    }
}
