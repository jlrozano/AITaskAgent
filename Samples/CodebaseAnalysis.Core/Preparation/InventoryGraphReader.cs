using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Enrichment.Java;
using CodebaseAnalysis.Core.Steps;
using Microsoft.Data.Sqlite;

namespace CodebaseAnalysis.Core.Preparation;

/// <summary>Estado de un enriquecedor sobre la indexación actual del grafo.</summary>
/// <param name="Enricher">Nombre del enriquecedor tal como lo registra en <c>enrichment_runs</c>.</param>
/// <param name="Status"><c>ok</c>, <c>failed</c> o <c>skipped</c> (la última pasada sobre esta indexación) o <see cref="InventoryGraphReader.StaleStatus"/>.</param>
/// <param name="RunId">Id de la fila de esa pasada.</param>
/// <param name="Message">Mensaje que dejó la pasada (el motivo del fallo o del salto); vacío si no dejó ninguno.</param>
public sealed record EnricherRunState(string Enricher, string Status, long? RunId, string Message);

/// <summary>Lo que el inventario necesita del grafo, leído de una vez.</summary>
public sealed record InventoryGraphData
{
    /// <summary>Puntos de entrada ordenados por tipo, nombre, fichero y línea, recortados al límite pedido.</summary>
    public IReadOnlyList<InventoryEntryPoint> EntryPoints { get; init; } = [];

    /// <summary>Puntos de entrada que hay en el grafo (<see cref="EntryPoints"/> puede estar recortada).</summary>
    public int EntryPointsTotal { get; init; }

    /// <summary>Tablas de más a menos referenciadas y por nombre, recortadas al límite pedido.</summary>
    public IReadOnlyList<InventoryTable> Tables { get; init; } = [];

    public int TablesTotal { get; init; }

    /// <summary>Un elemento por enriquecedor que dejó alguna fila en <c>enrichment_runs</c>, por nombre. Vacío si la tabla no existe (índice sin enriquecer).</summary>
    public IReadOnlyList<EnricherRunState> EnricherRuns { get; init; } = [];

    /// <summary>La deriva de cbm de la última pasada correcta del analizador Java sobre esta indexación; null si no hay o no la midió.</summary>
    public CbmMatchSummary? CbmMatch { get; init; }

    /// <summary>Rutas (relativas, con <c>/</c>, sin distinguir mayúsculas) de los ficheros que tienen algún nodo en el grafo.</summary>
    public IReadOnlySet<string> GraphFiles { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Huella de la indexación (<c>EnrichmentRuns.IndexFingerprintAsync</c>); null si el grafo no la tiene.</summary>
    public string? IndexFingerprint { get; init; }

    /// <summary>Lecturas que fallaron; la parte afectada queda vacía.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Lee del grafo (en solo lectura) lo que el inventario del repositorio necesita (T5): puntos de entrada, tablas, ficheros con
/// nodos y el estado de los enriquecedores. Toda consulta sobre <c>properties</c> protege el JSON con
/// <see cref="SqlFacetExtractorStep.SafeJson"/>: cbm deja nodos con JSON inválido y <c>json_extract</c> lanza <c>malformed
/// JSON</c> sobre ellos (60 en el índice de <c>Online</c>). Lo que no se puede leer es un aviso y esa parte queda vacía.
/// </summary>
public static class InventoryGraphReader
{
    /// <summary>Estado de un enriquecedor cuyas filas son de otra indexación del grafo: no cuentan para este.</summary>
    public const string StaleStatus = "stale";

    private static readonly string[] AnnotationKeys = ["annotations", "attributes", "decorators"];

    private static readonly HashSet<string> ScheduledAnnotations = new(StringComparer.OrdinalIgnoreCase) { "Scheduled" };

    private static readonly HashSet<string> MessageConsumerAnnotations = new(StringComparer.OrdinalIgnoreCase)
    {
        "MessageDriven", "JmsListener", "KafkaListener", "RabbitListener", "SqsListener", "StreamListener"
    };

    public static async Task<InventoryGraphData> ReadAsync(string databasePath, int maxEntryPoints, int maxTables, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            return new InventoryGraphData { Warnings = [$"⚠️ No existe la base de datos del grafo ('{databasePath}')."] };

        var warnings = new List<string>();
        try
        {
            using var conn = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            await conn.OpenAsync(ct);

            var fingerprint = await Guarded(() => EnrichmentRuns.IndexFingerprintAsync(conn, ct), null, "la huella de la indexación", warnings);
            var (entryPoints, entryTotal) = await Guarded(() => ReadEntryPointsAsync(conn, maxEntryPoints, ct), ([], 0), "los puntos de entrada", warnings);
            var (tables, tablesTotal) = await Guarded(() => ReadTablesAsync(conn, maxTables, ct), ([], 0), "las tablas", warnings);
            var files = await Guarded(() => ReadFilesAsync(conn, ct), new HashSet<string>(StringComparer.OrdinalIgnoreCase), "los ficheros", warnings);
            var (runs, cbmMatch) = await Guarded(() => ReadRunsAsync(conn, fingerprint, ct), ([], null), "las pasadas de enriquecimiento", warnings);

            return new InventoryGraphData
            {
                EntryPoints = entryPoints,
                EntryPointsTotal = entryTotal,
                Tables = tables,
                TablesTotal = tablesTotal,
                EnricherRuns = runs,
                CbmMatch = cbmMatch,
                GraphFiles = files,
                IndexFingerprint = fingerprint,
                Warnings = warnings
            };
        }
        catch (SqliteException ex)
        {
            return new InventoryGraphData { Warnings = [.. warnings, $"⚠️ No se pudo abrir la base de datos del grafo: {ex.Message}"] };
        }
    }

    private static async Task<T> Guarded<T>(Func<Task<T>> read, T fallback, string what, List<string> warnings)
    {
        try
        {
            return await read();
        }
        catch (SqliteException ex)
        {
            warnings.Add($"⚠️ No se pudo leer {what} del grafo: {ex.Message}");
            return fallback;
        }
    }

    // ───────────────────────────── Puntos de entrada ─────────────────────────────

    private sealed record NodeRow(long Id, string Label, string Name, string QualifiedName, string File, int Line, string Properties);

    private static async Task<(IReadOnlyList<InventoryEntryPoint>, int)> ReadEntryPointsAsync(SqliteConnection conn, int limit, CancellationToken ct)
    {
        var entries = new List<InventoryEntryPoint>();
        var seen = new HashSet<long>();

        // Métodos y clases con un rasgo de entrada: se descartan en SQL los que no tienen ninguno y se decide en C# con el
        // JSON ya parseado (un docstring que dice «Scheduled» casa con el LIKE pero no es una anotación).
        var candidates = await ReadNodesAsync(conn, $"""
            SELECT n.id, n.label, n.name, n.qualified_name, {FileExpr("n")}, {LineExpr("n")}, n.properties
            FROM nodes n
            WHERE (n.label IN ('Method', 'Function') AND n.name IN ('main', 'Main'))
               OR (n.label IN ('Method', 'Function', 'Class') AND (
                    json_extract({SqlSafe("n.properties")}, '$.is_entry_point') = 1
                    OR n.properties LIKE '%Scheduled%' OR n.properties LIKE '%Listener%' OR n.properties LIKE '%MessageDriven%'))
            ORDER BY n.qualified_name, n.id
            """, ct);

        var marked = new List<(NodeRow Node, JsonObject Properties)>();
        foreach (var node in candidates)
        {
            var properties = ParseObject(node.Properties);
            if (IsTrue(properties, "is_test"))
                continue;

            var annotations = AnnotationNames(properties);
            string? kind = null;
            if (annotations.Any(ScheduledAnnotations.Contains))
                kind = EntryPointKinds.Scheduled;
            else if (annotations.Any(MessageConsumerAnnotations.Contains))
                kind = EntryPointKinds.MessageConsumer;
            else if (node.Label != "Class" && node.Name is "main" or "Main")
                kind = EntryPointKinds.Main;

            if (kind != null)
            {
                if (seen.Add(node.Id))
                    entries.Add(Entry(kind, node.QualifiedName, node.File, node.Line));
            }
            else if (IsTrue(properties, "is_entry_point"))
            {
                marked.Add((node, properties));
            }
        }

        // Rutas: las de Struts con la clase que las atiende (HANDLES); el resto, como rutas genéricas.
        var handlers = await ReadHandlersAsync(conn, ct);
        var routes = await ReadNodesAsync(conn, $"""
            SELECT n.id, n.label, n.name, n.qualified_name, {FileExpr("n")}, {LineExpr("n")}, n.properties
            FROM nodes n WHERE n.label = 'Route' ORDER BY n.qualified_name, n.id
            """, ct);
        foreach (var route in routes)
        {
            var properties = ParseObject(route.Properties);
            var path = Text(properties, "route_path") ?? route.Name;
            if (!seen.Add(route.Id))
                continue;

            if (Text(properties, "framework") == "struts")
            {
                entries.Add(handlers.TryGetValue(route.Id, out var handler)
                    ? Entry(EntryPointKinds.StrutsAction, path, handler.File, handler.Line)
                    : Entry(EntryPointKinds.StrutsAction, path, route.File, route.Line));
            }
            else
            {
                var verb = Text(properties, "http_verb") ?? Text(properties, "http_method");
                var name = verb is { Length: > 0 } and not "ANY" ? $"{verb} {path}" : path;
                entries.Add(Entry(EntryPointKinds.Route, name, route.File, route.Line));
            }
        }

        foreach (var (node, _) in marked)
        {
            if (seen.Add(node.Id))
                entries.Add(Entry(EntryPointKinds.Marked, node.QualifiedName, node.File, node.Line));
        }

        entries.Sort((a, b) =>
        {
            var byKind = string.CompareOrdinal(a.Kind, b.Kind);
            if (byKind != 0) return byKind;
            var byName = string.CompareOrdinal(a.Name, b.Name);
            if (byName != 0) return byName;
            var byFile = string.CompareOrdinal(a.File, b.File);
            return byFile != 0 ? byFile : a.Line.CompareTo(b.Line);
        });
        return ([.. entries.Take(Math.Max(limit, 0))], entries.Count);
    }

    private static InventoryEntryPoint Entry(string kind, string name, string file, int line) =>
        new() { Kind = kind, Name = name, File = Normalize(file), Line = line };

    /// <summary>Clase o método que atiende cada ruta (<c>HANDLES</c>); si hay varios, el de menor nombre cualificado.</summary>
    private static async Task<Dictionary<long, (string File, int Line)>> ReadHandlersAsync(SqliteConnection conn, CancellationToken ct)
    {
        var handlers = new Dictionary<long, (string File, int Line)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT e.target_id, {FileExpr("h")}, {LineExpr("h")}
            FROM edges e JOIN nodes h ON h.id = e.source_id
            WHERE e.type = 'HANDLES'
            ORDER BY e.target_id, h.qualified_name, h.id
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var route = reader.GetInt64(0);
            if (!handlers.ContainsKey(route))
                handlers[route] = (reader.GetString(1), reader.GetInt32(2));
        }
        return handlers;
    }

    private static async Task<List<NodeRow>> ReadNodesAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        var rows = new List<NodeRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new NodeRow(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? "" : reader.GetString(4), reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                reader.IsDBNull(6) ? "" : reader.GetString(6)));
        }
        return rows;
    }

    // Solo las columnas nativas de cbm (file_path, start_line): el lector sirve igual sobre un índice crudo que sobre la copia de
    // trabajo, donde GraphSchema añade file y line_start como duplicados.
    private static string FileExpr(string alias) => $"coalesce({alias}.file_path, '')";

    private static string LineExpr(string alias) => $"coalesce({alias}.start_line, 0)";

    private static string SqlSafe(string column) => SqlFacetExtractorStep.SafeJson(column);

    /// <summary>Las propiedades como objeto JSON, o uno vacío si no son JSON válido.</summary>
    private static JsonObject ParseObject(string properties)
    {
        if (string.IsNullOrWhiteSpace(properties))
            return [];
        try
        {
            return JsonNode.Parse(properties) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Text(JsonObject properties, string key) =>
        properties[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static bool IsTrue(JsonObject properties, string key)
    {
        if (properties[key] is not JsonValue value)
            return false;
        if (value.TryGetValue<bool>(out var flag))
            return flag;
        return value.TryGetValue<long>(out var number) && number != 0;
    }

    /// <summary>Nombres simples de las anotaciones, atributos y decoradores del nodo (sin <c>@</c> ni argumentos).</summary>
    private static List<string> AnnotationNames(JsonObject properties)
    {
        var names = new List<string>();
        foreach (var key in AnnotationKeys)
        {
            if (properties[key] is not JsonArray array)
                continue;
            foreach (var item in array)
            {
                if (item is not JsonValue value || !value.TryGetValue<string>(out var text))
                    continue;
                var name = text.TrimStart('@', '[').Trim();
                var cut = name.IndexOfAny(['(', ' ', ']']);
                if (cut >= 0)
                    name = name[..cut];
                var dot = name.LastIndexOf('.');
                names.Add(dot >= 0 ? name[(dot + 1)..] : name);
            }
        }
        return names;
    }

    // ───────────────────────────── Tablas y ficheros ─────────────────────────────

    private static async Task<(IReadOnlyList<InventoryTable>, int)> ReadTablesAsync(SqliteConnection conn, int limit, CancellationToken ct)
    {
        var tables = new List<InventoryTable>();
        using var cmd = conn.CreateCommand();
        // Las tablas de E2 (SQL de las cadenas, escritas por este mismo paso tras leer el grafo) no cuentan como del grafo: ya entran por el escaneo.
        cmd.CommandText = $"""
            SELECT n.name, COUNT(e.id) AS refs
            FROM nodes n LEFT JOIN edges e ON e.target_id = n.id AND {Enrichment.AnalysisGraph.AnalysisGraphLabels.NotDerivedEdgePredicate("e")}
            WHERE n.label IN ('Table', 'AccessTable') AND {Enrichment.AnalysisGraph.AnalysisGraphLabels.NotDerivedTablePredicate("n")}
            GROUP BY n.name
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            tables.Add(new InventoryTable { Name = reader.GetString(0), References = reader.GetInt32(1) });

        tables.Sort((a, b) => a.References != b.References ? b.References.CompareTo(a.References) : string.CompareOrdinal(a.Name, b.Name));
        return ([.. tables.Take(Math.Max(limit, 0))], tables.Count);
    }

    private static async Task<IReadOnlySet<string>> ReadFilesAsync(SqliteConnection conn, CancellationToken ct)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT DISTINCT {FileExpr("n")} FROM nodes n";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var file = reader.IsDBNull(0) ? "" : Normalize(reader.GetString(0));
            if (file.Length > 0)
                files.Add(file);
        }
        return files;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    // ───────────────────────────── Pasadas de enriquecimiento ─────────────────────────────

    private static async Task<(IReadOnlyList<EnricherRunState>, CbmMatchSummary?)> ReadRunsAsync(
        SqliteConnection conn, string? fingerprint, CancellationToken ct)
    {
        var runs = await EnrichmentRuns.ListAsync(conn, ct);
        var states = new List<EnricherRunState>();
        CbmMatchSummary? cbmMatch = null;

        foreach (var group in runs.GroupBy(r => r.Enricher, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            // Sin huella (el grafo no tiene projects) no se puede decir qué indexación es: cuentan todas las pasadas.
            var current = fingerprint == null ? group.ToList() : group.Where(r => r.IndexFingerprint == fingerprint).ToList();
            if (current.Count == 0)
            {
                states.Add(new EnricherRunState(group.Key, StaleStatus, group.Max(r => r.Id), ""));
                continue;
            }

            var last = current.MaxBy(r => r.Id)!;
            states.Add(new EnricherRunState(group.Key, last.Status, last.Id, last.Message ?? ""));

            if (group.Key == JavaGraphWriter.EnricherName)
            {
                var lastOk = current.Where(r => r.Status == EnrichmentRuns.StatusOk).MaxBy(r => r.Id);
                cbmMatch = lastOk?.Outcome?.Details is { } details ? ParseCbmMatch(details) : null;
            }
        }

        return (states, cbmMatch);
    }

    /// <summary><c>details.cbm_match</c> de la pasada del analizador Java (<c>JavaGraphWriter</c>); null si no está o no es JSON.</summary>
    private static CbmMatchSummary? ParseCbmMatch(string details)
    {
        if (ParseObject(details)["cbm_match"] is not JsonObject match)
            return null;

        int Int(string key) => match[key] is JsonValue v && v.TryGetValue<long>(out var n) ? (int)n : 0;
        double Number(string key) => match[key] is JsonValue v && v.TryGetValue<double>(out var n) ? n : 0;

        return new CbmMatchSummary
        {
            UnindexedFiles = Int("unindexed_files"),
            UnindexedFilesLongPath = Int("unindexed_files_long_path"),
            TypesUnmatchedRatio = Number("types_unmatched_ratio"),
            MethodsUnmatchedRatio = Number("methods_unmatched_ratio"),
            DriftSuspected = IsTrue(match, "drift_suspected")
        };
    }
}
