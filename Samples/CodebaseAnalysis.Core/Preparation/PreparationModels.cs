using System.Text.Json.Serialization;
using CodebaseAnalysis.Core.Models;

namespace CodebaseAnalysis.Core.Preparation;

// Modelos de los artefactos de la preparación (spec 2026-10-04, §4): _repository_inventory.json, _domain_map.json,
// _coverage_plan.json y _time_budget.json. Son registros inmutables con propiedades `init` y listas que el productor deja
// ordenadas de forma ordinal: PreparationArtifacts los serializa sin reordenar, así que misma entrada = mismos bytes salvo
// GeneratedAt. El JSON usa snake_case (como el resto de artefactos del pipeline) y los enumerados como texto.

/// <summary>Lo que tienen en común los cuatro artefactos: cuándo se calcularon y sobre qué grafo.</summary>
public interface IPreparationArtifact
{
    /// <summary>Versión del formato del fichero; sube si cambia de forma incompatible.</summary>
    int SchemaVersion { get; }

    /// <summary>Instante de cálculo (UTC). Es lo único que cambia entre dos ejecuciones iguales.</summary>
    DateTimeOffset GeneratedAt { get; }

    /// <summary>
    /// Huella de la indexación del grafo con la que se calculó (<c>GraphProvenance.Fingerprint</c>); null si el grafo no la
    /// tenía. La regeneración la compara con la actual para decidir si reutiliza el artefacto o lo recalcula.
    /// </summary>
    string? GraphFingerprint { get; }
}

// ───────────────────────────── Inventario del repositorio ─────────────────────────────

/// <summary>Tipos de punto de entrada de <see cref="InventoryEntryPoint.Kind"/>.</summary>
public static class EntryPointKinds
{
    /// <summary>Acción de Struts: una ruta <c>struts</c> con su clase de <c>HANDLES</c>.</summary>
    public const string StrutsAction = "struts_action";

    /// <summary>Ruta HTTP de otro framework (nodo <c>Route</c> que no es de Struts).</summary>
    public const string Route = "route";

    /// <summary>Método <c>main</c>.</summary>
    public const string Main = "main";

    /// <summary>Proceso planificado (anotaciones de tipo <c>Scheduled</c>).</summary>
    public const string Scheduled = "scheduled";

    /// <summary>Consumidor de mensajes (anotaciones <c>MessageDriven</c>, <c>JmsListener</c>, <c>KafkaListener</c>...).</summary>
    public const string MessageConsumer = "message_consumer";

    /// <summary>Nodo que el indexador marca con <c>is_entry_point</c> sin que se sepa más.</summary>
    public const string Marked = "marked_entry_point";
}

/// <summary>Estados de <see cref="LanguageStatus.Status"/>.</summary>
public static class LanguageStatuses
{
    /// <summary>Hay una pasada correcta del enriquecedor registrada en <c>enrichment_runs</c>.</summary>
    public const string Enriched = "enriched";

    /// <summary>Sin enriquecer: no hay enriquecedor para el lenguaje, o no llegó a ejecutarse. El detalle dice cuál.</summary>
    public const string NotEnriched = "not_enriched";

    /// <summary>La última pasada del enriquecedor falló.</summary>
    public const string Failed = "failed";
}

/// <summary>Módulo derivado de un fichero de build; sin ninguno, un único módulo raíz.</summary>
public sealed record InventoryModule
{
    /// <summary>Carpeta del módulo, relativa al repositorio con <c>/</c>; vacía para la raíz.</summary>
    public required string Path { get; init; }

    /// <summary><c>maven</c>, <c>gradle</c>, <c>dotnet</c>, <c>node</c>, <c>go</c>, <c>cargo</c>, <c>python</c> o <c>root</c> (sin fichero de build).</summary>
    public required string Kind { get; init; }

    /// <summary>Fichero de build que lo define (nombre sin carpeta); vacío para <c>root</c>.</summary>
    public string BuildFile { get; init; } = "";
}

/// <summary>Ficheros y líneas de un tipo de source set (<c>production</c>, <c>test</c>, <c>qa</c>, <c>debug</c>, <c>generated</c>, <c>samples</c>, <c>unknown</c>).</summary>
public sealed record SourceSetSummary
{
    public required string SourceSet { get; init; }
    public int Files { get; init; }
    public long Lines { get; init; }
}

/// <summary>Un punto de entrada con su ubicación.</summary>
public sealed record InventoryEntryPoint
{
    /// <summary>Uno de <see cref="EntryPointKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>Nombre legible: la ruta, el nombre cualificado del método o de la clase.</summary>
    public required string Name { get; init; }

    /// <summary>Fichero relativo al repositorio con <c>/</c>; vacío si el grafo no lo sabe.</summary>
    public string File { get; init; } = "";

    /// <summary>Línea de inicio; 0 si se desconoce.</summary>
    public int Line { get; init; }
}

/// <summary>De dónde sale una <see cref="InventoryTable"/>.</summary>
public static class InventoryTableSources
{
    /// <summary>Un nodo <c>Table</c> o <c>AccessTable</c> del grafo.</summary>
    public const string Graph = "graph";

    /// <summary>Una sentencia SQL escrita en una cadena de código (<see cref="Support.SqlTableScanner"/>), sin nodo en el grafo.</summary>
    public const string SqlLiteral = "sql_literal";
}

/// <summary>Una tabla referenciada por código (nodos <c>Table</c> o <c>AccessTable</c> del grafo, o SQL escrito en cadenas) y cuánto la tocan.</summary>
public sealed record InventoryTable
{
    public required string Name { get; init; }

    /// <summary>El esquema escrito delante en el SQL del código (<c>app</c> en <c>FROM app.orders</c>); null si no lo lleva o la tabla viene del grafo.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Schema { get; init; }

    /// <summary>Aristas entrantes (tabla del grafo) o sentencias SQL que la nombran (tabla de <see cref="InventoryTableSources.SqlLiteral"/>).</summary>
    public int References { get; init; }

    /// <summary>Uno de <see cref="InventoryTableSources"/>.</summary>
    public string Source { get; init; } = InventoryTableSources.Graph;
}

/// <summary>Persistencia y configuración.</summary>
public sealed record InventoryPersistence
{
    /// <summary>Tablas, de más a menos referenciadas y por nombre. Vacío si el grafo no las modela.</summary>
    public IReadOnlyList<InventoryTable> Tables { get; init; } = [];

    /// <summary>Tablas que hay en total (<see cref="Tables"/> puede estar recortada).</summary>
    public int TablesTotal { get; init; }

    /// <summary>Ficheros de configuración y recursos (XML que no es de build, <c>.properties</c>, <c>.yml</c>, <c>.config</c>, <c>appsettings*.json</c>), ordenados y recortados.</summary>
    public IReadOnlyList<string> ConfigurationFiles { get; init; } = [];

    /// <summary>Ficheros de configuración que hay en total (<see cref="ConfigurationFiles"/> puede estar recortada).</summary>
    public int ConfigurationFilesTotal { get; init; }
}

/// <summary>Estado de enriquecimiento de un lenguaje presente en el repositorio.</summary>
public sealed record LanguageStatus
{
    public required string Language { get; init; }

    /// <summary>Ficheros de producción de ese lenguaje.</summary>
    public int Files { get; init; }

    /// <summary>Uno de <see cref="LanguageStatuses"/>.</summary>
    public required string Status { get; init; }

    /// <summary>Enriquecedor que lo cubre, o null si no hay ninguno para el lenguaje.</summary>
    public string? Enricher { get; init; }

    /// <summary>Id de la fila de <c>enrichment_runs</c> de la pasada que cuenta, si la hay.</summary>
    public long? RunId { get; init; }

    /// <summary>Explicación breve del estado, en español.</summary>
    public string Detail { get; init; } = "";
}

/// <summary>La deriva de cbm que mide el analizador Java y guarda en <c>enrichment_runs.details.cbm_match</c> (<c>JavaGraphWriter</c>).</summary>
public sealed record CbmMatchSummary
{
    public int UnindexedFiles { get; init; }
    public int UnindexedFilesLongPath { get; init; }
    public double TypesUnmatchedRatio { get; init; }
    public double MethodsUnmatchedRatio { get; init; }
    public bool DriftSuspected { get; init; }
}

/// <summary>Ficheros en disco frente a ficheros con nodos en el grafo.</summary>
public sealed record GraphCoverage
{
    /// <summary>Ficheros de producción con extensión de código que hay en disco.</summary>
    public int FilesOnDisk { get; init; }

    /// <summary>De ellos, los que tienen algún nodo en el grafo (conteo propio del inventario).</summary>
    public int FilesInGraph { get; init; }

    public int FilesNotInGraph { get; init; }

    /// <summary>Los primeros ficheros sin nodos, ordenados, para ver de un vistazo qué falta.</summary>
    public IReadOnlyList<string> NotInGraphSample { get; init; } = [];

    /// <summary>
    /// La medida del analizador Java si hay una pasada correcta con <c>cbm_match</c>: ficheros que cbm no indexó (también por
    /// <c>MAX_PATH</c>) y ratios de nombres que no casan. Complementa el conteo propio, que tras esa pasada ya ve los nodos
    /// que el analizador creó para los ficheros que cbm se dejó. Null si no hay pasada.
    /// </summary>
    public CbmMatchSummary? CbmMatch { get; init; }
}

/// <summary>Líneas de un fichero de producción.</summary>
public sealed record FileLines
{
    public required string Path { get; init; }
    public required string Language { get; init; }
    public int Lines { get; init; }
}

/// <summary>Líneas de producción por lenguaje.</summary>
public sealed record LanguageLines
{
    public required string Language { get; init; }
    public int Files { get; init; }
    public long Lines { get; init; }
}

/// <summary>Líneas físicas de producción contadas en disco (los nodos <c>Class</c> del grafo traen <c>lines: 0</c>).</summary>
public sealed record ProductionLines
{
    public long Total { get; init; }
    public IReadOnlyList<LanguageLines> ByLanguage { get; init; } = [];

    /// <summary>Por fichero, ordenado por ruta. Es lo que usa el descubrimiento para sumar por subsistema.</summary>
    public IReadOnlyList<FileLines> Files { get; init; } = [];
}

/// <summary>
/// <c>reports/_repository_inventory.json</c>: qué hay en el repositorio y qué sabe el grafo de ello. Determinista: mismo
/// grafo y mismo disco, mismo JSON salvo <see cref="GeneratedAt"/>.
/// </summary>
public sealed record RepositoryInventory : IPreparationArtifact
{
    public const string FileName = "_repository_inventory.json";

    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset GeneratedAt { get; init; }
    public string? GraphFingerprint { get; init; }

    public IReadOnlyList<InventoryModule> Modules { get; init; } = [];
    public IReadOnlyList<SourceSetSummary> SourceSets { get; init; } = [];
    public IReadOnlyList<InventoryEntryPoint> EntryPoints { get; init; } = [];

    /// <summary>Puntos de entrada que hay en total (<see cref="EntryPoints"/> puede estar recortada).</summary>
    public int EntryPointsTotal { get; init; }

    public InventoryPersistence Persistence { get; init; } = new();
    public IReadOnlyList<LanguageStatus> Languages { get; init; } = [];
    public GraphCoverage GraphCoverage { get; init; } = new();
    public ProductionLines ProductionLoc { get; init; } = new();

    /// <summary>Avisos de la construcción (lecturas que fallaron, rutas del grafo que no casan con el disco...).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

// ───────────────────────────── Mapa de dominios ─────────────────────────────

/// <summary>Quién puso el nombre de un subsistema.</summary>
public static class SubsystemNameOrigins
{
    /// <summary>Lo nombró el LLM y el nombre pasó la validación en código.</summary>
    public const string Llm = "llm";

    /// <summary>Derivado de la carpeta raíz o del término dominante: el LLM no respondió, omitió el cluster o respondió mal.</summary>
    public const string Derived = "derived";
}

/// <summary>Clase de un subsistema (<see cref="Subsystem.Kind"/>).</summary>
public static class SubsystemKinds
{
    /// <summary>Una capacidad: un grupo de ficheros que hace algo reconocible (lo normal).</summary>
    public const string Capability = "capability";

    /// <summary>
    /// La infraestructura compartida (<see cref="DomainMapBuilder"/>, <see cref="SharedInfrastructure"/>): tipos de los que dependen muchas comunidades
    /// distintas y que no tienen entradas ni estados propios (utilidades, configuración, marco común). Es un único subsistema por mapa.
    /// </summary>
    public const string Shared = "shared";
}

/// <summary>Prioridad que el LLM da a un subsistema al nombrarlo (la puntuación determinista del plan manda al elegir).</summary>
public enum SubsystemPriority
{
    Unspecified,
    Low,
    Medium,
    High
}

/// <summary>Señales de un subsistema con las que <c>CoveragePlanningStep</c> lo puntúa.</summary>
public sealed record SubsystemMetrics
{
    /// <summary>Ficheros de producción asignados.</summary>
    public int Files { get; init; }

    /// <summary>Líneas físicas de producción de esos ficheros.</summary>
    public long Loc { get; init; }

    /// <summary>
    /// Centralidad en el grafo de tipos, 0-1 normalizada: peso de las aristas que llegan desde fuera del subsistema entre el
    /// total. 0 si el grafo era pobre y mandó la estructura.
    /// </summary>
    public double Centrality { get; init; }

    public int EntryPoints { get; init; }

    /// <summary>Tipos distintos de punto de entrada (acción, <c>main</c>, planificado, consumidor...).</summary>
    public int EntryPointKinds { get; init; }

    public int Tables { get; init; }

    /// <summary>Enumerados y estados propios.</summary>
    public int States { get; init; }

    /// <summary>Integraciones externas (clientes de mensajería, HTTP, ficheros...), según lo que el grafo dejó.</summary>
    public int Integrations { get; init; }

    /// <summary>Puntos de uso dinámico (reflexión, instanciación por nombre) dentro del subsistema.</summary>
    public int DynamicUseSites { get; init; }
}

/// <summary>
/// Un subsistema: un grupo de ficheros de producción que forma una capacidad reconocible, descubierto de forma determinista
/// (estructura, comunidades del grafo y vocabulario) y nombrado después, validado en código.
/// </summary>
public sealed record Subsystem
{
    /// <summary>Identificador estable derivado del contenido (carpeta raíz o hash de sus ficheros): igual entre ejecuciones.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Descripción corta; vacía si el nombre es derivado.</summary>
    public string Description { get; init; } = "";

    public SubsystemPriority Priority { get; init; } = SubsystemPriority.Unspecified;

    /// <summary>Uno de <see cref="SubsystemNameOrigins"/>.</summary>
    public string NameOrigin { get; init; } = SubsystemNameOrigins.Derived;

    /// <summary>
    /// Uno de <see cref="SubsystemKinds"/>. Un mapa guardado antes de existir la clase no la trae y se lee como <see cref="SubsystemKinds.Capability"/>.
    /// Un subsistema compartido no entra entre los K de los informes funcional y de flujos (la infraestructura no es una capacidad de negocio que
    /// describir) pero sí en los de arquitectura y errores.
    /// </summary>
    public string Kind { get; init; } = SubsystemKinds.Capability;

    /// <summary>Si es la infraestructura compartida (<see cref="Kind"/> = <see cref="SubsystemKinds.Shared"/>).</summary>
    [JsonIgnore]
    public bool IsShared => string.Equals(Kind, SubsystemKinds.Shared, StringComparison.Ordinal);

    /// <summary>Source set de sus ficheros (<c>production</c>, <c>test</c>, <c>qa</c>, <c>generated</c>...): el plan excluye los que no son producción.</summary>
    public string SourceSet { get; init; } = "production";

    /// <summary>Carpetas raíz que lo componen, relativas al repositorio, con <c>/</c>; más de una cuando la capacidad está en ramas distantes del árbol.</summary>
    public IReadOnlyList<string> Roots { get; init; } = [];

    /// <summary>Término dominante del vocabulario con que se fundió o nombró, en minúsculas; vacío si no lo hubo.</summary>
    public string DominantTerm { get; init; } = "";

    /// <summary>Ficheros asignados, relativos al repositorio con <c>/</c> y ordenados. Cada fichero está en un único subsistema.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Tipos más relevantes (nombres cualificados), por importancia y nombre.</summary>
    public IReadOnlyList<string> KeyTypes { get; init; } = [];

    /// <summary>Puntos de entrada del subsistema.</summary>
    public IReadOnlyList<InventoryEntryPoint> EntryPoints { get; init; } = [];

    /// <summary>Tablas que tocan sus clases.</summary>
    public IReadOnlyList<string> Tables { get; init; } = [];

    public SubsystemMetrics Metrics { get; init; } = new();
}

/// <summary>Cómo se calcularon los clusters: con comunidades del grafo o solo con la estructura.</summary>
public static class DomainMapBases
{
    /// <summary>Estructura, comunidades del grafo y vocabulario.</summary>
    public const string Graph = "graph";

    /// <summary>El grafo no tenía referencias suficientes: mandó la estructura de carpetas y de Struts.</summary>
    public const string StructureOnly = "structure_only";
}

/// <summary>
/// <c>reports/_domain_map.json</c>: los subsistemas del repositorio y qué fichero de producción pertenece a cada uno. Cada
/// fichero de producción está en exactamente un subsistema o en <see cref="UnassignedFiles"/>.
/// </summary>
public sealed record DomainMap : IPreparationArtifact
{
    public const string FileName = "_domain_map.json";

    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset GeneratedAt { get; init; }
    public string? GraphFingerprint { get; init; }

    /// <summary>Uno de <see cref="DomainMapBases"/>.</summary>
    public string Basis { get; init; } = DomainMapBases.Graph;

    /// <summary>Subsistemas ordenados por identificador.</summary>
    public IReadOnlyList<Subsystem> Subsystems { get; init; } = [];

    /// <summary>Ficheros de producción que no entraron en ningún subsistema, ordenados.</summary>
    public IReadOnlyList<string> UnassignedFiles { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

// ───────────────────────────── Plan de cobertura ─────────────────────────────

/// <summary>Una señal de la puntuación de un subsistema.</summary>
public sealed record ScoreComponent
{
    /// <summary><c>centrality</c>, <c>entry_points</c>, <c>tables</c>, <c>states</c>, <c>integrations</c> o <c>dynamic_use_risk</c>.</summary>
    public required string Name { get; init; }

    /// <summary>La señal normalizada 0-1 entre los subsistemas.</summary>
    public double Value { get; init; }

    public double Weight { get; init; }

    /// <summary><see cref="Value"/> por <see cref="Weight"/>.</summary>
    public double Weighted { get; init; }
}

/// <summary>La puntuación de un subsistema y de qué se compone.</summary>
public sealed record SubsystemScore
{
    public required string SubsystemId { get; init; }
    public required string Name { get; init; }
    public double Score { get; init; }
    public IReadOnlyList<ScoreComponent> Components { get; init; } = [];
}

/// <summary>Un subsistema elegido (o muestreado como cola larga) para un informe.</summary>
public sealed record CoverageSelection
{
    public required string SubsystemId { get; init; }
    public required string Name { get; init; }

    /// <summary>Puesto por puntuación, desde 1.</summary>
    public int Rank { get; init; }

    public double Score { get; init; }
    public long Loc { get; init; }
}

/// <summary>Motivos de <see cref="CoverageExclusion.Reason"/>.</summary>
public static class ExclusionReasons
{
    public const string TestSource = "test_source";
    public const string QaSource = "qa_source";
    public const string DebugSource = "debug_source";
    public const string GeneratedSource = "generated_source";
    public const string SamplesSource = "samples_source";

    /// <summary>Infraestructura compartida (<see cref="SubsystemKinds.Shared"/>) en un informe de capacidades (funcional, flujos).</summary>
    public const string SharedInfrastructure = "shared_infrastructure";
    public const string BelowTopK = "below_top_k";
    public const string TrimmedByTimeBudget = "trimmed_by_time_budget";
}

/// <summary>Un subsistema que un informe no investiga, con el motivo.</summary>
public sealed record CoverageExclusion
{
    public required string SubsystemId { get; init; }
    public required string Name { get; init; }

    /// <summary>Uno de <see cref="ExclusionReasons"/>.</summary>
    public required string Reason { get; init; }
}

/// <summary>Un subsistema elegido cuya investigación falló o agotó su tiempo: queda «no cubierto» y el informe se escribe igual.</summary>
public sealed record CoverageFailure
{
    public required string SubsystemId { get; init; }
    public required string Name { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// La cobertura de un informe en modo B: qué subsistemas investiga, cuáles deja fuera y por qué, qué se le pregunta a cada
/// uno y qué evidencia mínima se exige. <see cref="Failed"/> lo rellena el envoltorio del informe al terminar.
/// </summary>
public sealed record ReportCoverage
{
    public required ReportType Report { get; init; }

    /// <summary>
    /// La puntuación de cada subsistema de producción para este informe, de mayor a menor y por identificador: los pesos base por el
    /// perfil del informe (<see cref="PreparationOptions.ReportProfiles"/>), de modo que cada informe puede elegir subsistemas distintos.
    /// Los componentes llevan el peso efectivo.
    /// </summary>
    public IReadOnlyList<SubsystemScore> Scores { get; init; } = [];

    /// <summary>Como mucho K, por la puntuación de este informe descendente.</summary>
    public IReadOnlyList<CoverageSelection> Chosen { get; init; } = [];

    /// <summary>Muestra estable de los que quedaron fuera (subconjunto de <see cref="Excluded"/> con <c>below_top_k</c>).</summary>
    public IReadOnlyList<CoverageSelection> LongTail { get; init; } = [];

    /// <summary>Todos los subsistemas no elegidos, con motivo, ordenados por identificador.</summary>
    public IReadOnlyList<CoverageExclusion> Excluded { get; init; } = [];

    /// <summary>Preguntas de la investigación, de la plantilla del informe; genéricas.</summary>
    public IReadOnlyList<string> Questions { get; init; } = [];

    /// <summary>Evidencia mínima exigida por subsistema (entradas, flujos completos, caminos alternativos, un contraejemplo).</summary>
    public IReadOnlyList<string> MinimumEvidence { get; init; } = [];

    public IReadOnlyList<CoverageFailure> Failed { get; init; } = [];
}

/// <summary><c>reports/_coverage_plan.json</c>: qué investiga cada informe en modo B.</summary>
public sealed record CoveragePlan : IPreparationArtifact
{
    public const string FileName = "_coverage_plan.json";

    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset GeneratedAt { get; init; }
    public string? GraphFingerprint { get; init; }

    /// <summary>K con el que se calculó (el de <c>PreparationOptions</c>; <c>TimeBudgetStep</c> puede recortarlo después en <see cref="TimeBudgetPlan"/>).</summary>
    public int MaxSubsystemsPerReport { get; init; }

    /// <summary>
    /// Puntuación de cada subsistema de producción con los pesos base (sin perfil de informe), de mayor a menor y por identificador.
    /// Cada informe en modo B lleva la suya (<see cref="ReportCoverage.Scores"/>).
    /// </summary>
    public IReadOnlyList<SubsystemScore> Scores { get; init; } = [];

    /// <summary>Un elemento por informe en modo B, en el orden del catálogo.</summary>
    public IReadOnlyList<ReportCoverage> Reports { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

// ───────────────────────────── Presupuesto de tiempo ─────────────────────────────

/// <summary>Qué pieza presupuesta una entrada de <see cref="TimeBudgetPlan"/>.</summary>
public enum TimeBudgetKind
{
    /// <summary>Investigación de un subsistema de un informe en modo B.</summary>
    Investigation,

    /// <summary>Consolidación de un informe en modo B (el auditor que escribe el informe).</summary>
    Consolidation,

    /// <summary>Auditor de un informe en modo A.</summary>
    Auditor
}

/// <summary>El tiempo asignado a una pieza y de qué tamaño salió.</summary>
public sealed record TimeBudgetEntry
{
    public TimeBudgetKind Kind { get; init; }
    public required ReportType Report { get; init; }

    /// <summary>Solo en <see cref="TimeBudgetKind.Investigation"/>.</summary>
    public string? SubsystemId { get; init; }

    /// <summary>Tamaño de entrada de la fórmula: miles de líneas, o subsistemas elegidos en la consolidación.</summary>
    public double Units { get; init; }

    /// <summary>Tiempo asignado, en minutos; 0 = sin límite.</summary>
    public double Minutes { get; init; }
}

/// <summary>Un subsistema que el presupuesto quitó de un informe para que la suma cupiera en el techo global.</summary>
public sealed record TimeBudgetTrim
{
    public required ReportType Report { get; init; }
    public required string SubsystemId { get; init; }
    public required string Name { get; init; }
}

/// <summary>El <c>Timeout</c> que <c>TimeBudgetStep</c> escribe en el paso de un informe (el auditor en modo A o el envoltorio de modo B).</summary>
public sealed record ReportStepTimeout
{
    public required ReportType Report { get; init; }

    /// <summary>
    /// Tiempo del paso entero, en minutos; 0 = sin límite. En el envoltorio de modo B cubre las tandas de investigaciones
    /// (tantas como piden los subsistemas elegidos con el paralelismo configurado, cada una con el límite de su investigación más
    /// largo) más la consolidación; en un auditor, su propio presupuesto.
    /// </summary>
    public double Minutes { get; init; }
}

/// <summary><c>reports/_time_budget.json</c>: los tiempos asignados, con las entradas y los coeficientes con que se calcularon.</summary>
public sealed record TimeBudgetPlan : IPreparationArtifact
{
    public const string FileName = "_time_budget.json";

    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset GeneratedAt { get; init; }
    public string? GraphFingerprint { get; init; }

    /// <summary>Líneas de producción totales con que se calculó el auditor en modo A.</summary>
    public long TotalProductionLoc { get; init; }

    /// <summary>Los coeficientes usados (<c>CodebaseAnalysis:TimeBudget</c>).</summary>
    public TimeBudgetOptions Coefficients { get; init; } = new();

    /// <summary>Techo global del pipeline en minutos (<c>CodebaseAnalysis:Timeouts</c>); 0 = sin techo.</summary>
    public double PipelineCeilingMinutes { get; init; }

    /// <summary>Concurrencia efectiva con que se dividió la suma de presupuestos para compararla con el techo.</summary>
    public int EffectiveConcurrency { get; init; }

    /// <summary>
    /// Factor por el que subieron todos los límites al esperar turno en el limitador de llamadas LLM (1 = sin cola). Ya está dentro de
    /// <see cref="Entries"/> y <see cref="ReportTimeouts"/>.
    /// </summary>
    public double QueueFactor { get; init; } = 1;

    /// <summary>Suma de todos los presupuestos, en minutos, sin dividir por la concurrencia.</summary>
    public double TotalMinutes { get; init; }

    public IReadOnlyList<TimeBudgetEntry> Entries { get; init; } = [];

    /// <summary>El <c>Timeout</c> escrito en el paso de cada informe registrado, en el orden del catálogo.</summary>
    public IReadOnlyList<ReportStepTimeout> ReportTimeouts { get; init; } = [];

    /// <summary>Subsistemas que se quitaron para caber en el techo, en el orden en que se quitaron.</summary>
    public IReadOnlyList<TimeBudgetTrim> Trimmed { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}
