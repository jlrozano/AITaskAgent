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

    /// <summary>Pares de métodos casi idénticos (<c>SIMILAR_TO</c> del indexador) con sus dos ubicaciones; candidatos a duplicación.</summary>
    public string DuplicateCandidates { get; set; } = "[]";

    /// <summary>Total de pares de <see cref="DuplicateCandidates"/> (los listados son los mayores), umbrales y salvedad, JSON.</summary>
    public string DuplicateSummary { get; set; } = "{}";

    // Report 3: Código Muerto
    /// <summary>
    /// Candidatos de <c>dead_code_candidate</c> sin los duplicados: si una clase es candidata no se listan sus métodos (van
    /// en <c>dead_members</c>), los métodos privados sin uso salen en <see cref="DeadPrivateMethods"/> y los que
    /// sobrescriben un contrato (<c>OVERRIDES</c>) no son candidatos. Por confianza y tamaño; los totales, en
    /// <see cref="DeadCodeSummary"/>.
    /// </summary>
    public string DeadCodeCandidates { get; set; } = "[]";

    /// <summary>Totales de los candidatos de código muerto (por confianza y por qué no se listan), JSON; <c>{}</c> sin ellos.</summary>
    public string DeadCodeSummary { get; set; } = "{}";

    /// <summary>
    /// Métodos privados sin ninguna referencia desde código vivo, dentro de una clase que no es candidata: confianza alta,
    /// referencias exactas del compilador. Por fichero y línea.
    /// </summary>
    public string DeadPrivateMethods { get; set; } = "[]";
    public string UnusedDiRegistrations { get; set; } = "[]";
    public string OrphanRoutes { get; set; } = "[]";
    public string ZeroFaninClasses { get; set; } = "[]";

    /// <summary>
    /// Campos Java privados, sin los <c>static final</c>, sin <c>READS</c> entrante, con fichero y línea. Va también al
    /// informe 02.
    /// </summary>
    public string UnreadFields { get; set; } = "[]";

    /// <summary>Recuento de los campos sin leer: <c>total_unread</c>, los que nadie toca (<c>never_touched</c>) y los solo escritos (<c>write_only</c>).</summary>
    public string UnreadFieldsSummary { get; set; } = "{}";

    /// <summary>Campos privados que se escriben y nunca se leen (<c>writes &gt; 0</c> sin <c>READS</c>): estado inútil si no los usa la reflexión.</summary>
    public string WriteOnlyFields { get; set; } = "[]";

    /// <summary>
    /// Candidatos de <see cref="ZeroFaninClasses"/> con un patrón común (prefijo o sufijo del nombre, o directorio) resumidos
    /// en grupos, con <c>likely_dynamic</c> y su evidencia (<c>Support.DeadCodeGrouping</c>). Los miembros de un grupo no se
    /// repiten en <see cref="ZeroFaninClasses"/>. <c>[]</c> sin candidatos.
    /// </summary>
    public string ZeroFaninGroups { get; set; } = "[]";

    /// <summary>Lo mismo para <see cref="UnreadFields"/>, agrupando por el tipo que declara cada campo. Va también al 02.</summary>
    public string UnreadFieldGroups { get; set; } = "[]";

    /// <summary>
    /// Puntos de instanciación por nombre con argumento no literal y clases nombradas en XML de configuración
    /// (<c>Support.DynamicInstantiationScanner</c>), con el aviso de que es una heurística. <c>[]</c> sin nada que mostrar.
    /// </summary>
    public string DynamicInstantiationSites { get; set; } = "[]";

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
    /// <summary>
    /// Hallazgos de <c>detected_issues</c> con su regla y su cita <c>ruta:línea</c>: los más relevantes con un cupo por regla
    /// (no todos: en un repositorio grande son miles); los totales, en <see cref="DetectedIssuesSummary"/>.
    /// </summary>
    public string SuspectPatterns { get; set; } = "[]";

    /// <summary>Totales de <c>detected_issues</c>: hallazgos y nodos en total y por regla, las graves primero.</summary>
    public string DetectedIssuesSummary { get; set; } = "{}";

    /// <summary>Recuento de rutas por autenticación: declarada, no declarada (inferida), <c>[AllowAnonymous]</c> explícito y desconocida.</summary>
    public string RouteAuthSummary { get; set; } = "{}";

    /// <summary>JSP que el plugin Struts/JSP marca como sospechosas, con sus motivos.</summary>
    public string SuspiciousJsps { get; set; } = "[]";

    /// <summary>Credenciales escritas como literal, con el valor enmascarado (HardcodedSecretScanner).</summary>
    public string HardcodedSecrets { get; set; } = "[]";

    /// <summary>
    /// Credenciales en claro que el compilador resolvió como constantes (constantes con nombre, concatenaciones, cadenas de conexión),
    /// con el valor enmascarado: totales por tipo y las primeras con su cita <c>ruta:línea</c>.
    /// </summary>
    public string ResolvedSecrets { get; set; } = "{}";

    /// <summary>
    /// Nombres de clase que el compilador resolvió en <c>Class.forName</c>, <c>Type.GetType</c> o <c>Activator.CreateInstance</c>,
    /// con «*» por cada tramo no constante, y las clases que por eso no son código muerto.
    /// </summary>
    public string ReflectionTargets { get; set; } = "{}";

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

    /// <summary>Marcas TODO/FIXME/HACK: totales, por tipo, por fichero y las más graves con su cita <c>ruta:línea</c>.</summary>
    public string TodoMarkers { get; set; } = "{}";

    /// <summary>Código comentado (comentarios que parsean como código): bloques, líneas, ficheros con más y los bloques mayores con su cita.</summary>
    public string CommentedCode { get; set; } = "{}";
    public string ObsoleteElements { get; set; } = "[]";
    public string ComplexClasses { get; set; } = "[]";

    // Report 9: Inyección de Dependencias
    public string DiRegistrationsCatalog { get; set; } = "[]";
    public string DiLifetimesMapping { get; set; } = "[]";

    /// <summary>Totales de los registros de DI: por vida útil, sin consumidores, consumidos por el framework y con dependencia cautiva.</summary>
    public string DiSummary { get; set; } = "{}";

    /// <summary>Dependencias cautivas (<c>captive-dependency</c> de <c>detected_issues</c>): un Singleton que retiene un servicio Scoped o Transient.</summary>
    public string CaptiveDependencies { get; set; } = "[]";
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

    // Preparación por subsistema (spec 2026-10-04). Los rellenan los pasos RepositoryInventoryStep, DomainDiscoveryStep y
    // CoveragePlanningStep, y el envoltorio de modo B sobre su copia (CloneForReport). Las plantillas los leen como
    // {{ repository_inventory }}, {{ domain_map }}, {{ coverage_plan }}, {{ investigation_notes }} y {{ coverage_summary }}; el motor
    // no tiene condicionales, así que un bloque vacío se pinta vacío y el prompt tiene que tolerarlo.

    /// <summary>Versión compacta del inventario del repositorio (módulos, source sets, entradas, estado por lenguaje, LOC), JSON; <c>{}</c> sin preparación.</summary>
    public string RepositoryInventory { get; set; } = "{}";

    /// <summary>Versión compacta del mapa de dominios (subsistemas con su resumen), JSON; <c>{}</c> sin preparación.</summary>
    public string DomainMap { get; set; } = "{}";

    /// <summary>Versión compacta del plan de cobertura (subsistemas elegidos y excluidos con motivo, preguntas, evidencia mínima), JSON; <c>{}</c> sin preparación.</summary>
    public string CoveragePlan { get; set; } = "{}";

    /// <summary>
    /// Notas de las investigaciones por subsistema (texto plano con citas <c>ruta:línea</c>), que la consolidación de un informe en
    /// modo B convierte en el documento. Vacío en los informes en modo A y sin preparación.
    /// </summary>
    public string InvestigationNotes { get; set; } = "";

    /// <summary>
    /// Resumen de la cobertura del informe (subsistemas cubiertos, excluidos con motivo y fallidos) para su apartado
    /// «Cobertura». Vacío sin preparación.
    /// </summary>
    public string CoverageSummary { get; set; } = "";

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

    /// <summary>
    /// Copia para un informe: las ramas del paralelo comparten el input que sale de <c>SqlFacetExtractorStep</c> (y de los pasos de
    /// preparación), y el envoltorio de modo B rellena <see cref="InvestigationNotes"/> y <see cref="CoverageSummary"/> solo para
    /// su informe. Es una copia superficial de las facetas (son texto y números, inmutables) con <see cref="RawFacets"/> y la
    /// lista de pasos siguientes propios; <c>MemberwiseClone</c> copia también las propiedades que se añadan mañana.
    /// </summary>
    public ReportFacetInput CloneForReport()
    {
        var clone = (ReportFacetInput)MemberwiseClone();
        clone.RawFacets = new Dictionary<string, object?>(RawFacets);
        clone.NextSteps = [];
        return clone;
    }
}
