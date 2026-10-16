using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Support;
using Microsoft.Data.Sqlite;

namespace CodebaseAnalysis.Core.Enrichment.Java;

/// <summary>Contexto de una escritura del resultado Java sobre la copia del grafo.</summary>
public sealed record JavaGraphWriteRequest
{
    /// <summary>La copia del análisis (<c>.codebase/codebase.db</c>); nunca la BD de <c>~/.cache/codebase-memory-mcp</c>.</summary>
    public required string DatabasePath { get; init; }

    /// <summary>Ficheros entregados al analizador, relativos con <c>/</c>: sus aristas de cbm se sustituyen.</summary>
    public IReadOnlyList<string> AnalyzedFiles { get; init; } = [];

    /// <summary>Raíces de test (<see cref="TestRoots.Resolve"/>): sus nodos se retiran de la copia.</summary>
    public IReadOnlyList<string> TestRoots { get; init; } = [];

    /// <summary>Huella de las opciones que cambian el resultado (<see cref="EnrichmentRuns.ConfigFingerprint"/>).</summary>
    public string ConfigFingerprint { get; init; } = "";

    /// <summary>Versión del JDK si el resumen no la trae.</summary>
    public string? ToolVersion { get; init; }

    /// <summary>Inicio de la pasada (para <c>duration_ms</c>); por defecto, el de la escritura.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>
    /// Raíz del repositorio, para medir la ruta absoluta de los ficheros que cbm no indexó (límite MAX_PATH de Windows). Sin
    /// ella no se mide y <c>details.cbm_match.unindexed_files_long_path</c> queda a null.
    /// </summary>
    public string? RepositoryPath { get; init; }
}

/// <summary>Recuentos propios del analizador Java, además de los de <c>enrichment_runs</c>.</summary>
public sealed record JavaGraphStats
{
    public int TypesMatched { get; init; }
    public int TypesCreated { get; init; }
    public int TypesUnmatched { get; init; }
    public int MethodsMatched { get; init; }
    public int MethodsCreated { get; init; }

    /// <summary>Declaraciones de sobrecarga creadas como nodo propio (incluidas en <see cref="MethodsCreated"/>).</summary>
    public int OverloadsCreated { get; init; }

    public int MethodsUnmatched { get; init; }

    /// <summary>
    /// Ficheros analizados sin ningún nodo de cbm en su <c>file_path</c>: cbm no los indexó (p. ej. por una ruta por encima
    /// del MAX_PATH de Windows) y todas sus declaraciones se crean. Es el mismo recuento que
    /// <see cref="EnrichmentOutcome.FilesUnmatched"/>.
    /// </summary>
    public int UnindexedFiles { get; init; }

    /// <summary>
    /// De <see cref="UnindexedFiles"/>, los que tienen una ruta absoluta de <see cref="JavaGraphWriter.WindowsMaxPath"/>
    /// caracteres o más; null si no se conoce la raíz del repositorio.
    /// </summary>
    public int? UnindexedLongPathFiles { get; init; }

    /// <summary>Tipos creados (incluidos en <see cref="TypesCreated"/>) en ficheros que cbm no indexó.</summary>
    public int TypesCreatedInUnindexedFiles { get; init; }

    /// <summary>Métodos creados sin contar sobrecargas (incluidos en <see cref="MethodsCreated"/>) en ficheros que cbm no indexó.</summary>
    public int MethodsCreatedInUnindexedFiles { get; init; }

    /// <summary>
    /// Campos casados con el nodo de cbm de su tipo (Q5): el <c>Field</c> y, en las constantes de enum, el <c>Variable</c>
    /// (<see cref="FieldsMatchedToVariable"/>).
    /// </summary>
    public int FieldsMatched { get; init; }

    /// <summary>
    /// De <see cref="FieldsMatched"/>, los anclados en un <c>Variable</c> de cbm en <c>&lt;QN del tipo&gt;.&lt;campo&gt;</c>:
    /// cbm no crea <c>Field</c> para las constantes de enum, sino un <c>Variable</c> con ese QN.
    /// </summary>
    public int FieldsMatchedToVariable { get; init; }

    /// <summary>Campos sin <c>Field</c> de cbm creados como nodo propio en <c>&lt;QN del tipo&gt;.&lt;campo&gt;</c>.</summary>
    public int FieldsCreated { get; init; }

    /// <summary>Campos que ni casan ni se crean; el motivo va en <see cref="FieldsUnmatchedByReason"/>.</summary>
    public int FieldsUnmatched { get; init; }

    /// <summary>
    /// <see cref="FieldsUnmatched"/> por motivo: <see cref="JavaGraphWriter.ReasonOwnerWithoutNode"/> (el tipo propietario
    /// no tiene nodo) y <see cref="JavaGraphWriter.ReasonQualifiedNameTaken"/> (el QN lo ocupa otro nodo, P9).
    /// </summary>
    public IReadOnlyDictionary<string, long> FieldsUnmatchedByReason { get; init; } = new Dictionary<string, long>();

    /// <summary>Campos creados (incluidos en <see cref="FieldsCreated"/>) en ficheros que cbm no indexó.</summary>
    public int FieldsCreatedInUnindexedFiles { get; init; }

    /// <summary>
    /// Campos creados (incluidos en <see cref="FieldsCreated"/>) de interfaces, enums y anotaciones en ficheros que cbm sí
    /// indexó: cbm solo guarda como <c>Field</c> los campos de las clases, así que estos se crean siempre y, como las
    /// sobrecargas en los métodos, no cuentan para la deriva.
    /// </summary>
    public int FieldsCreatedWithoutCbmCounterpart { get; init; }

    /// <summary><c>Field</c> de cbm de los ficheros analizados con los que no casó ningún campo del analizador.</summary>
    public int CbmFieldsUnmatched { get; init; }

    public int CallEdges { get; init; }
    public int InheritsEdges { get; init; }
    public int ImplementsEdges { get; init; }
    public int DefinesMethodEdges { get; init; }

    /// <summary><c>DEFINES</c> de un tipo a los campos creados.</summary>
    public int DefinesFieldEdges { get; init; }

    public int UsesTypeEdges { get; init; }
    public int ReadsEdges { get; init; }
    public int WritesEdges { get; init; }

    /// <summary><c>OVERRIDES</c> de un método al que sobrescribe o implementa (schema 3).</summary>
    public int OverridesEdges { get; init; }

    /// <summary><c>IMPLEMENTS</c> con <c>is_inherited</c> a las interfaces que una clase recibe de su superclase o de otra interfaz.</summary>
    public int InheritedImplementsEdges { get; init; }

    /// <summary>Métodos con <c>fan_in</c>, <c>fan_out</c> y <c>hotspot</c> (schema 3).</summary>
    public int MethodsWithMetrics { get; init; }

    /// <summary>Hallazgos de <c>detected_issues</c> escritos, por regla (schema 3).</summary>
    public IReadOnlyDictionary<string, long> IssuesByRule { get; init; } = new Dictionary<string, long>();

    /// <summary>Nodos con <c>detected_issues</c>.</summary>
    public int NodesWithIssues { get; init; }

    /// <summary>Hallazgos cuyo método, campo o tipo no tiene nodo.</summary>
    public int IssuesUnmatched { get; init; }

    /// <summary>Marcas TODO/FIXME/HACK escritas (schema 3).</summary>
    public int TodoMarkers { get; init; }

    /// <summary>Rachas de código comentado escritas (schema 3).</summary>
    public int CommentedCodeBlocks { get; init; }

    /// <summary>Credenciales en claro escritas en <c>hardcoded_secrets</c> (con el valor enmascarado).</summary>
    public int HardcodedSecrets { get; init; }

    /// <summary>Nodos con <c>hardcoded_secrets</c>.</summary>
    public int NodesWithSecrets { get; init; }

    /// <summary>Nombres de clase escritos en <c>reflection_targets</c>.</summary>
    public int ReflectionTargets { get; init; }

    /// <summary>Nodos <c>File</c> con la codificación con la que se leyó el fichero (schema 3).</summary>
    public int FilesWithEncoding { get; init; }

    /// <summary>
    /// <c>Function</c> gemelos de métodos de interfaz a los que se sustituyó el <c>docstring</c> de cbm; no cuentan como nodos
    /// actualizados.
    /// </summary>
    public int TwinDocstrings { get; init; }

    /// <summary>Aristas de cbm sustituidas por tipo (<c>CALLS</c>, <c>USAGE</c>…), las que salen de los ficheros analizados.</summary>
    public IReadOnlyDictionary<string, long> CbmEdgesReplacedByType { get; init; } = new Dictionary<string, long>();

    /// <summary>Referencias del proyecto (sumando el <c>count</c> de los registros <c>ref</c>) que no llegan a arista.</summary>
    public long ProjectRefsUnmatched { get; init; }

    /// <summary>
    /// <see cref="ProjectRefsUnmatched"/> por motivo: <see cref="JavaGraphWriter.ReasonTargetWithoutNode"/> (p. ej. destinos
    /// en clases anónimas o locales, con nombre binario) y <see cref="JavaGraphWriter.ReasonSourceWithoutNode"/>.
    /// </summary>
    public IReadOnlyDictionary<string, long> ProjectRefsUnmatchedByReason { get; init; } = new Dictionary<string, long>();

    /// <summary>Llamadas por origen tal como las cuenta el analizador.</summary>
    public IReadOnlyDictionary<string, long> CallsByOrigin { get; init; } = new Dictionary<string, long>();

    /// <summary>Llamadas de proyecto cuyo origen o destino no se pudo llevar a un nodo del grafo.</summary>
    public int ProjectCallsUnmatched { get; init; }

    /// <summary>
    /// <see cref="ProjectCallsUnmatched"/> por motivo: <see cref="JavaGraphWriter.ReasonImplicitConstructor"/>,
    /// <see cref="JavaGraphWriter.ReasonTargetWithoutNode"/> (p. ej. destinos en clases anónimas o locales, con nombre
    /// binario) y <see cref="JavaGraphWriter.ReasonSourceWithoutNode"/>.
    /// </summary>
    public IReadOnlyDictionary<string, long> ProjectCallsUnmatchedByReason { get; init; } = new Dictionary<string, long>();

    /// <summary>Ficheros con diagnósticos pero sin registro <c>file</c> (el analizador no pudo leerlos).</summary>
    public int UnreadableFiles { get; init; }

    public int FilesWithErrors { get; init; }
    public int PreviousNodesRemoved { get; init; }
    public int PreviousEdgesRemoved { get; init; }

    /// <summary>
    /// Milisegundos por fase de la escritura, en orden de ejecución (<c>read</c>, <c>schema</c>, <c>remove_previous</c>,
    /// <c>remove_tests</c>, <c>load_nodes</c>, <c>replace_cbm_edges</c>, <c>types</c>, <c>methods</c>, <c>fields</c>,
    /// <c>patches</c>, <c>hierarchy</c>, <c>calls</c>, <c>refs</c>, <c>overrides</c>, <c>findings</c>, <c>run_row</c> y <c>commit</c>). Las de <c>details.write_timings_ms</c> son las mismas
    /// salvo las dos últimas, que ocurren después de escribir la fila.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, long>> WriteTimingsMs { get; init; } = [];

    /// <summary>
    /// Proporción de tipos que no casaron con un nodo de cbm y se crearon (P9), sobre los casados y los creados; 0 sin
    /// tipos. Un nodo creado no es un acierto: es la señal de que el QN del analizador no es el de cbm.
    /// </summary>
    public double TypesUnmatchedRatio => Ratio(TypesCreated, TypesMatched + TypesCreated);

    /// <summary>
    /// Lo mismo para los métodos, sin las sobrecargas: P8 las crea siempre, porque cbm tiene un nodo por nombre.
    /// </summary>
    public double MethodsUnmatchedRatio => Ratio(MethodsCreated - OverloadsCreated, MethodsMatched + MethodsCreated - OverloadsCreated);

    /// <summary>Tipos creados en ficheros que cbm sí indexó: su nombre no casa con el de cbm.</summary>
    public int TypesCreatedNameMismatch => TypesCreated - TypesCreatedInUnindexedFiles;

    /// <summary>Métodos creados sin contar sobrecargas en ficheros que cbm sí indexó.</summary>
    public int MethodsCreatedNameMismatch => MethodsCreated - OverloadsCreated - MethodsCreatedInUnindexedFiles;

    /// <summary>
    /// <see cref="TypesUnmatchedRatio"/> solo en los ficheros que cbm indexó: un tipo de un fichero sin nodos de cbm no puede
    /// casar con nada y no dice nada de cómo deriva cbm los QN.
    /// </summary>
    public double TypesNameMismatchRatio => Ratio(TypesCreatedNameMismatch, TypesMatched + TypesCreatedNameMismatch);

    /// <summary><see cref="MethodsUnmatchedRatio"/> solo en los ficheros que cbm indexó.</summary>
    public double MethodsNameMismatchRatio => Ratio(MethodsCreatedNameMismatch, MethodsMatched + MethodsCreatedNameMismatch);

    /// <summary>
    /// Lo mismo para los campos, sin los que cbm no guarda como <c>Field</c> (<see cref="FieldsCreatedWithoutCbmCounterpart"/>).
    /// </summary>
    public double FieldsUnmatchedRatio => Ratio(FieldsCreated - FieldsCreatedWithoutCbmCounterpart,
        FieldsMatched + FieldsCreated - FieldsCreatedWithoutCbmCounterpart);

    /// <summary>
    /// Campos de clase creados en ficheros que cbm sí indexó: su nombre no casa con el <c>name</c> de ningún <c>Field</c>.
    /// </summary>
    public int FieldsCreatedNameMismatch => FieldsCreated - FieldsCreatedInUnindexedFiles - FieldsCreatedWithoutCbmCounterpart;

    /// <summary><see cref="FieldsUnmatchedRatio"/> solo en los ficheros que cbm indexó.</summary>
    public double FieldsNameMismatchRatio => Ratio(FieldsCreatedNameMismatch, FieldsMatched + FieldsCreatedNameMismatch);

    /// <summary>
    /// Más de <see cref="JavaGraphWriter.CbmDriftThreshold"/> de tipos, de métodos o de campos sin casar en total, sea cual
    /// sea la causa: es lo que decide si la pasada avisa.
    /// </summary>
    public bool ExceedsUnmatchedThreshold =>
        TypesUnmatchedRatio > JavaGraphWriter.CbmDriftThreshold || MethodsUnmatchedRatio > JavaGraphWriter.CbmDriftThreshold
        || FieldsUnmatchedRatio > JavaGraphWriter.CbmDriftThreshold;

    /// <summary>
    /// Más de <see cref="JavaGraphWriter.CbmDriftThreshold"/> de tipos o de métodos sin casar en los ficheros que cbm indexó:
    /// lo normal es que casen casi todos (D3 replica las rarezas de cbm), así que suele ser que una versión nueva de cbm deriva
    /// los QN de otra forma y la pasada está duplicando sus nodos en lugar de enriquecerlos. Los ficheros que cbm no indexó no
    /// cuentan: sus declaraciones se crean siempre (<see cref="UnindexedFiles"/>). Los campos cuentan igual, con el formato de
    /// <c>name</c> de los <c>Field</c> de cbm en lugar del QN.
    /// </summary>
    public bool SuspectsCbmDrift =>
        TypesNameMismatchRatio > JavaGraphWriter.CbmDriftThreshold || MethodsNameMismatchRatio > JavaGraphWriter.CbmDriftThreshold
        || FieldsNameMismatchRatio > JavaGraphWriter.CbmDriftThreshold;

    private static double Ratio(int part, int total) => total > 0 ? (double)part / total : 0;
}

/// <summary>Resultado de <see cref="JavaGraphWriter.ApplyAsync"/>.</summary>
public sealed record JavaGraphWriteResult(EnrichmentOutcome Outcome, JavaSummaryRecord Summary, JavaGraphStats Stats);

/// <summary>
/// Aplica el resultado del analizador Java sobre la copia del grafo en una transacción (P7-P13): borra la salida anterior,
/// retira el código de test, casa tipos, métodos y campos con los nodos de cbm, crea las sobrecargas y las declaraciones que
/// cbm no tiene, completa propiedades, sustituye <c>CALLS</c>/<c>INHERITS</c>/<c>IMPLEMENTS</c> de los ficheros analizados
/// (y, con schema 2, <c>USAGE</c> por <c>USES_TYPE</c>/<c>READS</c>/<c>WRITES</c>, Q5-Q7) y deja la fila <c>ok</c> de
/// <c>enrichment_runs</c>. No sabe nada del proceso Java: recibe los registros ya leídos.
/// </summary>
public static class JavaGraphWriter
{
    public const string EnricherName = "JavaAnalyzer";

    /// <summary>
    /// Versión de las reglas de escritura; cambiarla invalida las pasadas <c>ok</c> anteriores (P3). La 2 añade campos y
    /// referencias (Q8); la 3, <c>OVERRIDES</c>, <c>fan_in</c>/<c>fan_out</c>/<c>hotspot</c>, <c>detected_issues</c>, comentarios
    /// (<c>todo_markers</c>, <c>commented_code</c>, <c>docstring</c> decodificado con la codificación del fichero) y <c>encoding</c>; la 4,
    /// las constantes resueltas (<c>hardcoded_secrets</c> con el valor enmascarado y <c>reflection_targets</c>); la 5, el papel de cada tipo
    /// (<c>type_role</c>) y su visibilidad (<c>is_public</c>).
    /// </summary>
    public const string EnricherVersion = "5";

    /// <summary>
    /// Papel del tipo, con las mismas categorías que el enriquecedor C# (<c>TypeRoleEnricher</c>) y solo con lo que da el analizador:
    /// enum y anotación son <c>model</c>; interfaz o clase abstracta, <c>contract</c> si es pública y <c>infrastructure</c> si no;
    /// una clase que extiende directamente una excepción de <c>java.lang</c>, <c>model</c>; el resto, <c>implementation</c>.
    /// </summary>
    internal static string TypeRole(JavaTypeRecord type)
    {
        if (type.Kind is "enum" or "annotation")
            return "model";
        if (type.Kind == "interface" || type.Abstract)
            return type.Access == "public" ? "contract" : "infrastructure";
        if (type.Super?.Fqn is "java.lang.Exception" or "java.lang.RuntimeException" or "java.lang.Error" or "java.lang.Throwable")
            return "model";
        return "implementation";
    }

    /// <summary>Uso de un tipo del proyecto desde un método, un campo o un tipo (Q6).</summary>
    public const string UsesTypeEdge = "USES_TYPE";

    /// <summary>Lectura de un campo del proyecto.</summary>
    public const string ReadsEdge = "READS";

    /// <summary>Escritura de un campo del proyecto.</summary>
    public const string WritesEdge = "WRITES";

    /// <summary>Método que sobrescribe o implementa a otro del proyecto (mismo nombre que la arista de C#).</summary>
    public const string OverridesEdge = "OVERRIDES";

    /// <summary>
    /// Propiedades que un valor null del parche borra (el resto las conserva, ver <see cref="EnrichmentMarks.Merge"/>): el
    /// <c>docstring</c> de cbm sale de los comentarios <c>//</c> leídos con la codificación equivocada (U+FFFD), y el del
    /// analizador, que ya conoce la del fichero, lo sustituye aunque sea para dejarlo vacío.
    /// </summary>
    private static readonly string[] NullReplacesKeys = ["docstring", "docstring_source"];

    /// <summary>Propiedades que escribe esta pasada y que, si quedaron de otra anterior sobre los mismos nodos, se retiran antes.</summary>
    private static readonly string[] OwnedKeys =
    [
        "detected_issues", "issue_rules", "todo_markers", "todo_count", "commented_code", "commented_code_lines",
        "hardcoded_secrets", "reflection_targets",
        "fan_in", "fan_out", "hotspot", "is_override", "encoding", "outer_fqn", "owner_fqn"
    ];

    /// <summary>
    /// Proporción de tipos o de métodos sin casar con cbm (creados por P9) por encima de la cual la pasada avisa de una
    /// posible deriva de los QN de cbm (<see cref="JavaGraphStats.SuspectsCbmDrift"/>). Queda en <c>details.cbm_match</c>.
    /// </summary>
    public const double CbmDriftThreshold = 0.05;

    /// <summary>
    /// Longitud de ruta absoluta a partir de la cual Windows no abre un fichero sin el prefijo de rutas largas (MAX_PATH, 260
    /// con el terminador): en la prueba M, cbm no indexó los <c>.java</c> que llegaban a ella.
    /// </summary>
    public const int WindowsMaxPath = 260;

    /// <summary>Labels con los que cbm guarda un tipo; <c>Function</c> se acepta aparte (enums, ver <c>ApplyTypesAsync</c>).</summary>
    private static readonly HashSet<string> TypeLabels = new(StringComparer.Ordinal) { "Class", "Interface", "Enum", "Annotation" };

    /// <summary>Motivos de una llamada de proyecto que no llega a arista (claves de <c>unresolved</c>).</summary>
    public const string ReasonImplicitConstructor = "implicit_constructor";

    /// <inheritdoc cref="ReasonImplicitConstructor"/>
    public const string ReasonTargetWithoutNode = "target_without_node";

    /// <inheritdoc cref="ReasonImplicitConstructor"/>
    public const string ReasonSourceWithoutNode = "source_without_node";

    /// <summary>Motivos de un campo que ni casa con un <c>Field</c> de cbm ni se crea.</summary>
    public const string ReasonOwnerWithoutNode = "owner_without_node";

    /// <inheritdoc cref="ReasonOwnerWithoutNode"/>
    public const string ReasonQualifiedNameTaken = "qn_taken";

    /// <summary>Aristas de cbm que se sustituyen desde los nodos de los ficheros analizados (P10).</summary>
    private static readonly string[] ReplacedEdgeTypes = ["CALLS", "INHERITS", "IMPLEMENTS"];

    /// <summary>
    /// Las que se sustituyen además con un resultado de schema 2, que trae los registros <c>ref</c> (Q7). Con uno de schema 1
    /// no hay con qué sustituir las <c>USAGE</c> de cbm y se conservan.
    /// </summary>
    private static readonly string[] ReplacedReferenceEdgeTypes = ["USAGE", UsesTypeEdge, ReadsEdge, WritesEdge];

    /// <exception cref="InvalidOperationException">Si la BD es la de cbm.</exception>
    /// <exception cref="InvalidDataException">Si el resultado no termina en un <c>summary</c> de la versión soportada.</exception>
    public static async Task<JavaGraphWriteResult> ApplyAsync(
        JavaGraphWriteRequest request, IEnumerable<JavaAnalysisRecord> records, CancellationToken ct)
    {
        EnrichmentMarks.AssertWorkingCopy(request.DatabasePath);
        var startedAt = request.StartedAt ?? DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var timings = new PhaseTimings();

        // Todo el resultado se valida antes de tocar la BD: un fichero truncado no debe dejar media pasada.
        var input = Collect(records, ct);
        timings.Lap("read");

        await GraphSchema.EnsureAsync(request.DatabasePath, ct);
        using var conn = await EnrichmentMarks.OpenForBulkWriteAsync(request.DatabasePath, ct);
        await EnrichmentRuns.EnsureAsync(conn, ct);
        timings.Lap("schema");

        using var tx = conn.BeginTransaction();
        using var pass = new Pass(conn, tx, await GraphProject.ResolveAsync(conn, "Codebase", ct, tx), startedAt, timings);
        var result = await pass.RunAsync(request, input, ct);

        var indexFingerprint = await EnrichmentRuns.IndexFingerprintAsync(conn, ct, tx) ?? "";
        var runId = await EnrichmentRuns.InsertAsync(conn, new EnrichmentRun
        {
            Enricher = EnricherName,
            EnricherVersion = EnricherVersion,
            ToolVersion = input.Summary.JavaVersion ?? request.ToolVersion,
            IndexFingerprint = indexFingerprint,
            ConfigFingerprint = request.ConfigFingerprint,
            StartedAt = startedAt,
            DurationMs = request.StartedAt is { } s ? (long)(DateTimeOffset.UtcNow - s).TotalMilliseconds : stopwatch.ElapsedMilliseconds,
            Status = EnrichmentRuns.StatusOk,
            Outcome = result.Outcome
        }, ct, tx);
        timings.Lap("run_row");

        tx.Commit();
        timings.Lap("commit");
        return result with
        {
            Outcome = result.Outcome with { RunId = runId },
            Stats = result.Stats with { WriteTimingsMs = timings.ToList() }
        };
    }

    /// <summary>Cronómetro por fases: cada <see cref="Lap"/> apunta lo transcurrido desde el anterior.</summary>
    private sealed class PhaseTimings
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly List<KeyValuePair<string, long>> _laps = [];
        private long _last;

        public void Lap(string phase)
        {
            var now = _stopwatch.ElapsedMilliseconds;
            _laps.Add(new(phase, now - _last));
            _last = now;
        }

        public List<KeyValuePair<string, long>> ToList() => [.. _laps];
    }

    // ---------------------------------------------------------------- lectura del resultado

    private sealed class CallAggregate
    {
        public SortedSet<int> Lines { get; } = [];
        public SortedSet<string> Kinds { get; } = new(StringComparer.Ordinal);
        public int Count { get; set; }
        public bool Initializer { get; init; }

        public void Add(int line, string kind, int count = 1)
        {
            Lines.Add(line);
            Kinds.Add(kind);
            Count += count;
        }
    }

    /// <summary>De dónde sale una referencia: un método (<c>from</c>), un campo (<c>fromField</c>) o el tipo (<c>fromType</c>).</summary>
    private enum RefSource
    {
        Method,
        Field,
        Type
    }

    /// <summary>Clave de agregación de los <c>ref</c>: la del analizador sin el fichero.</summary>
    private readonly record struct RefKey(RefSource SourceKind, string Source, string Target, string Kind, string? Access);

    private sealed class RefAggregate
    {
        public SortedSet<int> Lines { get; } = [];
        public SortedSet<string> Contexts { get; } = new(StringComparer.Ordinal);
        public int Count { get; set; }

        public void Add(IEnumerable<int> lines, IEnumerable<string> contexts, int count)
        {
            Lines.UnionWith(lines);
            Contexts.UnionWith(contexts);
            Count += count;
        }
    }

    private sealed record CollectedInput(
        List<JavaFileRecord> Files,
        List<JavaTypeRecord> Types,
        List<JavaMethodRecord> Methods,
        List<JavaFieldRecord> Fields,
        Dictionary<(string Source, string Target), CallAggregate> ProjectCalls,
        Dictionary<RefKey, RefAggregate> ProjectRefs,
        Dictionary<string, long> CallsByOrigin,
        Dictionary<string, long> UnresolvedByReason,
        int DiagnosticCount,
        HashSet<string> DiagnosticPaths,
        List<JavaIssueRecord> Issues,
        List<JavaCommentRecord> Comments,
        List<JavaConstantRecord> Constants,
        JavaSummaryRecord Summary);

    private static CollectedInput Collect(IEnumerable<JavaAnalysisRecord> records, CancellationToken ct)
    {
        var files = new List<JavaFileRecord>();
        var types = new List<JavaTypeRecord>();
        var methods = new List<JavaMethodRecord>();
        var fields = new List<JavaFieldRecord>();
        var calls = new Dictionary<(string, string), CallAggregate>();
        var refs = new Dictionary<RefKey, RefAggregate>();
        var byOrigin = new Dictionary<string, long>(StringComparer.Ordinal);
        var byReason = new Dictionary<string, long>(StringComparer.Ordinal);
        var diagnostics = 0;
        var diagnosticPaths = new HashSet<string>(StringComparer.Ordinal);
        var issues = new List<JavaIssueRecord>();
        var comments = new List<JavaCommentRecord>();
        var constants = new List<JavaConstantRecord>();
        JavaSummaryRecord? summary = null;
        var seen = 0;

        foreach (var record in records)
        {
            if (++seen % 50_000 == 0)
                ct.ThrowIfCancellationRequested();

            switch (record)
            {
                case JavaFileRecord file:
                    files.Add(file);
                    break;
                case JavaTypeRecord type:
                    types.Add(type);
                    break;
                case JavaMethodRecord method:
                    methods.Add(method);
                    break;
                case JavaCallRecord call:
                    var origin = string.IsNullOrEmpty(call.Origin) ? JavaOrigins.Unresolved : call.Origin;
                    Increment(byOrigin, origin);
                    if (origin == JavaOrigins.Unresolved)
                        Increment(byReason, call.Reason ?? "unknown");
                    else if (origin == JavaOrigins.Project && call.To == null)
                        Increment(byReason, ReasonTargetWithoutNode);
                    else if (origin == JavaOrigins.Project)
                    {
                        // Fuera de un método, la llamada sale del tipo (inicializadores): clave aparte para no mezclarlas.
                        var source = call.From ?? "#init " + call.FromType;
                        var target = call.To!;
                        if (!calls.TryGetValue((source, target), out var aggregate))
                            calls[(source, target)] = aggregate = new CallAggregate { Initializer = call.From == null };
                        aggregate.Add(call.Line, call.Kind ?? "call");
                    }
                    break;
                case JavaFieldRecord field:
                    fields.Add(field);
                    break;
                case JavaRefRecord reference:
                    // El analizador solo detalla los destinos del proyecto; el resto va contado en summary.refs.
                    if (reference.Origin != JavaOrigins.Project || string.IsNullOrEmpty(reference.To))
                        break;
                    var key = reference.From != null
                        ? new RefKey(RefSource.Method, reference.From, reference.To, reference.Kind, reference.Access)
                        : reference.FromField != null
                            ? new RefKey(RefSource.Field, reference.FromField, reference.To, reference.Kind, reference.Access)
                            : new RefKey(RefSource.Type, reference.FromType, reference.To, reference.Kind, reference.Access);
                    if (!refs.TryGetValue(key, out var references))
                        refs[key] = references = new RefAggregate();
                    references.Add(reference.Lines ?? [], reference.Contexts ?? [], reference.Count);
                    break;
                case JavaIssueRecord issue:
                    issues.Add(issue);
                    break;
                case JavaCommentRecord comment:
                    comments.Add(comment);
                    break;
                case JavaConstantRecord constant:
                    constants.Add(constant);
                    break;
                case JavaDiagnosticRecord diagnostic:
                    diagnostics++;
                    // path y line pueden venir a null (diagnóstico sin fuente); un fichero ilegible da diag sin file.
                    if (!string.IsNullOrEmpty(diagnostic.Path))
                        diagnosticPaths.Add(diagnostic.Path);
                    break;
                case JavaSummaryRecord s:
                    summary = s;
                    break;
            }
        }

        if (summary == null)
            throw new InvalidDataException("El resultado del analizador Java no termina con el registro 'summary': está incompleto.");
        if (!JavaAnalysisReader.IsSupportedSchema(summary.Schema))
            throw new InvalidDataException(
                $"El resultado del analizador Java es de la versión de contrato {summary.Schema}; se esperaba entre "
                + $"{JavaAnalysisReader.MinSupportedSchema} y {JavaAnalysisReader.SupportedSchema}.");

        return new CollectedInput(files, types, methods, fields, calls, refs, byOrigin, byReason, diagnostics, diagnosticPaths, issues, comments, constants, summary);
    }

    private static void Increment(Dictionary<string, long> counts, string key, long by = 1) =>
        counts[key] = counts.GetValueOrDefault(key) + by;

    // ---------------------------------------------------------------- escritura

    private sealed record GraphNode(long Id, string Label, string Name, string QualifiedName, string FilePath, int StartLine, int EndLine,
        string? Properties);

    /// <summary>Arista entre nodos ya escrita, guardada para calcular <c>fan_in</c>/<c>fan_out</c> sin volver a leer la BD.</summary>
    private readonly record struct WrittenEdge(long Source, long Target);

    private sealed record MappedType(long Id, string QualifiedName, string Kind, string PathPrefix);

    private sealed class Pass(SqliteConnection conn, SqliteTransaction tx, string project, DateTimeOffset enrichedAt, PhaseTimings timings)
        : IDisposable
    {
        private readonly Dictionary<string, GraphNode> _byQn = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MappedType> _typesByFqn = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _methodsByKey = new(StringComparer.Ordinal);
        private readonly Dictionary<long, JsonObject> _pendingPatches = [];
        private readonly Dictionary<long, (int Start, int End)> _pendingLines = [];
        private readonly HashSet<long> _typeNodeIds = [];
        private readonly Dictionary<string, long> _unmatchedCalls = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _fieldsByKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _unmatchedFields = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _unmatchedRefs = new(StringComparer.Ordinal);

        // Aristas escritas que cuentan para el fan: llamadas, usos de tipo y accesos a campos, y las de OVERRIDES.
        private readonly HashSet<WrittenEdge> _callPairs = [];
        private readonly HashSet<WrittenEdge> _referencePairs = [];
        private readonly Dictionary<long, List<long>> _overriddenBy = [];

        // Propiedades ya escritas de un nodo por un FlushPatches anterior: el siguiente parche se suma a ellas, no a las de cbm.
        private readonly Dictionary<long, string> _written = [];
        private readonly HashSet<long> _updatedNodes = [], _createdNodes = [], _twinNodes = [];
        private int _twinDocstrings;
        private int _schema = 2;
        private int _overridesEdges, _inheritedImplementsEdges, _methodsWithMetrics, _nodesWithIssues, _issuesUnmatched, _todoMarkers, _commentedCode;
        private int _hardcodedSecrets, _nodesWithSecrets, _reflectionTargets;
        private int _filesWithEncoding;
        private readonly SortedDictionary<string, long> _issuesByRule = new(StringComparer.Ordinal);
        private SqliteCommand? _insertEdge;
        private SqliteParameter? _edgeSource, _edgeTarget, _edgeType, _edgeProperties;
        private string? _plainEdgeProperties;
        private SqliteCommand? _insertNode;
        private SqliteCommand? _updateNode;

        private int _nodesCreated, _nodesUpdated, _edgesCreated;
        private int _typesMatched, _typesCreated, _typesUnmatched;
        private int _methodsMatched, _methodsCreated, _overloadsCreated, _methodsUnmatched;
        private int _fieldsMatched, _fieldsMatchedVariable, _fieldsCreated, _fieldsUnmatched, _fieldsCreatedUnindexed, _fieldsWithoutCounterpart;
        private int _cbmFieldsUnmatched;
        private int _callEdges, _inheritsEdges, _implementsEdges, _definesEdges, _projectCallsUnmatched;
        private int _definesFieldEdges, _usesTypeEdges, _readsEdges, _writesEdges;
        private long _projectRefsUnmatched;
        private int _typesCreatedUnindexed, _methodsCreatedUnindexed;

        // Ficheros con algún nodo de cbm antes de escribir nada: una declaración creada fuera de ellos no es deriva de los QN.
        private HashSet<string> _filesWithCbmNodes = new(StringComparer.Ordinal);

        public async Task<JavaGraphWriteResult> RunAsync(JavaGraphWriteRequest request, CollectedInput input, CancellationToken ct)
        {
            _schema = input.Summary.Schema;

            // P12: la salida de la pasada anterior de este enriquecedor.
            var (previousNodes, previousEdges) = await EnrichmentMarks.RemovePreviousOutputAsync(conn, tx, EnricherName, ct);
            timings.Lap("remove_previous");

            // P13: el código de test no entra en el grafo.
            var (testNodes, _) = await EnrichmentMarks.RemoveNodesUnderAsync(conn, tx, request.TestRoots, ct);
            timings.Lap("remove_tests");

            var analyzedFiles = request.AnalyzedFiles
                .Concat(input.Files.Select(f => f.Path))
                .Select(p => p.Replace('\\', '/'))
                .Where(p => p.Length > 0 && !TestRoots.Contains(request.TestRoots, p))
                .ToHashSet(StringComparer.Ordinal);
            await LoadNodesAsync(analyzedFiles, ct);
            var filesWithNodes = _byQn.Values.Select(n => n.FilePath).ToHashSet(StringComparer.Ordinal);
            _filesWithCbmNodes = filesWithNodes;
            timings.Lap("load_nodes");

            // P10 y Q7: las aristas heurísticas de cbm que salen de los ficheros analizados se sustituyen por las resueltas.
            string[] replacedTypes = input.Summary.Schema >= 2 ? [.. ReplacedEdgeTypes, .. ReplacedReferenceEdgeTypes] : ReplacedEdgeTypes;
            var cbmEdgesReplacedByType = await DeleteCbmEdgesAsync(replacedTypes, ct);
            var cbmEdgesReplaced = (int)cbmEdgesReplacedByType.Values.Sum();
            timings.Lap("replace_cbm_edges");

            await ApplyTypesAsync(input.Types, ct);
            timings.Lap("types");
            await ApplyMethodsAsync(input.Methods, ct);
            timings.Lap("methods");
            await ApplyFieldsAsync(input.Fields, ct);
            timings.Lap("fields");
            await FlushPatchesAsync(ct);
            timings.Lap("patches");
            await WriteHierarchyAsync(input.Types, ct);
            timings.Lap("hierarchy");
            await WriteCallsAsync(input.ProjectCalls, ct);
            timings.Lap("calls");
            await WriteRefsAsync(input.ProjectRefs, ct);
            timings.Lap("refs");
            await WriteOverridesAsync(input.Methods, ct);
            timings.Lap("overrides");
            await ApplyFindingsAsync(input, ct);
            timings.Lap("findings");

            var analyzed = input.Files.Select(f => f.Path).Distinct(StringComparer.Ordinal).ToList();
            var filesWithErrors = input.Files.Count(f => f.Errors > 0);
            var unresolved = new SortedDictionary<string, long>(input.UnresolvedByReason, StringComparer.Ordinal);
            foreach (var (reason, count) in _unmatchedCalls)
                unresolved[reason] = unresolved.GetValueOrDefault(reason) + count;
            var analyzedSet = analyzed.ToHashSet(StringComparer.Ordinal);
            var unreadable = input.DiagnosticPaths.Count(p => !analyzedSet.Contains(p));
            var unindexed = analyzed.Where(f => !filesWithNodes.Contains(f)).ToList();
            int? unindexedLongPath = string.IsNullOrWhiteSpace(request.RepositoryPath)
                ? null
                : unindexed.Count(f => AbsoluteLength(request.RepositoryPath, f) >= WindowsMaxPath);

            var summary = input.Summary;
            var stats = new JavaGraphStats
            {
                TypesMatched = _typesMatched,
                TypesCreated = _typesCreated,
                TypesUnmatched = _typesUnmatched,
                MethodsMatched = _methodsMatched,
                MethodsCreated = _methodsCreated,
                OverloadsCreated = _overloadsCreated,
                MethodsUnmatched = _methodsUnmatched,
                UnindexedFiles = unindexed.Count,
                UnindexedLongPathFiles = unindexedLongPath,
                TypesCreatedInUnindexedFiles = _typesCreatedUnindexed,
                MethodsCreatedInUnindexedFiles = _methodsCreatedUnindexed,
                FieldsMatched = _fieldsMatched,
                FieldsCreated = _fieldsCreated,
                FieldsUnmatched = _fieldsUnmatched,
                FieldsUnmatchedByReason = new SortedDictionary<string, long>(_unmatchedFields, StringComparer.Ordinal),
                FieldsCreatedInUnindexedFiles = _fieldsCreatedUnindexed,
                FieldsMatchedToVariable = _fieldsMatchedVariable,
                FieldsCreatedWithoutCbmCounterpart = _fieldsWithoutCounterpart,
                CbmFieldsUnmatched = _cbmFieldsUnmatched,
                CallEdges = _callEdges,
                InheritsEdges = _inheritsEdges,
                ImplementsEdges = _implementsEdges,
                DefinesMethodEdges = _definesEdges,
                DefinesFieldEdges = _definesFieldEdges,
                UsesTypeEdges = _usesTypeEdges,
                ReadsEdges = _readsEdges,
                WritesEdges = _writesEdges,
                OverridesEdges = _overridesEdges,
                InheritedImplementsEdges = _inheritedImplementsEdges,
                MethodsWithMetrics = _methodsWithMetrics,
                IssuesByRule = new SortedDictionary<string, long>(_issuesByRule, StringComparer.Ordinal),
                NodesWithIssues = _nodesWithIssues,
                IssuesUnmatched = _issuesUnmatched,
                TodoMarkers = _todoMarkers,
                CommentedCodeBlocks = _commentedCode,
                HardcodedSecrets = _hardcodedSecrets,
                NodesWithSecrets = _nodesWithSecrets,
                ReflectionTargets = _reflectionTargets,
                FilesWithEncoding = _filesWithEncoding,
                TwinDocstrings = _twinDocstrings,
                CbmEdgesReplacedByType = cbmEdgesReplacedByType,
                ProjectRefsUnmatched = _projectRefsUnmatched,
                ProjectRefsUnmatchedByReason = new SortedDictionary<string, long>(_unmatchedRefs, StringComparer.Ordinal),
                CallsByOrigin = new SortedDictionary<string, long>(input.CallsByOrigin, StringComparer.Ordinal),
                ProjectCallsUnmatched = _projectCallsUnmatched,
                ProjectCallsUnmatchedByReason = new SortedDictionary<string, long>(_unmatchedCalls, StringComparer.Ordinal),
                UnreadableFiles = unreadable,
                FilesWithErrors = filesWithErrors,
                PreviousNodesRemoved = previousNodes,
                PreviousEdgesRemoved = previousEdges
            };

            var byKind = summary.Diagnostics ?? new Dictionary<string, long>();
            var diagnostics = new JsonObject
            {
                ["error"] = byKind.TryGetValue("error", out var errors) ? errors : input.DiagnosticCount,
                ["warning"] = byKind.TryGetValue("warning", out var warnings) ? warnings : 0,
                ["files_with_errors"] = filesWithErrors,
                ["unreadable_files"] = unreadable,
                ["missing_packages"] = ToArray(summary.MissingPackages ?? [])
            };
            var details = new JsonObject
            {
                ["schema"] = summary.Schema,
                ["analyzer_version"] = summary.AnalyzerVersion,
                ["java_version"] = summary.JavaVersion,
                ["calls"] = ToObject(stats.CallsByOrigin),
                ["types"] = new JsonObject { ["matched"] = _typesMatched, ["created"] = _typesCreated, ["unmatched"] = _typesUnmatched },
                ["methods"] = new JsonObject
                {
                    ["matched"] = _methodsMatched, ["created"] = _methodsCreated, ["overloads"] = _overloadsCreated, ["unmatched"] = _methodsUnmatched
                },
                ["fields"] = new JsonObject
                {
                    ["matched"] = _fieldsMatched, ["matched_variable"] = _fieldsMatchedVariable, ["created"] = _fieldsCreated,
                    ["created_without_cbm_counterpart"] = _fieldsWithoutCounterpart, ["unmatched"] = _fieldsUnmatched,
                    ["unmatched_by_reason"] = ToObject(stats.FieldsUnmatchedByReason), ["cbm_fields_unmatched"] = _cbmFieldsUnmatched
                },
                ["refs"] = RefsDetails(summary.Refs, stats),
                ["edges"] = new JsonObject
                {
                    ["CALLS"] = _callEdges, ["INHERITS"] = _inheritsEdges, ["IMPLEMENTS"] = _implementsEdges, ["DEFINES_METHOD"] = _definesEdges,
                    ["DEFINES"] = _definesFieldEdges, [UsesTypeEdge] = _usesTypeEdges, [ReadsEdge] = _readsEdges, [WritesEdge] = _writesEdges,
                    [OverridesEdge] = _overridesEdges, ["IMPLEMENTS_INHERITED"] = _inheritedImplementsEdges
                },
                ["findings"] = new JsonObject
                {
                    ["methods_with_metrics"] = _methodsWithMetrics,
                    ["nodes_with_issues"] = _nodesWithIssues,
                    ["issues_by_rule"] = ToObject(_issuesByRule),
                    ["issues_unmatched"] = _issuesUnmatched,
                    ["todo_markers"] = _todoMarkers,
                    ["commented_code_blocks"] = _commentedCode,
                    ["hardcoded_secrets"] = _hardcodedSecrets,
                    ["reflection_targets"] = _reflectionTargets,
                    ["files_with_encoding"] = _filesWithEncoding,
                    ["twin_docstrings"] = _twinDocstrings
                },
                ["cbm_edges_replaced"] = ToObject(cbmEdgesReplacedByType),
                // H-9: la deriva de los QN de cbm se ve aquí y en el aviso del paso, no solo en los recuentos. Lo que no casa
                // se separa por causa: ficheros que cbm no indexó (sin ningún nodo suyo) y nombres que no casan en los que sí.
                ["cbm_match"] = new JsonObject
                {
                    ["types_unmatched_ratio"] = Math.Round(stats.TypesUnmatchedRatio, 4),
                    ["methods_unmatched_ratio"] = Math.Round(stats.MethodsUnmatchedRatio, 4),
                    ["unindexed_files"] = stats.UnindexedFiles,
                    ["unindexed_files_long_path"] = stats.UnindexedLongPathFiles,
                    ["types_created_unindexed_file"] = stats.TypesCreatedInUnindexedFiles,
                    ["methods_created_unindexed_file"] = stats.MethodsCreatedInUnindexedFiles,
                    ["types_created_name_mismatch"] = stats.TypesCreatedNameMismatch,
                    ["methods_created_name_mismatch"] = stats.MethodsCreatedNameMismatch,
                    ["types_name_mismatch_ratio"] = Math.Round(stats.TypesNameMismatchRatio, 4),
                    ["methods_name_mismatch_ratio"] = Math.Round(stats.MethodsNameMismatchRatio, 4),
                    ["fields_unmatched_ratio"] = Math.Round(stats.FieldsUnmatchedRatio, 4),
                    ["fields_created_unindexed_file"] = stats.FieldsCreatedInUnindexedFiles,
                    ["fields_created_name_mismatch"] = stats.FieldsCreatedNameMismatch,
                    ["fields_name_mismatch_ratio"] = Math.Round(stats.FieldsNameMismatchRatio, 4),
                    ["threshold"] = CbmDriftThreshold,
                    ["drift_suspected"] = stats.SuspectsCbmDrift
                },
                ["previous_output_removed"] = new JsonObject { ["nodes"] = previousNodes, ["edges"] = previousEdges },
                ["timings_ms"] = new JsonObject { ["parse"] = summary.ParseMs, ["analyze"] = summary.AnalyzeMs, ["scan"] = summary.ScanMs },
                ["write_timings_ms"] = ToObject(timings.ToList()),
                ["heap_peak_mb"] = summary.HeapPeakMb,
                ["batches"] = summary.Batches,
                ["wall_ms"] = summary.WallMs
            };

            var outcome = new EnrichmentOutcome
            {
                FilesAnalyzed = analyzed.Count,
                FilesUnmatched = unindexed.Count,
                NodesCreated = _nodesCreated,
                NodesUpdated = _nodesUpdated,
                NodesRemoved = testNodes,
                CbmEdgesReplaced = cbmEdgesReplaced,
                EdgesCreated = _edgesCreated,
                Unresolved = ToObject(unresolved).ToJsonString(),
                Diagnostics = diagnostics.ToJsonString(),
                Details = details.ToJsonString()
            };
            return new JavaGraphWriteResult(outcome, summary, stats);
        }

        // ------------------------------------------------------------ carga

        private async Task LoadNodesAsync(HashSet<string> files, CancellationToken ct)
        {
            await ExecuteAsync("CREATE TEMP TABLE IF NOT EXISTS enrichment_files(path TEXT PRIMARY KEY)", ct);
            await ExecuteAsync("DELETE FROM temp.enrichment_files", ct);
            using (var insert = Command("INSERT OR IGNORE INTO temp.enrichment_files(path) VALUES (@path)"))
            {
                var path = insert.Parameters.Add("@path", SqliteType.Text);
                foreach (var file in files)
                {
                    path.Value = file;
                    await insert.ExecuteNonQueryAsync(ct);
                }
            }

            // Lo que esta pasada escribe y quedó de otra anterior sobre los mismos nodos se retira antes de leerlos: el parche solo suma.
            using (var reset = Command($"""
                UPDATE nodes SET properties = json_remove(properties, {string.Join(", ", OwnedKeys.Select(k => $"'$.{k}'"))})
                WHERE project = @project AND json_valid(properties)
                  AND file_path IN (SELECT path FROM temp.enrichment_files)
                  AND ({string.Join(" OR ", OwnedKeys.Select(k => $"json_type(properties, '$.{k}') IS NOT NULL"))})
                """))
            {
                reset.Parameters.AddWithValue("@project", project);
                await reset.ExecuteNonQueryAsync(ct);
            }

            using var select = Command("""
                SELECT n.id, n.label, n.name, n.qualified_name, n.file_path, COALESCE(n.start_line, 0), COALESCE(n.end_line, 0), n.properties
                FROM nodes n JOIN temp.enrichment_files f ON n.file_path = f.path
                WHERE n.project = @project
                """);
            select.Parameters.AddWithValue("@project", project);
            using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var node = new GraphNode(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetInt32(5), reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7));
                _byQn[node.QualifiedName] = node;
            }
        }

        /// <summary>Borra las aristas de esos tipos que salen de los ficheros analizados y devuelve cuántas por tipo.</summary>
        private async Task<SortedDictionary<string, long>> DeleteCbmEdgesAsync(IReadOnlyList<string> types, CancellationToken ct)
        {
            using var delete = Command($"""
                DELETE FROM edges
                WHERE type IN ({string.Join(", ", types.Select(t => $"'{t}'"))})
                  AND source_id IN (SELECT n.id FROM nodes n JOIN temp.enrichment_files f ON n.file_path = f.path WHERE n.project = @project)
                RETURNING type
                """);
            delete.Parameters.AddWithValue("@project", project);
            var byType = new SortedDictionary<string, long>(StringComparer.Ordinal);
            using var reader = await delete.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var type = reader.GetString(0);
                byType[type] = byType.GetValueOrDefault(type) + 1;
            }
            return byType;
        }

        // ------------------------------------------------------------ tipos (P7, P9, P11)

        private async Task ApplyTypesAsync(List<JavaTypeRecord> types, CancellationToken ct)
        {
            foreach (var type in types.OrderBy(t => t.Path, StringComparer.Ordinal).ThenBy(t => t.Line))
            {
                var chain = type.Chain ?? [];
                if (chain.Count == 0 || string.IsNullOrEmpty(type.Fqn))
                {
                    _typesUnmatched++;
                    continue;
                }

                var prefix = PathPrefix(type.Path);
                var qn = $"{prefix}.{string.Join('.', chain)}";
                var properties = TypeProperties(type);

                if (_byQn.TryGetValue(qn, out var node))
                {
                    // cbm deja con label Function el nodo de algunos enums: su constructor, emitido como Function en
                    // <prefijo>.<nombre>, ocupa el QN del tipo. Se casa igual, se conserva el label y solo se parchea.
                    if (!TypeLabels.Contains(node.Label) && node.Label != "Function")
                    {
                        _typesUnmatched++;
                        continue;
                    }
                    Patch(node, properties);
                    _typesMatched++;
                    _typeNodeIds.Add(node.Id);
                    _typesByFqn[type.Fqn] = new MappedType(node.Id, qn, type.Kind, prefix);
                    continue;
                }

                var id = await CreateNodeAsync(LabelOf(type.Kind), chain[^1], qn, type.Path, type.Line, type.End, properties, ct);
                _typesCreated++;
                if (!IndexedByCbm(type.Path))
                    _typesCreatedUnindexed++;
                _typeNodeIds.Add(id);
                _typesByFqn[type.Fqn] = new MappedType(id, qn, type.Kind, prefix);
            }
        }

        private static string LabelOf(string kind) => kind switch
        {
            "interface" or "annotation" => "Interface",
            "enum" => "Enum",
            _ => "Class"
        };

        private JsonObject TypeProperties(JavaTypeRecord type)
        {
            var properties = new JsonObject
            {
                ["fqn"] = type.Fqn,
                ["kind"] = type.Kind,
                ["accessibility"] = type.Access,
                ["is_abstract"] = type.Abstract,
                ["type_role"] = TypeRole(type),
                ["is_public"] = type.Access == "public",
                ["annotations"] = ToArray(type.Annotations ?? []),
                ["docstring"] = type.Doc,
                ["super_fqn"] = type.Super?.Fqn,
                ["interfaces_fqn"] = ToArray((type.Interfaces ?? []).Select(i => i.Fqn))
            };
            if (_schema >= 3)
            {
                properties["docstring_source"] = type.DocSource;
                properties["outer_fqn"] = type.Outer;
            }
            return properties;
        }

        // ------------------------------------------------------------ métodos (P7, P8, P9, P11)

        private async Task ApplyMethodsAsync(List<JavaMethodRecord> methods, CancellationToken ct)
        {
            var groups = methods
                .Where(m => !string.IsNullOrEmpty(m.Key))
                .GroupBy(m => (m.OwnerFqn, Name: CbmName(m)))
                .OrderBy(g => g.Key.OwnerFqn, StringComparer.Ordinal).ThenBy(g => g.Key.Name, StringComparer.Ordinal);

            foreach (var group in groups)
            {
                var declarations = group.OrderBy(m => m.Line).ThenBy(m => m.Key, StringComparer.Ordinal).ToList();
                if (!_typesByFqn.TryGetValue(group.Key.OwnerFqn, out var owner))
                {
                    _methodsUnmatched += declarations.Count;
                    continue;
                }

                var plainQn = $"{owner.QualifiedName}.{group.Key.Name}";
                var anchor = FindAnchor(owner, plainQn, group.Key.Name);
                var plainTaken = _byQn.ContainsKey(plainQn);

                JavaMethodRecord? kept;
                if (anchor != null)
                    kept = declarations.FirstOrDefault(d => d.Line <= anchor.StartLine && anchor.StartLine <= Math.Max(d.End, d.Line))
                        ?? declarations.MinBy(d => Math.Abs(d.Line - anchor.StartLine));
                else
                    kept = plainTaken ? null : declarations[0];

                if (kept == null)
                {
                    // P9: el QN de la declaración está ocupado por otro label; no hay dónde anclarla.
                    _methodsUnmatched += declarations.Count;
                    continue;
                }

                if (anchor != null)
                {
                    Patch(anchor, MethodProperties(kept, owner, overloadOf: null, created: false), kept.Line, kept.End);
                    _methodsMatched++;
                    _methodsByKey[kept.Key] = anchor.Id;
                    if (anchor.Label == "Method")
                        PatchTwinDocstring(owner, group.Key.Name, declarations);
                }
                else
                {
                    var id = await CreateNodeAsync("Method", group.Key.Name, plainQn, kept.Path, kept.Line, kept.End,
                        MethodProperties(kept, owner, overloadOf: null, created: true), ct);
                    await DefinesMethodAsync(owner.Id, id, ct);
                    _methodsCreated++;
                    if (!IndexedByCbm(kept.Path))
                        _methodsCreatedUnindexed++;
                    _methodsByKey[kept.Key] = id;
                }

                var others = declarations.Where(d => !ReferenceEquals(d, kept)).ToList();
                if (others.Count == 0)
                    continue;

                // P8: las demás sobrecargas, con los tipos simples borrados; si dos colisionan, con los completos.
                var overloadOf = anchor?.QualifiedName ?? plainQn;
                var simple = others.ToLookup(d => $"{plainQn}({string.Join(',', d.ParamSimple ?? [])})", StringComparer.Ordinal);
                foreach (var declaration in others)
                {
                    var qn = $"{plainQn}({string.Join(',', declaration.ParamSimple ?? [])})";
                    if (simple[qn].Count() > 1)
                        qn = $"{plainQn}({string.Join(',', declaration.Params ?? [])})";

                    if (_byQn.ContainsKey(qn))
                    {
                        _methodsUnmatched++;
                        continue;
                    }

                    var id = await CreateNodeAsync("Method", group.Key.Name, qn, declaration.Path, declaration.Line, declaration.End,
                        MethodProperties(declaration, owner, overloadOf, created: true), ct);
                    await DefinesMethodAsync(owner.Id, id, ct);
                    _methodsCreated++;
                    _overloadsCreated++;
                    _methodsByKey[declaration.Key] = id;
                }
            }
        }

        /// <summary>
        /// cbm guarda cada método de interfaz dos veces: un <c>Method</c> en <c>&lt;QN del tipo&gt;.&lt;nombre&gt;</c> y un <c>Function</c>
        /// gemelo en <c>&lt;prefijo&gt;.&lt;nombre&gt;</c>, los dos con el mismo <c>docstring</c> (con U+FFFD si el comentario no era
        /// UTF-8). El gemelo no es el ancla del grupo, pero lleva el mismo texto mal leído: recibe el <c>docstring</c> del analizador
        /// (el de la declaración que cae en su línea) y nada más, y no cuenta como nodo actualizado.
        /// </summary>
        private void PatchTwinDocstring(MappedType owner, string name, List<JavaMethodRecord> declarations)
        {
            if (!_byQn.TryGetValue($"{owner.PathPrefix}.{name}", out var twin) || twin.Label != "Function" || _typeNodeIds.Contains(twin.Id))
                return;
            var declaration = declarations.FirstOrDefault(d => d.Line <= twin.StartLine && twin.StartLine <= Math.Max(d.End, d.Line))
                ?? declarations.MinBy(d => Math.Abs(d.Line - twin.StartLine));
            if (declaration == null)
                return;
            var patch = new JsonObject { ["docstring"] = declaration.Doc };
            if (_schema >= 3)
                patch["docstring_source"] = declaration.DocSource;
            Patch(twin, patch);
            _twinNodes.Add(twin.Id);
            _twinDocstrings++;
        }

        /// <summary>
        /// Nodo de cbm de un grupo de declaraciones: el <c>Method</c> por QN y, si no lo hay, el <c>Function</c> que cbm crea
        /// en <c>&lt;prefijo&gt;.&lt;nombre&gt;</c> para los métodos de interfaces y enums (P7), que hace de Method del grupo.
        /// Un Function que ya es el nodo de un tipo (el enum cuyo constructor pisó su QN) no sirve de ancla.
        /// </summary>
        private GraphNode? FindAnchor(MappedType owner, string plainQn, string name)
        {
            if (_byQn.TryGetValue(plainQn, out var method) && method.Label == "Method")
                return method;
            if (_byQn.TryGetValue($"{owner.PathPrefix}.{name}", out var function) && function.Label == "Function"
                && !_typeNodeIds.Contains(function.Id))
                return function;
            return null;
        }

        private bool IndexedByCbm(string path) => _filesWithCbmNodes.Contains(path.Replace('\\', '/'));

        /// <summary>Longitud de la ruta absoluta de un fichero del repositorio; 0 si la ruta no es válida.</summary>
        private static int AbsoluteLength(string repositoryPath, string relative)
        {
            try
            {
                return Path.GetFullPath(Path.Combine(repositoryPath, relative.Replace('/', Path.DirectorySeparatorChar))).Length;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return 0;
            }
        }

        /// <summary>Nombre del nodo en cbm: un constructor es un método con el nombre de la clase.</summary>
        private static string CbmName(JavaMethodRecord method) =>
            method.Ctor && method.Chain is { Count: > 0 } chain ? chain[^1] : method.Name;

        private JsonObject MethodProperties(JavaMethodRecord method, MappedType owner, string? overloadOf, bool created)
        {
            var parameters = method.ParamSimple ?? [];
            var names = method.ParamNames ?? [];
            var arguments = string.Join(", ", parameters.Select((type, i) => i < names.Count ? $"{type} {names[i]}" : type));
            var name = CbmName(method);
            var signature = method.Ctor || string.IsNullOrEmpty(method.RetSimple)
                ? $"{name}({arguments})"
                : $"{method.RetSimple} {name}({arguments})";

            var properties = new JsonObject
            {
                ["signature"] = signature,
                ["param_types"] = ToArray(method.Params ?? []),
                ["return_type_fqn"] = method.Ctor ? null : method.Ret,
                ["accessibility"] = method.Access,
                ["is_static"] = method.Static,
                ["is_abstract"] = method.Abstract,
                ["is_constructor"] = method.Ctor,
                ["annotations"] = ToArray(method.Annotations ?? []),
                ["docstring"] = method.Doc,
                ["cyclomatic"] = method.Cyclomatic
            };
            if (_schema >= 3)
            {
                properties["docstring_source"] = method.DocSource;
                properties["is_override"] = method.IsOverride;
                properties["owner_fqn"] = method.OwnerFqn;
            }
            if (created)
            {
                properties["parent_class"] = owner.QualifiedName;
                properties["return_type"] = method.Ctor ? null : method.RetSimple;
                properties["overload_of"] = overloadOf;
            }
            return properties;
        }

        // ------------------------------------------------------------ campos (Q5)

        private async Task ApplyFieldsAsync(List<JavaFieldRecord> fields, CancellationToken ct)
        {
            if (fields.Count == 0)
                return;

            // cbm guarda cada campo de clase como Field en <QN del tipo>.<name>, con el inicializador dentro del name
            // ("nombreConexion = null", "PROPUESTA[]={…}", "envioAmiga\t\t= true"): se indexan por tipo y nombre del campo. Los
            // de interfaces y enums no tienen Field.
            var cbmFields = new Dictionary<(string Owner, string Name), GraphNode>();
            foreach (var node in _byQn.Values)
            {
                if (node.Label != "Field" || node.Name.Length == 0 || !node.QualifiedName.EndsWith("." + node.Name, StringComparison.Ordinal))
                    continue;
                var owner = node.QualifiedName[..^(node.Name.Length + 1)];
                cbmFields.TryAdd((owner, CbmFieldName(node.Name)), node);
            }

            var matched = new HashSet<long>();
            foreach (var field in fields.OrderBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Name, StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(field.Name) || !_typesByFqn.TryGetValue(field.OwnerFqn, out var owner))
                {
                    UnmatchedField(ReasonOwnerWithoutNode);
                    continue;
                }

                if (cbmFields.TryGetValue((owner.QualifiedName, field.Name), out var node) && matched.Add(node.Id))
                {
                    Patch(node, FieldProperties(field, owner, created: false));
                    _fieldsMatched++;
                    _fieldsByKey[field.Key] = node.Id;
                    continue;
                }

                // cbm no crea Field para las constantes de enum: deja un Variable en <QN del tipo>.<constante>, que hace de
                // Field (como el Function de P7 hace de Method). No es el Variable gemelo, que va en <prefijo>.<nombre>.
                var qn = $"{owner.QualifiedName}.{field.Name}";
                if (_byQn.TryGetValue(qn, out var variable) && variable.Label == "Variable" && matched.Add(variable.Id))
                {
                    Patch(variable, FieldProperties(field, owner, created: false));
                    _fieldsMatched++;
                    _fieldsMatchedVariable++;
                    _fieldsByKey[field.Key] = variable.Id;
                    continue;
                }

                // P9: sin nodo de cbm, se crea en <QN del tipo>.<campo> si ese QN está libre.
                if (_byQn.ContainsKey(qn))
                {
                    UnmatchedField(ReasonQualifiedNameTaken);
                    continue;
                }
                var id = await CreateNodeAsync("Field", field.Name, qn, field.Path, field.Line, field.End,
                    FieldProperties(field, owner, created: true), ct);
                if (await InsertEdgeAsync(owner.Id, id, "DEFINES", null, ct))
                    _definesFieldEdges++;
                _fieldsCreated++;
                if (!IndexedByCbm(field.Path))
                    _fieldsCreatedUnindexed++;
                else if (owner.Kind != "class")
                    _fieldsWithoutCounterpart++;
                _fieldsByKey[field.Key] = id;
            }
            _cbmFieldsUnmatched = cbmFields.Count - (matched.Count - _fieldsMatchedVariable);
        }

        /// <summary>
        /// Nombre del campo en el <c>name</c> de un <c>Field</c> de cbm: hasta el primer blanco, <c>=</c> o <c>[</c>
        /// (<c>PROPUESTA[]={"1003"}</c> → <c>PROPUESTA</c>).
        /// </summary>
        private static string CbmFieldName(string name)
        {
            for (var i = 0; i < name.Length; i++)
            {
                if (char.IsWhiteSpace(name[i]) || name[i] is '=' or '[')
                    return name[..i];
            }
            return name;
        }

        private void UnmatchedField(string reason)
        {
            _fieldsUnmatched++;
            _unmatchedFields[reason] = _unmatchedFields.GetValueOrDefault(reason) + 1;
        }

        private JsonObject FieldProperties(JavaFieldRecord field, MappedType owner, bool created)
        {
            var properties = new JsonObject
            {
                ["field_name"] = field.Name,
                ["type_fqn"] = field.TypeFqn,
                ["accessibility"] = field.Access,
                ["is_static"] = field.Static,
                ["is_final"] = field.Final,
                ["docstring"] = field.Doc
            };
            if (_schema >= 3)
            {
                properties["docstring_source"] = field.DocSource;
                properties["owner_fqn"] = field.OwnerFqn;
            }
            if (created)
                properties["parent_class"] = owner.QualifiedName;
            return properties;
        }

        // ------------------------------------------------------------ aristas (P10)

        private async Task WriteHierarchyAsync(List<JavaTypeRecord> types, CancellationToken ct)
        {
            var byFqn = new Dictionary<string, JavaTypeRecord>(StringComparer.Ordinal);
            foreach (var record in types.Where(t => !string.IsNullOrEmpty(t.Fqn)))
                byFqn.TryAdd(record.Fqn, record);

            foreach (var type in types)
            {
                if (string.IsNullOrEmpty(type.Fqn) || !_typesByFqn.TryGetValue(type.Fqn, out var source))
                    continue;

                var isInterface = type.Kind is "interface" or "annotation";
                if (!isInterface && ProjectTarget(type.Super) is { } superclass
                    && await InsertEdgeAsync(source.Id, superclass, "INHERITS", null, ct))
                    _inheritsEdges++;

                foreach (var implemented in type.Interfaces ?? [])
                {
                    if (ProjectTarget(implemented) is not { } target)
                        continue;
                    var edgeType = isInterface ? "INHERITS" : "IMPLEMENTS";
                    if (!await InsertEdgeAsync(source.Id, target, edgeType, null, ct))
                        continue;
                    if (isInterface)
                        _inheritsEdges++;
                    else
                        _implementsEdges++;
                }

                if (!isInterface)
                    await WriteInheritedImplementsAsync(type, source.Id, byFqn, ct);
            }
        }

        /// <summary>
        /// <c>IMPLEMENTS</c> con <c>is_inherited</c> a las interfaces del proyecto que una clase recibe de su superclase o de las
        /// que extiende una interfaz (las directas ya tienen su arista), como hace <c>SemanticMethodEnricher</c> en C#.
        /// </summary>
        private async Task WriteInheritedImplementsAsync(JavaTypeRecord type, long sourceId, Dictionary<string, JavaTypeRecord> byFqn, CancellationToken ct)
        {
            var direct = (type.Interfaces ?? []).Where(i => i.Origin == JavaOrigins.Project).Select(i => i.Fqn).ToHashSet(StringComparer.Ordinal);
            var inherited = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal) { type.Fqn };
            var pending = new Stack<JavaTypeRecord>();
            pending.Push(type);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                var parents = new List<JavaTypeReference>(current.Interfaces ?? []);
                if (current.Super != null)
                    parents.Add(current.Super);
                foreach (var parent in parents)
                {
                    if (parent.Origin != JavaOrigins.Project || !byFqn.TryGetValue(parent.Fqn, out var parentType))
                        continue;
                    if (parentType.Kind is "interface" or "annotation" && !direct.Contains(parent.Fqn))
                        inherited.Add(parent.Fqn);
                    if (visited.Add(parent.Fqn))
                        pending.Push(parentType);
                }
            }

            foreach (var fqn in inherited.Order(StringComparer.Ordinal))
            {
                if (!_typesByFqn.TryGetValue(fqn, out var target))
                    continue;
                var properties = new JsonObject { ["is_inherited"] = true };
                if (await InsertEdgeAsync(sourceId, target.Id, "IMPLEMENTS", properties, ct))
                    _inheritedImplementsEdges++;
            }
        }

        /// <summary>Solo los destinos del proyecto generan arista; los del JDK o de jars quedan en las propiedades del tipo.</summary>
        private long? ProjectTarget(JavaTypeReference? reference) =>
            reference is { Origin: JavaOrigins.Project } && _typesByFqn.TryGetValue(reference.Fqn, out var target) ? target.Id : null;

        private async Task WriteCallsAsync(Dictionary<(string Source, string Target), CallAggregate> calls, CancellationToken ct)
        {
            // Dos claves pueden acabar en el mismo par de nodos (dos grupos anclados en el mismo Function): se funden.
            var edges = new Dictionary<(long, long), CallAggregate>();
            foreach (var ((sourceKey, targetKey), aggregate) in calls)
            {
                long? source = aggregate.Initializer
                    ? (_typesByFqn.TryGetValue(sourceKey["#init ".Length..], out var type) ? type.Id : null)
                    : (_methodsByKey.TryGetValue(sourceKey, out var method) ? method : null);
                if (source is not { } s)
                {
                    Unmatched(ReasonSourceWithoutNode, aggregate.Count);
                    continue;
                }
                if (!_methodsByKey.TryGetValue(targetKey, out var t))
                {
                    // Destinos en clases anónimas o locales (nombre binario, a.B$1Local#go()) no tienen nodo.
                    Unmatched(IsImplicitConstructor(targetKey) ? ReasonImplicitConstructor : ReasonTargetWithoutNode, aggregate.Count);
                    continue;
                }

                if (!edges.TryGetValue((s, t), out var merged))
                    edges[(s, t)] = merged = new CallAggregate { Initializer = aggregate.Initializer };
                foreach (var line in aggregate.Lines)
                    merged.Lines.Add(line);
                foreach (var kind in aggregate.Kinds)
                    merged.Kinds.Add(kind);
                merged.Count += aggregate.Count;
            }

            foreach (var ((source, target), aggregate) in edges.OrderBy(e => e.Key.Item1).ThenBy(e => e.Key.Item2))
            {
                var properties = new JsonObject
                {
                    ["lines"] = new JsonArray(aggregate.Lines.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
                    ["count"] = aggregate.Count,
                    ["kinds"] = ToArray(aggregate.Kinds)
                };
                if (aggregate.Initializer)
                    properties["context"] = "initializer";
                if (await InsertEdgeAsync(source, target, "CALLS", properties, ct))
                    _callEdges++;
                _callPairs.Add(new WrittenEdge(source, target));
            }
        }

        private void Unmatched(string reason, int count)
        {
            _projectCallsUnmatched += count;
            _unmatchedCalls[reason] = _unmatchedCalls.GetValueOrDefault(reason) + count;
        }

        /// <summary>
        /// Q6: <c>USES_TYPE</c> a los tipos y <c>READS</c>/<c>WRITES</c> a los campos, una arista por (origen, destino, tipo)
        /// con las líneas, el número de referencias y, en <c>USES_TYPE</c>, los contextos unidos. Un acceso <c>readwrite</c>
        /// (<c>op=</c>, <c>++</c>, <c>--</c>) da las dos.
        /// </summary>
        private async Task WriteRefsAsync(Dictionary<RefKey, RefAggregate> refs, CancellationToken ct)
        {
            var edges = new Dictionary<(long Source, long Target, string Type), RefAggregate>();
            foreach (var (key, aggregate) in refs)
            {
                long? source = key.SourceKind switch
                {
                    RefSource.Method => _methodsByKey.TryGetValue(key.Source, out var method) ? method : null,
                    RefSource.Field => _fieldsByKey.TryGetValue(key.Source, out var field) ? field : null,
                    _ => _typesByFqn.TryGetValue(key.Source, out var type) ? type.Id : null
                };
                if (source is not { } s)
                {
                    UnmatchedRef(ReasonSourceWithoutNode, aggregate.Count);
                    continue;
                }

                var isField = key.Kind == JavaRefKinds.Field;
                long? target = isField
                    ? (_fieldsByKey.TryGetValue(key.Target, out var targetField) ? targetField : null)
                    : (_typesByFqn.TryGetValue(key.Target, out var targetType) ? targetType.Id : null);
                if (target is not { } t)
                {
                    // Destinos declarados en clases anónimas o locales (nombre binario, a.B$1Loc, a.B$1#campo) no tienen nodo.
                    UnmatchedRef(ReasonTargetWithoutNode, aggregate.Count);
                    continue;
                }

                foreach (var edgeType in EdgeTypesOf(isField, key.Access))
                {
                    if (!edges.TryGetValue((s, t, edgeType), out var merged))
                        edges[(s, t, edgeType)] = merged = new RefAggregate();
                    merged.Add(aggregate.Lines, aggregate.Contexts, aggregate.Count);
                }
            }

            var contextsJson = new Dictionary<string, string>(StringComparer.Ordinal);
            var json = new System.Text.StringBuilder();
            foreach (var ((source, target, type), aggregate) in edges
                         .OrderBy(e => e.Key.Source).ThenBy(e => e.Key.Target).ThenBy(e => e.Key.Type, StringComparer.Ordinal))
            {
                // Cientos de miles de aristas: el JSON se escribe a mano, con el mismo orden de claves que EdgeProperties.
                json.Clear().Append(_refEdgePrefix ??= EnrichmentMarks.EdgeProperties(EnricherName).ToJsonString()[..^1]).Append(",\"lines\":[");
                var first = true;
                foreach (var line in aggregate.Lines)
                {
                    if (!first)
                        json.Append(',');
                    json.Append(line.ToString(CultureInfo.InvariantCulture));
                    first = false;
                }
                json.Append("],\"count\":").Append(aggregate.Count.ToString(CultureInfo.InvariantCulture));
                if (type == UsesTypeEdge)
                {
                    var contexts = string.Join(',', aggregate.Contexts);
                    if (!contextsJson.TryGetValue(contexts, out var encoded))
                        contextsJson[contexts] = encoded = ToArray(aggregate.Contexts).ToJsonString();
                    json.Append(",\"contexts\":").Append(encoded);
                }
                json.Append('}');

                if (!await InsertEdgeJsonAsync(source, target, type, json.ToString(), ct))
                    continue;
                _referencePairs.Add(new WrittenEdge(source, target));
                switch (type)
                {
                    case UsesTypeEdge:
                        _usesTypeEdges++;
                        break;
                    case ReadsEdge:
                        _readsEdges++;
                        break;
                    default:
                        _writesEdges++;
                        break;
                }
            }
        }

        private string? _refEdgePrefix;

        private static readonly string[] UsesType = [UsesTypeEdge], Reads = [ReadsEdge], Writes = [WritesEdge], ReadsAndWrites = [ReadsEdge, WritesEdge];

        private static string[] EdgeTypesOf(bool isField, string? access) =>
            !isField ? UsesType
            : access == JavaRefKinds.Write ? Writes
            : access == JavaRefKinds.ReadWrite ? ReadsAndWrites
            : Reads;

        private void UnmatchedRef(string reason, int count)
        {
            _projectRefsUnmatched += count;
            _unmatchedRefs[reason] = _unmatchedRefs.GetValueOrDefault(reason) + count;
        }

        /// <summary>
        /// Constructor que genera <c>javac</c> (sin declaración, así que sin registro <c>method</c> ni nodo) de un tipo del
        /// proyecto: <c>a.B#B()</c> con <c>a.B</c> casado. No genera arista; se cuenta aparte.
        /// </summary>
        private bool IsImplicitConstructor(string key)
        {
            var hash = key.IndexOf('#');
            if (hash <= 0)
                return false;
            var paren = key.IndexOf('(', hash);
            if (paren < 0)
                return false;
            var ownerFqn = key[..hash];
            var name = key[(hash + 1)..paren];
            var simpleName = ownerFqn[(ownerFqn.LastIndexOf('.') + 1)..];
            return (name == simpleName || name == "<init>") && _typesByFqn.ContainsKey(ownerFqn);
        }

        private async Task DefinesMethodAsync(long typeId, long methodId, CancellationToken ct)
        {
            if (await InsertEdgeAsync(typeId, methodId, "DEFINES_METHOD", null, ct))
                _definesEdges++;
        }

        // ------------------------------------------------------------ OVERRIDES, métricas, hallazgos y comentarios (schema 3)

        /// <summary><c>OVERRIDES</c> del método al que sobrescribe o implementa, cuando los dos tienen nodo.</summary>
        private async Task WriteOverridesAsync(List<JavaMethodRecord> methods, CancellationToken ct)
        {
            foreach (var method in methods.OrderBy(m => m.Path, StringComparer.Ordinal).ThenBy(m => m.Line).ThenBy(m => m.Key, StringComparer.Ordinal))
            {
                if (method.Overrides is not { Count: > 0 } overridden || !_methodsByKey.TryGetValue(method.Key, out var source))
                    continue;
                foreach (var key in overridden)
                {
                    if (!_methodsByKey.TryGetValue(key, out var target) || target == source)
                        continue;
                    if (!_overriddenBy.TryGetValue(source, out var list))
                        _overriddenBy[source] = list = [];
                    if (!list.Contains(target))
                        list.Add(target);
                    if (await InsertEdgeAsync(source, target, OverridesEdge, new JsonObject { ["presentational"] = true }, ct))
                        _overridesEdges++;
                }
            }
        }

        private async Task ApplyFindingsAsync(CollectedInput input, CancellationToken ct)
        {
            var nodesById = _byQn.Values.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
            GraphNode? NodeOf(long id) => nodesById.GetValueOrDefault(id);

            ApplyMetrics(input.Methods, NodeOf);

            if (input.Summary.Schema >= 3)
            {
                ApplyIssues(input.Issues, NodeOf);
                ApplyComments(input, NodeOf);
                ApplyConstants(input.Constants, NodeOf);
            }

            await FlushPatchesAsync(ct);
        }

        /// <summary>
        /// <c>fan_in</c>, <c>fan_out</c> y <c>hotspot</c> con la misma semántica que <c>MetricsEnricher</c> en C#: <c>fan_in</c> son los
        /// orígenes distintos que llaman al método o a lo que sobrescribe o implementa (una llamada por la interfaz cuenta para la
        /// implementación), sin contarse a sí mismo; <c>fan_out</c>, los símbolos del proyecto distintos que nombra (métodos llamados,
        /// tipos usados y campos leídos o escritos); <c>hotspot</c> = round(ciclomática · ln(1 + fan_in), 2).
        /// </summary>
        private void ApplyMetrics(List<JavaMethodRecord> methods, Func<long, GraphNode?> nodeOf)
        {
            var callers = new Dictionary<long, HashSet<long>>();
            var targets = new Dictionary<long, HashSet<long>>();
            foreach (var edge in _callPairs)
            {
                if (!callers.TryGetValue(edge.Target, out var into))
                    callers[edge.Target] = into = [];
                into.Add(edge.Source);
                if (!targets.TryGetValue(edge.Source, out var from))
                    targets[edge.Source] = from = [];
                from.Add(edge.Target);
            }
            foreach (var edge in _referencePairs)
            {
                if (!targets.TryGetValue(edge.Source, out var from))
                    targets[edge.Source] = from = [];
                from.Add(edge.Target);
            }

            foreach (var method in methods.OrderBy(m => m.Path, StringComparer.Ordinal).ThenBy(m => m.Line).ThenBy(m => m.Key, StringComparer.Ordinal))
            {
                if (!_methodsByKey.TryGetValue(method.Key, out var id) || nodeOf(id) is not { } node)
                    continue;

                var incoming = new HashSet<long>();
                var seen = new HashSet<long>();
                var pending = new Stack<long>();
                pending.Push(id);
                while (pending.Count > 0)
                {
                    var current = pending.Pop();
                    if (!seen.Add(current))
                        continue;
                    if (callers.TryGetValue(current, out var direct))
                        incoming.UnionWith(direct);
                    if (_overriddenBy.TryGetValue(current, out var bases))
                    {
                        foreach (var b in bases)
                            pending.Push(b);
                    }
                }
                incoming.Remove(id);

                var outgoing = targets.TryGetValue(id, out var named) ? named.Count(t => t != id) : 0;
                var cyclomatic = method.Cyclomatic ?? 0;
                Patch(node, new JsonObject
                {
                    ["fan_in"] = incoming.Count,
                    ["fan_out"] = outgoing,
                    ["hotspot"] = Math.Round(cyclomatic * Math.Log(1 + incoming.Count), 2)
                });
                _methodsWithMetrics++;
            }
        }

        private void ApplyIssues(List<JavaIssueRecord> issues, Func<long, GraphNode?> nodeOf)
        {
            var byNode = new Dictionary<long, List<JavaIssueRecord>>();
            foreach (var issue in issues)
            {
                long? id = issue.From != null
                    ? (_methodsByKey.TryGetValue(issue.From, out var method) ? method : null)
                    : issue.FromField != null
                        ? (_fieldsByKey.TryGetValue(issue.FromField, out var field) ? field : null)
                        : (_typesByFqn.TryGetValue(issue.FromType, out var type) ? type.Id : null);
                if (id is not { } nodeId || nodeOf(nodeId) == null)
                {
                    _issuesUnmatched++;
                    continue;
                }
                if (!byNode.TryGetValue(nodeId, out var list))
                    byNode[nodeId] = list = [];
                list.Add(issue);
            }

            foreach (var (id, list) in byNode.OrderBy(e => e.Key))
            {
                var ordered = list.OrderBy(i => i.Line).ThenBy(i => i.Rule, StringComparer.Ordinal).ThenBy(i => i.Text, StringComparer.Ordinal)
                    .DistinctBy(i => (i.Line, i.Rule, i.Text)).ToList();
                foreach (var issue in ordered)
                    _issuesByRule[issue.Rule] = _issuesByRule.GetValueOrDefault(issue.Rule) + 1;
                Patch(nodeOf(id)!, new JsonObject
                {
                    ["detected_issues"] = ToArray(ordered.Select(i => $"{i.Rule}: {i.Text} (línea {i.Line})")),
                    ["issue_rules"] = ToArray(ordered.Select(i => i.Rule).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                });
                _nodesWithIssues++;
            }
        }

        /// <summary>
        /// Constantes que resolvió el compilador, en el nodo del método, el campo o el tipo que las contiene:
        /// <c>hardcoded_secrets</c> (<c>[{kind, name, line, preview_masked}]</c>, con el valor ya enmascarado por el analizador; se
        /// descartan los identificadores que nombran una referencia a la credencial, como <c>UserSecretsId</c>) y
        /// <c>reflection_targets</c> (<c>string[]</c> con los nombres de clase, con <c>*</c> por cada tramo no constante).
        /// </summary>
        private void ApplyConstants(List<JavaConstantRecord> constants, Func<long, GraphNode?> nodeOf)
        {
            var secrets = new Dictionary<long, List<JavaConstantRecord>>();
            var targets = new Dictionary<long, SortedSet<string>>();
            foreach (var constant in constants)
            {
                var isSecret = constant.What == JavaConstantKinds.Secret;
                if (!isSecret && constant.What != JavaConstantKinds.Reflection)
                    continue;
                // Un identificador que nombra una referencia a la credencial (UserSecretsId, token_url) no guarda el secreto.
                if (isSecret && constant.Kind == "identifier" && constant.Name != null && HardcodedSecretScanner.NamesAReference(constant.Name))
                    continue;
                long? id = constant.From != null
                    ? (_methodsByKey.TryGetValue(constant.From, out var method) ? method : null)
                    : constant.FromField != null
                        ? (_fieldsByKey.TryGetValue(constant.FromField, out var field) ? field : null)
                        : (_typesByFqn.TryGetValue(constant.FromType, out var type) ? type.Id : null);
                if (id is not { } nodeId || nodeOf(nodeId) == null)
                    continue;
                if (isSecret)
                    (secrets.TryGetValue(nodeId, out var list) ? list : secrets[nodeId] = []).Add(constant);
                else
                    (targets.TryGetValue(nodeId, out var set) ? set : targets[nodeId] = new SortedSet<string>(StringComparer.Ordinal)).Add(constant.Value);
            }

            foreach (var id in secrets.Keys.Concat(targets.Keys).Distinct().Order())
            {
                var patch = new JsonObject();
                if (secrets.TryGetValue(id, out var found))
                {
                    var ordered = found.OrderBy(c => c.Line).ThenBy(c => c.Kind, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal)
                        .DistinctBy(c => (c.Line, c.Kind, c.Name, c.Value)).ToList();
                    patch["hardcoded_secrets"] = new JsonArray(ordered.Select(c => (JsonNode?)new JsonObject
                    {
                        ["kind"] = c.Kind, ["name"] = c.Name, ["line"] = c.Line, ["preview_masked"] = c.Value
                    }).ToArray());
                    _hardcodedSecrets += ordered.Count;
                    _nodesWithSecrets++;
                }
                if (targets.TryGetValue(id, out var names))
                {
                    patch["reflection_targets"] = ToArray(names);
                    _reflectionTargets += names.Count;
                }
                Patch(nodeOf(id)!, patch);
            }
        }

        /// <summary>
        /// <c>todo_markers</c> y <c>commented_code</c> en el método, el tipo o el nodo <c>File</c> que dice el analizador, y la
        /// codificación con la que se leyó cada fichero en su nodo <c>File</c>.
        /// </summary>
        private void ApplyComments(CollectedInput input, Func<long, GraphNode?> nodeOf)
        {
            var fileNodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
            foreach (var node in _byQn.Values.Where(n => n.Label == "File"))
                fileNodes.TryAdd(node.FilePath.Replace('\\', '/'), node);

            foreach (var file in input.Files)
            {
                if (string.IsNullOrEmpty(file.Encoding) || !fileNodes.TryGetValue(file.Path.Replace('\\', '/'), out var node))
                    continue;
                // El mismo formato que escribe C# (Encoding.WebName): utf-8, iso-8859-1, windows-1252…
                Patch(node, new JsonObject { ["encoding"] = file.Encoding.ToLowerInvariant() });
                _filesWithEncoding++;
            }

            var todos = new Dictionary<long, List<JavaTodoMarker>>();
            var code = new Dictionary<long, List<JavaCommentedCode>>();
            foreach (var comment in input.Comments)
            {
                long? id = comment.Owner switch
                {
                    JavaCommentOwners.Method => comment.Key != null && _methodsByKey.TryGetValue(comment.Key, out var method) ? method : null,
                    JavaCommentOwners.Type => comment.Key != null && _typesByFqn.TryGetValue(comment.Key, out var type) ? type.Id : null,
                    _ => fileNodes.TryGetValue(comment.Path.Replace('\\', '/'), out var node) ? node.Id : null
                };
                // Un método o tipo sin nodo (p. ej. una sobrecarga sin casar): el hallazgo pasa al nodo del fichero.
                if (id is null && fileNodes.TryGetValue(comment.Path.Replace('\\', '/'), out var fallback))
                    id = fallback.Id;
                if (id is not { } nodeId)
                    continue;
                if (comment.Todos is { Count: > 0 })
                    (todos.TryGetValue(nodeId, out var t) ? t : todos[nodeId] = []).AddRange(comment.Todos);
                if (comment.Code is { Count: > 0 })
                    (code.TryGetValue(nodeId, out var c) ? c : code[nodeId] = []).AddRange(comment.Code);
            }

            foreach (var id in todos.Keys.Concat(code.Keys).Distinct().Order())
            {
                if (nodeOf(id) is not { } node)
                    continue;
                var patch = new JsonObject();
                if (todos.TryGetValue(id, out var markers))
                {
                    markers = [.. markers.OrderBy(m => m.Line).ThenBy(m => m.Kind, StringComparer.Ordinal)];
                    patch["todo_markers"] = new JsonArray(markers.Select(m => (JsonNode?)new JsonObject
                        { ["kind"] = m.Kind, ["line"] = m.Line, ["text"] = m.Text }).ToArray());
                    patch["todo_count"] = markers.Count;
                    _todoMarkers += markers.Count;
                }
                if (code.TryGetValue(id, out var runs))
                {
                    runs = [.. runs.OrderBy(r => r.StartLine)];
                    patch["commented_code"] = new JsonArray(runs.Select(r => (JsonNode?)new JsonObject
                        { ["start_line"] = r.StartLine, ["end_line"] = r.EndLine, ["lines"] = r.Lines, ["preview"] = r.Preview }).ToArray());
                    patch["commented_code_lines"] = runs.Sum(r => r.Lines);
                    _commentedCode += runs.Count;
                }
                Patch(node, patch);
            }
        }

        // ------------------------------------------------------------ SQL

        private void Patch(GraphNode node, JsonObject properties, int? start = null, int? end = null)
        {
            if (!_pendingPatches.TryGetValue(node.Id, out var patch))
                _pendingPatches[node.Id] = patch = [];
            foreach (var (key, value) in properties)
                patch[key] = value?.DeepClone();
            if (start is { } s)
                _pendingLines[node.Id] = (s, Math.Max(end ?? s, s));
        }

        /// <summary>Escribe las propiedades completadas (y las líneas de las sobrecargas conservadas) de los nodos de cbm.</summary>
        private async Task FlushPatchesAsync(CancellationToken ct)
        {
            var nodesById = _byQn.Values.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
            _updateNode ??= Command("""
                UPDATE nodes SET properties = @props,
                    start_line = @start, line_start = @start, end_line = @end, line_end = @end
                WHERE id = @id
                """);
            _updateNode.Parameters.Clear();
            var props = _updateNode.Parameters.Add("@props", SqliteType.Text);
            var startParameter = _updateNode.Parameters.Add("@start", SqliteType.Integer);
            var endParameter = _updateNode.Parameters.Add("@end", SqliteType.Integer);
            var idParameter = _updateNode.Parameters.Add("@id", SqliteType.Integer);

            foreach (var (id, patch) in _pendingPatches)
            {
                var node = nodesById[id];
                var (start, end) = _pendingLines.TryGetValue(id, out var lines) ? lines : (node.StartLine, node.EndLine);

                var merged = EnrichmentMarks.Merge(_written.TryGetValue(id, out var already) ? already : node.Properties, patch, EnricherName);
                foreach (var key in NullReplacesKeys)
                {
                    if (patch.TryGetPropertyValue(key, out var value) && value is null)
                        merged.Remove(key);
                }
                var json = merged.ToJsonString();
                props.Value = json;
                startParameter.Value = start;
                endParameter.Value = end;
                idParameter.Value = id;
                await _updateNode.ExecuteNonQueryAsync(ct);
                _written[id] = json;
                // Un nodo creado por esta pasada que recibe un segundo parche no es un nodo de cbm completado.
                if (!_createdNodes.Contains(id) && !_twinNodes.Contains(id) && _updatedNodes.Add(id))
                    _nodesUpdated++;
            }
            _pendingPatches.Clear();
            _pendingLines.Clear();
        }

        private async Task<long> CreateNodeAsync(string label, string name, string qualifiedName, string filePath, int start, int end,
            JsonObject properties, CancellationToken ct)
        {
            _insertNode ??= Command("""
                INSERT INTO nodes(project, project_id, label, name, qualified_name, file_path, file,
                                  start_line, line_start, end_line, line_end, properties)
                VALUES (@project, 1, @label, @name, @qn, @file, @file, @start, @start, @end, @end, @props);
                SELECT last_insert_rowid();
                """);
            _insertNode.Parameters.Clear();
            _insertNode.Parameters.AddWithValue("@project", project);
            _insertNode.Parameters.AddWithValue("@label", label);
            _insertNode.Parameters.AddWithValue("@name", name);
            _insertNode.Parameters.AddWithValue("@qn", qualifiedName);
            _insertNode.Parameters.AddWithValue("@file", filePath);
            _insertNode.Parameters.AddWithValue("@start", start);
            _insertNode.Parameters.AddWithValue("@end", Math.Max(end, start));
            var createdProperties = EnrichmentMarks.CreatedNodeProperties(EnricherName, enrichedAt, properties).ToJsonString();
            _insertNode.Parameters.AddWithValue("@props", createdProperties);
            var id = Convert.ToInt64(await _insertNode.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);

            _byQn[qualifiedName] = new GraphNode(id, label, name, qualifiedName, filePath, start, end, createdProperties);
            _createdNodes.Add(id);
            _nodesCreated++;
            return id;
        }

        private Task<bool> InsertEdgeAsync(long source, long target, string type, JsonObject? properties, CancellationToken ct) =>
            InsertEdgeJsonAsync(source, target, type, properties == null ? null : EnrichmentMarks.EdgeProperties(EnricherName, properties).ToJsonString(), ct);

        /// <summary>Inserta una arista con sus <c>properties</c> ya serializadas (con la marca); null deja solo la marca.</summary>
        private async Task<bool> InsertEdgeJsonAsync(long source, long target, string type, string? propertiesJson, CancellationToken ct)
        {
            // Medio millón de aristas en un repo grande: el comando y sus parámetros se preparan una vez y se reutilizan.
            if (_insertEdge == null)
            {
                _insertEdge = Command("""
                    INSERT INTO edges(project, project_id, source_id, target_id, type, properties)
                    VALUES (@project, 1, @source, @target, @type, @props)
                    ON CONFLICT DO NOTHING
                    """);
                _insertEdge.Parameters.AddWithValue("@project", project);
                _edgeSource = _insertEdge.Parameters.Add("@source", SqliteType.Integer);
                _edgeTarget = _insertEdge.Parameters.Add("@target", SqliteType.Integer);
                _edgeType = _insertEdge.Parameters.Add("@type", SqliteType.Text);
                _edgeProperties = _insertEdge.Parameters.Add("@props", SqliteType.Text);
                _plainEdgeProperties = EnrichmentMarks.EdgeProperties(EnricherName).ToJsonString();
            }
            _edgeSource!.Value = source;
            _edgeTarget!.Value = target;
            _edgeType!.Value = type;
            _edgeProperties!.Value = propertiesJson ?? _plainEdgeProperties;
            var inserted = await _insertEdge.ExecuteNonQueryAsync(ct) > 0;
            if (inserted)
                _edgesCreated++;
            return inserted;
        }

        public void Dispose()
        {
            _insertEdge?.Dispose();
            _insertNode?.Dispose();
            _updateNode?.Dispose();
        }

        private SqliteCommand Command(string sql)
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            return cmd;
        }

        private async Task ExecuteAsync(string sql, CancellationToken ct)
        {
            using var cmd = Command(sql);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        /// <summary>Prefijo de QN de cbm para un fichero: <c>&lt;proyecto&gt;.&lt;ruta sin extensión con / → .&gt;</c>.</summary>
        private string PathPrefix(string path)
        {
            var normalized = path.Replace('\\', '/');
            var dot = normalized.LastIndexOf('.');
            if (dot > normalized.LastIndexOf('/'))
                normalized = normalized[..dot];
            return $"{project}.{normalized.Replace('/', '.')}";
        }
    }

    /// <summary>
    /// <c>details.refs</c>: los totales del analizador (<c>summary.refs</c>) y las referencias del proyecto que no llegaron a
    /// arista, por motivo. Null con un resultado de schema 1.
    /// </summary>
    private static JsonObject? RefsDetails(JavaRefsSummary? refs, JavaGraphStats stats) => refs == null ? null : new JsonObject
    {
        ["type"] = ToObject(refs.Type ?? new Dictionary<string, long>()),
        ["field"] = ToObject(refs.Field ?? new Dictionary<string, long>()),
        ["contexts"] = ToObject(refs.Contexts ?? new Dictionary<string, long>()),
        ["records"] = refs.Records,
        ["self_excluded"] = refs.SelfExcluded,
        ["unresolved_by_reason"] = ToObject(refs.UnresolvedByReason ?? new Dictionary<string, long>()),
        ["unmatched"] = stats.ProjectRefsUnmatched,
        ["unmatched_by_reason"] = ToObject(stats.ProjectRefsUnmatchedByReason)
    };

    private static JsonArray ToArray(IEnumerable<string> values) =>
        new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static JsonObject ToObject(IEnumerable<KeyValuePair<string, long>> values)
    {
        var result = new JsonObject();
        foreach (var (key, value) in values)
            result[key] = value;
        return result;
    }
}
