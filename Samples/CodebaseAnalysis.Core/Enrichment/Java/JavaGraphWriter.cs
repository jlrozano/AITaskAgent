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
    public int CallEdges { get; init; }
    public int InheritsEdges { get; init; }
    public int ImplementsEdges { get; init; }
    public int DefinesMethodEdges { get; init; }

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
    /// <c>remove_tests</c>, <c>load_nodes</c>, <c>replace_cbm_edges</c>, <c>types</c>, <c>methods</c>, <c>patches</c>,
    /// <c>hierarchy</c>, <c>calls</c>, <c>run_row</c> y <c>commit</c>). Las de <c>details.write_timings_ms</c> son las mismas
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

    /// <summary>
    /// Más de <see cref="JavaGraphWriter.CbmDriftThreshold"/> de tipos o de métodos sin casar: lo normal es que casen casi
    /// todos (D3 replica las rarezas de cbm), así que suele ser que una versión nueva de cbm deriva los QN de otra forma y la
    /// pasada está duplicando sus nodos en lugar de enriquecerlos.
    /// </summary>
    public bool SuspectsCbmDrift =>
        TypesUnmatchedRatio > JavaGraphWriter.CbmDriftThreshold || MethodsUnmatchedRatio > JavaGraphWriter.CbmDriftThreshold;

    private static double Ratio(int part, int total) => total > 0 ? (double)part / total : 0;
}

/// <summary>Resultado de <see cref="JavaGraphWriter.ApplyAsync"/>.</summary>
public sealed record JavaGraphWriteResult(EnrichmentOutcome Outcome, JavaSummaryRecord Summary, JavaGraphStats Stats);

/// <summary>
/// Aplica el resultado del analizador Java sobre la copia del grafo en una transacción (P7-P13): borra la salida anterior,
/// retira el código de test, casa tipos y métodos con los nodos de cbm por QN, crea las sobrecargas y las declaraciones que
/// cbm no tiene, completa propiedades, sustituye <c>CALLS</c>/<c>INHERITS</c>/<c>IMPLEMENTS</c> de los ficheros analizados
/// y deja la fila <c>ok</c> de <c>enrichment_runs</c>. No sabe nada del proceso Java: recibe los registros ya leídos.
/// </summary>
public static class JavaGraphWriter
{
    public const string EnricherName = "JavaAnalyzer";

    /// <summary>Versión de las reglas de escritura; cambiarla invalida las pasadas <c>ok</c> anteriores (P3).</summary>
    public const string EnricherVersion = "1";

    /// <summary>
    /// Proporción de tipos o de métodos sin casar con cbm (creados por P9) por encima de la cual la pasada avisa de una
    /// posible deriva de los QN de cbm (<see cref="JavaGraphStats.SuspectsCbmDrift"/>). Queda en <c>details.cbm_match</c>.
    /// </summary>
    public const double CbmDriftThreshold = 0.05;

    /// <summary>Labels con los que cbm guarda un tipo; <c>Function</c> se acepta aparte (enums, ver <c>ApplyTypesAsync</c>).</summary>
    private static readonly HashSet<string> TypeLabels = new(StringComparer.Ordinal) { "Class", "Interface", "Enum", "Annotation" };

    /// <summary>Motivos de una llamada de proyecto que no llega a arista (claves de <c>unresolved</c>).</summary>
    public const string ReasonImplicitConstructor = "implicit_constructor";

    /// <inheritdoc cref="ReasonImplicitConstructor"/>
    public const string ReasonTargetWithoutNode = "target_without_node";

    /// <inheritdoc cref="ReasonImplicitConstructor"/>
    public const string ReasonSourceWithoutNode = "source_without_node";

    private static readonly string[] ReplacedEdgeTypes = ["CALLS", "INHERITS", "IMPLEMENTS"];

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

    private sealed record CollectedInput(
        List<JavaFileRecord> Files,
        List<JavaTypeRecord> Types,
        List<JavaMethodRecord> Methods,
        Dictionary<(string Source, string Target), CallAggregate> ProjectCalls,
        Dictionary<string, long> CallsByOrigin,
        Dictionary<string, long> UnresolvedByReason,
        int DiagnosticCount,
        HashSet<string> DiagnosticPaths,
        JavaSummaryRecord Summary);

    private static CollectedInput Collect(IEnumerable<JavaAnalysisRecord> records, CancellationToken ct)
    {
        var files = new List<JavaFileRecord>();
        var types = new List<JavaTypeRecord>();
        var methods = new List<JavaMethodRecord>();
        var calls = new Dictionary<(string, string), CallAggregate>();
        var byOrigin = new Dictionary<string, long>(StringComparer.Ordinal);
        var byReason = new Dictionary<string, long>(StringComparer.Ordinal);
        var diagnostics = 0;
        var diagnosticPaths = new HashSet<string>(StringComparer.Ordinal);
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
        if (summary.Schema != JavaAnalysisReader.SupportedSchema)
            throw new InvalidDataException(
                $"El resultado del analizador Java es de la versión de contrato {summary.Schema}; se esperaba {JavaAnalysisReader.SupportedSchema}.");

        return new CollectedInput(files, types, methods, calls, byOrigin, byReason, diagnostics, diagnosticPaths, summary);
    }

    private static void Increment(Dictionary<string, long> counts, string key, long by = 1) =>
        counts[key] = counts.GetValueOrDefault(key) + by;

    // ---------------------------------------------------------------- escritura

    private sealed record GraphNode(long Id, string Label, string QualifiedName, string FilePath, int StartLine, int EndLine, string? Properties);

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
        private SqliteCommand? _insertEdge;
        private SqliteParameter? _edgeSource, _edgeTarget, _edgeType, _edgeProperties;
        private string? _plainEdgeProperties;
        private SqliteCommand? _insertNode;
        private SqliteCommand? _updateNode;

        private int _nodesCreated, _nodesUpdated, _edgesCreated;
        private int _typesMatched, _typesCreated, _typesUnmatched;
        private int _methodsMatched, _methodsCreated, _overloadsCreated, _methodsUnmatched;
        private int _callEdges, _inheritsEdges, _implementsEdges, _definesEdges, _projectCallsUnmatched;

        public async Task<JavaGraphWriteResult> RunAsync(JavaGraphWriteRequest request, CollectedInput input, CancellationToken ct)
        {
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
            timings.Lap("load_nodes");

            // P10: las aristas heurísticas de cbm que salen de los ficheros analizados se sustituyen por las resueltas.
            var cbmEdgesReplaced = await DeleteCbmEdgesAsync(ct);
            timings.Lap("replace_cbm_edges");

            await ApplyTypesAsync(input.Types, ct);
            timings.Lap("types");
            await ApplyMethodsAsync(input.Methods, ct);
            timings.Lap("methods");
            await FlushPatchesAsync(ct);
            timings.Lap("patches");
            await WriteHierarchyAsync(input.Types, ct);
            timings.Lap("hierarchy");
            await WriteCallsAsync(input.ProjectCalls, ct);
            timings.Lap("calls");

            var analyzed = input.Files.Select(f => f.Path).Distinct(StringComparer.Ordinal).ToList();
            var filesWithErrors = input.Files.Count(f => f.Errors > 0);
            var unresolved = new SortedDictionary<string, long>(input.UnresolvedByReason, StringComparer.Ordinal);
            foreach (var (reason, count) in _unmatchedCalls)
                unresolved[reason] = unresolved.GetValueOrDefault(reason) + count;
            var analyzedSet = analyzed.ToHashSet(StringComparer.Ordinal);
            var unreadable = input.DiagnosticPaths.Count(p => !analyzedSet.Contains(p));

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
                CallEdges = _callEdges,
                InheritsEdges = _inheritsEdges,
                ImplementsEdges = _implementsEdges,
                DefinesMethodEdges = _definesEdges,
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
                ["edges"] = new JsonObject
                {
                    ["CALLS"] = _callEdges, ["INHERITS"] = _inheritsEdges, ["IMPLEMENTS"] = _implementsEdges, ["DEFINES_METHOD"] = _definesEdges
                },
                // H-9: la deriva de los QN de cbm se ve aquí y en el aviso del paso, no solo en los recuentos.
                ["cbm_match"] = new JsonObject
                {
                    ["types_unmatched_ratio"] = Math.Round(stats.TypesUnmatchedRatio, 4),
                    ["methods_unmatched_ratio"] = Math.Round(stats.MethodsUnmatchedRatio, 4),
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
                FilesUnmatched = analyzed.Count(f => !filesWithNodes.Contains(f)),
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

            using var select = Command("""
                SELECT n.id, n.label, n.qualified_name, n.file_path, COALESCE(n.start_line, 0), COALESCE(n.end_line, 0), n.properties
                FROM nodes n JOIN temp.enrichment_files f ON n.file_path = f.path
                WHERE n.project = @project
                """);
            select.Parameters.AddWithValue("@project", project);
            using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var node = new GraphNode(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt32(4), reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetString(6));
                _byQn[node.QualifiedName] = node;
            }
        }

        private async Task<int> DeleteCbmEdgesAsync(CancellationToken ct)
        {
            using var delete = Command($"""
                DELETE FROM edges
                WHERE type IN ({string.Join(", ", ReplacedEdgeTypes.Select(t => $"'{t}'"))})
                  AND source_id IN (SELECT n.id FROM nodes n JOIN temp.enrichment_files f ON n.file_path = f.path WHERE n.project = @project)
                """);
            delete.Parameters.AddWithValue("@project", project);
            return await delete.ExecuteNonQueryAsync(ct);
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

        private static JsonObject TypeProperties(JavaTypeRecord type) => new()
        {
            ["fqn"] = type.Fqn,
            ["kind"] = type.Kind,
            ["accessibility"] = type.Access,
            ["is_abstract"] = type.Abstract,
            ["annotations"] = ToArray(type.Annotations ?? []),
            ["docstring"] = type.Doc,
            ["super_fqn"] = type.Super?.Fqn,
            ["interfaces_fqn"] = ToArray((type.Interfaces ?? []).Select(i => i.Fqn))
        };

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
                }
                else
                {
                    var id = await CreateNodeAsync("Method", group.Key.Name, plainQn, kept.Path, kept.Line, kept.End,
                        MethodProperties(kept, owner, overloadOf: null, created: true), ct);
                    await DefinesMethodAsync(owner.Id, id, ct);
                    _methodsCreated++;
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

        /// <summary>Nombre del nodo en cbm: un constructor es un método con el nombre de la clase.</summary>
        private static string CbmName(JavaMethodRecord method) =>
            method.Ctor && method.Chain is { Count: > 0 } chain ? chain[^1] : method.Name;

        private static JsonObject MethodProperties(JavaMethodRecord method, MappedType owner, string? overloadOf, bool created)
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
            if (created)
            {
                properties["parent_class"] = owner.QualifiedName;
                properties["return_type"] = method.Ctor ? null : method.RetSimple;
                properties["overload_of"] = overloadOf;
            }
            return properties;
        }

        // ------------------------------------------------------------ aristas (P10)

        private async Task WriteHierarchyAsync(List<JavaTypeRecord> types, CancellationToken ct)
        {
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
            }
        }

        private void Unmatched(string reason, int count)
        {
            _projectCallsUnmatched += count;
            _unmatchedCalls[reason] = _unmatchedCalls.GetValueOrDefault(reason) + count;
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
            var nodesById = _byQn.Values.ToDictionary(n => n.Id);
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

                props.Value = EnrichmentMarks.Merge(node.Properties, patch, EnricherName).ToJsonString();
                startParameter.Value = start;
                endParameter.Value = end;
                idParameter.Value = id;
                await _updateNode.ExecuteNonQueryAsync(ct);
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
            _insertNode.Parameters.AddWithValue("@props",
                EnrichmentMarks.CreatedNodeProperties(EnricherName, enrichedAt, properties).ToJsonString());
            var id = Convert.ToInt64(await _insertNode.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);

            _byQn[qualifiedName] = new GraphNode(id, label, qualifiedName, filePath, start, end, null);
            _nodesCreated++;
            return id;
        }

        private async Task<bool> InsertEdgeAsync(long source, long target, string type, JsonObject? properties, CancellationToken ct)
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
            _edgeProperties!.Value = properties == null
                ? _plainEdgeProperties
                : EnrichmentMarks.EdgeProperties(EnricherName, properties).ToJsonString();
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
