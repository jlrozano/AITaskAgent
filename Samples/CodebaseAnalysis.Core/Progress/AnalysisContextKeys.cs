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
    /// C#, <c>BdpEnricherStep</c> no recurre al CLI ni al fallback del LLM.
    /// </summary>
    public const string DetectedLanguages = "DetectedLanguages";

    /// <summary>
    /// La <c>Support.GraphProvenance</c> del grafo sobre el que trabajan los auditores (huella de indexación y filas de
    /// <c>enrichment_runs</c>); la pone <c>SqlFacetExtractorStep</c> y va a la línea <c>analysis-meta</c> de cada informe.
    /// </summary>
    public const string GraphProvenance = "GraphProvenance";
}
