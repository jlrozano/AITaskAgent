namespace CodebaseAnalysis.Core.Progress;

/// <summary>Claves de <c>PipelineContext.Metadata</c> que comparten los steps del análisis.</summary>
public static class AnalysisContextKeys
{
    /// <summary>El <c>AnalysisPipelineInput</c> de la ejecución.</summary>
    public const string PipelineInput = "PipelineInput";

    /// <summary>Ruta de la BD del grafo; la leen las tools <c>graph_*</c>.</summary>
    public const string CbmDbPath = "CbmDbPath";

    /// <summary>El <see cref="IAnalysisReporter"/> de la ejecución. Si falta, se usa la consola.</summary>
    public const string Reporter = "AnalysisReporter";

    /// <summary>Carpeta de informes de la ejecución.</summary>
    public const string ReportsPath = "ReportsPath";

    /// <summary>Instante de inicio (<c>DateTimeOffset</c>), para la duración total.</summary>
    public const string PipelineStartedAt = "PipelineStartedAt";

    /// <summary>Las <c>KnowledgePaths</c> del repositorio (plan v2 §4.1); la ponen el chat y el pipeline.</summary>
    public const string KnowledgePaths = "KnowledgePaths";

    /// <summary>El <c>ChatTurnScope</c> del turno de chat en curso: origen de las versiones y presupuesto de escrituras.</summary>
    public const string ChatTurn = "ChatTurn";

    /// <summary>Los <c>ChatKnowledgeServices</c> del chat (describir imágenes, embeddings); null si no hay.</summary>
    public const string ChatKnowledgeServices = "ChatKnowledgeServices";

    /// <summary>
    /// <c>true</c> desde que la detección retira del grafo los informes del catálogo marcados hasta que la síntesis los
    /// reincorpora. Si el run termina con la marca puesta (fallo o cancelación antes de la síntesis),
    /// <see cref="CodebaseAnalysisPipeline"/> los reincorpora con el contenido que haya en disco.
    /// </summary>
    public const string CatalogReportsRetired = "CatalogReportsRetired";

    /// <summary>
    /// Los <c>Enrichment.DetectedLanguages</c> que encontró en disco el paso de enriquecimiento por lenguaje; con Java y sin
    /// C#, <c>DotNetEnricherStep</c> no recurre al CLI ni al fallback del LLM.
    /// </summary>
    public const string DetectedLanguages = "DetectedLanguages";

    /// <summary>
    /// La <c>Support.GraphProvenance</c> del grafo sobre el que trabajan los auditores (huella de indexación y filas de
    /// <c>enrichment_runs</c>); la pone <c>SqlFacetExtractorStep</c> y va a la línea <c>analysis-meta</c> de cada informe.
    /// </summary>
    public const string GraphProvenance = "GraphProvenance";

    // Preparación de los informes por subsistema (spec 2026-10-04). Las ponen los pasos de preparación, en este orden, entre
    // SqlFacetExtractorStep y los informes; los leen los pasos siguientes y los envoltorios de los informes en modo B.

    /// <summary>El <c>Preparation.RepositoryInventory</c> que deja <c>RepositoryInventoryStep</c>.</summary>
    public const string RepositoryInventory = "RepositoryInventory";

    /// <summary>
    /// Todos los puntos de entrada de producción del grafo (<c>IReadOnlyList&lt;InventoryEntryPoint&gt;</c>), sin el recorte que sufre la
    /// lista del artefacto del inventario. La deja <c>RepositoryInventoryStep</c> y la lee <c>DomainDiscoveryStep</c>: las métricas de
    /// entradas de cada subsistema, que puntúan el plan, no pueden salir de una muestra.
    /// </summary>
    public const string InventoryEntryPoints = "InventoryEntryPoints";

    /// <summary>
    /// <c>IReadOnlyList&lt;SqlTableReference&gt;</c>: las tablas que el código nombra en sentencias SQL escritas en cadenas, con el fichero
    /// donde se nombran (<see cref="Support.SqlTableScanner"/>). La deja <c>RepositoryInventoryStep</c> y la lee <c>DomainDiscoveryStep</c>: con
    /// ellas cada subsistema cuenta las tablas de sus ficheros aunque el grafo no traiga nodos <c>Table</c>.
    /// </summary>
    public const string InventorySqlTables = "InventorySqlTables";

    /// <summary>El <c>Preparation.DomainMap</c> que deja <c>DomainDiscoveryStep</c>.</summary>
    public const string DomainMap = "DomainMap";

    /// <summary>El <c>SharedPass.ConsolidatedRegistry</c> que deja <c>SharedPassStep</c> (solo con la pasada compartida activada).</summary>
    public const string SharedPassRegistry = "SharedPassRegistry";

    /// <summary>El <c>Preparation.CoveragePlan</c> que deja <c>CoveragePlanningStep</c>.</summary>
    public const string CoveragePlan = "CoveragePlan";

    /// <summary>El <c>Preparation.TimeBudgetPlan</c> que deja <c>TimeBudgetStep</c>.</summary>
    public const string TimeBudgetPlan = "TimeBudgetPlan";

    /// <summary>
    /// El <c>Preparation.AuditorStepRegistry</c> del pipeline: la factoría lo crea (uno por pipeline, sin estáticos) con los
    /// pasos de cada informe, y <c>TimeBudgetStep</c> escribe en ellos el <c>Timeout</c> calculado.
    /// </summary>
    public const string AuditorStepRegistry = "AuditorStepRegistry";
}
