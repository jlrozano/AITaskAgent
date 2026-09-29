using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;

namespace CodebaseAnalysis.Core.Models;

public class ReportFacetInput : StepResult
{
    public string RepositoryPath { get; set; } = string.Empty;
    public string DatabasePath { get; set; } = string.Empty;
    public string OutputReportsPath { get; set; } = string.Empty;
    public string DetectedLanguages { get; set; } = "No determinado";

    // Report 1: Funcional
    public string MetricsSummary { get; set; } = "[]";
    public string RoutesSummary { get; set; } = "[]";
    public string FlowsSummary { get; set; } = "[]";
    public string DomainEntitiesSummary { get; set; } = "[]";

    // Report 2: Code Smells
    public string HighCyclomaticMethods { get; set; } = "[]";
    public string BloatedClasses { get; set; } = "[]";
    public string CouplingMetrics { get; set; } = "[]";

    // Report 3: Código Muerto
    public string DeadCodeCandidates { get; set; } = "[]";
    public string UnusedDiRegistrations { get; set; } = "[]";
    public string OrphanRoutes { get; set; } = "[]";
    public string ZeroFaninClasses { get; set; } = "[]";

    /// <summary>
    /// Campos Java privados, sin los <c>static final</c>, sin <c>READS</c> entrante, con fichero y línea. Va también al
    /// informe 02.
    /// </summary>
    public string UnreadFields { get; set; } = "[]";

    /// <summary>Acciones Struts (<c>Route</c> con <c>framework = struts</c>) sin <c>HANDLES</c> entrante, con el motivo.</summary>
    public string StrutsActionsWithoutHandler { get; set; } = "[]";

    // Report 4: Flujos End-to-End
    public string FlowsCatalog { get; set; } = "[]";
    public string FlowStepsGraph { get; set; } = "[]";
    public string RouteHandlersMapping { get; set; } = "[]";
    public string DiResolutions { get; set; } = "[]";

    /// <summary>Forwards de Struts sin destino, de la última pasada del plugin en <c>enrichment_runs</c>; <c>[]</c> sin ella.</summary>
    public string UnresolvedForwards { get; set; } = "[]";

    // Report 5: Errores y Bugs
    public string CriticalHotspotsExceptions { get; set; } = "[]";
    public string UnvalidatedRoutes { get; set; } = "[]";
    public string SuspectPatterns { get; set; } = "[]";

    /// <summary>JSP que el plugin Struts/JSP marca como sospechosas, con sus motivos.</summary>
    public string SuspiciousJsps { get; set; } = "[]";

    /// <summary>Credenciales escritas como literal, con el valor enmascarado (HardcodedSecretScanner).</summary>
    public string HardcodedSecrets { get; set; } = "[]";

    // Report 6: Arquitectura y Acoplamiento
    public string ProjectComponents { get; set; } = "[]";
    public string MartinInstabilityMetrics { get; set; } = "[]";
    public string DependencyCycles { get; set; } = "[]";
    public string LayerViolations { get; set; } = "[]";
    public string ArchitecturalCommunities { get; set; } = "[]";

    /// <summary>Jerarquía <c>INHERITS</c>/<c>IMPLEMENTS</c> por supertipo, con aristas resueltas y heurísticas de cbm.</summary>
    public string TypeHierarchy { get; set; } = "[]";

    // Report 7: APIs y Seguridad
    public string RoutesCatalog { get; set; } = "[]";
    public string RoutesSecurityMatrix { get; set; } = "[]";
    public string RoutesContracts { get; set; } = "[]";
    public string OrphanRoutesList { get; set; } = "[]";

    // Report 8: Deuda Técnica y Hotspots
    public string HotspotsRanking { get; set; } = "[]";
    public string DocCoverageMetrics { get; set; } = "[]";
    public string ObsoleteElements { get; set; } = "[]";
    public string ComplexClasses { get; set; } = "[]";

    // Report 9: Inyección de Dependencias
    public string DiRegistrationsCatalog { get; set; } = "[]";
    public string DiLifetimesMapping { get; set; } = "[]";
    public string ExtensionPointsCatalog { get; set; } = "[]";
    public string ConfigAdaptersMapping { get; set; } = "[]";
    public string CreationalCandidates { get; set; } = "[]";

    // Workflow Steps, Prompts & Markdown Documentation
    public string WorkflowStepsCatalog { get; set; } = "[]";
    public string PromptTemplatesCatalog { get; set; } = "[]";
    public string DocumentationAndPrompts { get; set; } = "[]";

    /// <summary>
    /// Índice de la documentación aportada por el usuario con <c>useInAnalysis</c> (plan v2 §7.8), en JSON; <c>[]</c>
    /// sin documentos. Lo incluye cada auditor con <c>_provided_documentation.md</c>.
    /// </summary>
    public string ProvidedDocumentation { get; set; } = "[]";

    // Global Metrics
    public int TotalNodes { get; set; }
    public int TotalEdges { get; set; }
    public int TotalLoc { get; set; }
    public int TotalRoutes { get; set; }
    public int TotalFlows { get; set; }
    public int DeadCodeCount { get; set; }

    public Dictionary<string, object?> RawFacets { get; set; } = [];

    public ReportFacetInput()
        : base(new EmptyStep(nameof(ReportFacetInput)))
    {
    }

    public ReportFacetInput(IStep step)
        : base(step)
    {
    }

    public ReportFacetInput(IStep step, IStepError? error)
        : base(step)
    {
        Error = error;
    }
}
