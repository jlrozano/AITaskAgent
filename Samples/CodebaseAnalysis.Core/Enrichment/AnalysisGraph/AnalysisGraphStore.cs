using System.Globalization;
using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Support;
using Microsoft.Data.Sqlite;

namespace CodebaseAnalysis.Core.Enrichment.AnalysisGraph;

/// <summary>
/// Etiquetas, aristas y marcas de lo que el análisis deja en el grafo para el chat (plan 2026-10-06, E1-E3): subsistemas del mapa de
/// dominios, tablas del SQL escrito en cadenas y notas de las investigaciones. Son nodos sin fichero (<c>file_path</c> vacío), así
/// que los lectores que agrupan por fichero no los ven.
/// </summary>
public static class AnalysisGraphLabels
{
    public const string Subsystem = "Subsystem";
    public const string Table = "Table";
    public const string InvestigationNote = "InvestigationNote";

    /// <summary>Subsistema → nodo de código, JSP o ruta de sus ficheros.</summary>
    public const string Contains = "CONTAINS";

    /// <summary>Subsistema → subsistema del que usa código (derivada de las aristas de código entre sus miembros).</summary>
    public const string DependsOn = "DEPENDS_ON";

    /// <summary>Código → tabla que lee (<c>SELECT</c>, <c>WITH</c>, o las de <c>FROM</c>/<c>JOIN</c> de una escritura).</summary>
    public const string Queries = "QUERIES";

    /// <summary>Código → tabla que escribe (<c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c>, <c>MERGE</c> y DDL).</summary>
    public const string Modifies = "MODIFIES";

    /// <summary>Nota → subsistema investigado.</summary>
    public const string About = "ABOUT";

    /// <summary>Nota → nodo de código que contiene una <c>ruta:línea</c> citada.</summary>
    public const string Cites = "CITES";

    /// <summary>Marca (<c>enriched_by</c>) de los subsistemas y sus aristas.</summary>
    public const string SubsystemEnricher = "SubsystemMap";

    /// <summary>Marca de las tablas creadas y de las aristas <c>QUERIES</c>/<c>MODIFIES</c>.</summary>
    public const string SqlTableEnricher = "SqlTableAccess";

    /// <summary>
    /// Marca de las notas y sus aristas. El contenido de una nota lo escribió el LLM: <c>graph_neighbors</c> la presenta como
    /// estimación, no como arista resuelta.
    /// </summary>
    public const string NoteEnricher = "InvestigationNotes";

    // ───────── Registro del LLM (paquete C del plan 2026-10-16): hallazgos, anclas, resúmenes, lotes y ejecuciones ─────────

    /// <summary>Un hallazgo del registro (bug, deuda o seguridad), con su severidad y su estado. Escrito por el LLM.</summary>
    public const string Finding = "Finding";

    /// <summary>El fragmento de código (ruta, líneas, cita literal) en que se apoya un hallazgo.</summary>
    public const string Anchor = "Anchor";

    /// <summary>Un resumen en prosa de un símbolo, un fichero, un subsistema o el sistema. Escrito por el LLM.</summary>
    public const string Summary = "Summary";

    /// <summary>Un lote de la pasada compartida (normalmente un subsistema).</summary>
    public const string Batch = "Batch";

    /// <summary>Una ejecución de la pasada que escribe el registro: modelos y versiones de los prompts.</summary>
    public const string Run = "Run";

    /// <summary>Hallazgo → ancla.</summary>
    public const string At = "AT";

    /// <summary>Ancla → nodo <c>File</c> del fichero anclado.</summary>
    public const string In = "IN";

    /// <summary>Hallazgo → semilla (símbolo con un issue o código muerto del grafo) de la que partió.</summary>
    public const string FromSeed = "FROM_SEED";

    /// <summary>Hallazgo → hallazgo del que es una instancia (duplicado fusionado).</summary>
    public const string InstanceOf = "INSTANCE_OF";

    /// <summary>Resumen → nodo resumido.</summary>
    public const string Summarizes = "SUMMARIZES";

    /// <summary>Nodo (hallazgo, resumen, tipo, fichero) → lote en el que se leyó.</summary>
    public const string InBatch = "IN_BATCH";

    /// <summary>Hallazgo, resumen o lote → <c>Run</c> que lo escribió.</summary>
    public const string InRun = "IN_RUN";

    /// <summary>Marca (<c>enriched_by</c>) de todo lo que escribe el registro del LLM.</summary>
    public const string LlmRegistryEnricher = "LlmRegistry";

    /// <summary>Valor de <c>origin</c> de los nodos escritos por el LLM (los de código conservan el suyo).</summary>
    public const string OriginLlm = "llm";

    /// <summary>Prefijo de las propiedades del contrato LLM de un método (<c>llm_does</c>, <c>llm_inputs</c>…).</summary>
    public const string LlmPropertyPrefix = "llm_";

    /// <summary>Labels de los nodos del registro del LLM: no son código ni deben contar en lectores por etiqueta.</summary>
    public static IReadOnlyList<string> RegistryLabels { get; } = [Finding, Anchor, Summary, Batch, Run];

    public static string RunQualifiedName(string runId) => $"llm_run:{runId}";

    public static string BatchQualifiedName(string runId, string batch) => $"llm_batch:{runId}:{batch}";

    public static string FindingQualifiedName(string runId, string id) => $"llm_finding:{runId}:{id}";

    public static string AnchorQualifiedName(string runId, string findingId, int index) => $"llm_anchor:{runId}:{findingId}:{index}";

    public static string SummaryQualifiedName(string runId, string level, string key) => $"llm_summary:{runId}:{level}:{key}";

    /// <summary>
    /// Predicado SQL «el nodo con alias <paramref name="alias"/> lo escribió el LLM» (<c>origin = 'llm'</c>). Las consultas por defecto
    /// de las herramientas lo excluyen: solo lo ven las vistas <c>findings</c> y <c>llm_contracts</c> de <c>graph_domain</c>.
    /// </summary>
    public static string LlmNodePredicate(string alias) =>
        $"(CASE WHEN json_valid({alias}.properties) THEN coalesce(json_extract({alias}.properties, '$.origin'), '') = '{OriginLlm}' ELSE 0 END)";

    public static string SubsystemQualifiedName(string subsystemId) => "subsystem:" + subsystemId;

    public static string TableQualifiedName(string normalizedName) => "sqltable:" + normalizedName;

    public static string NoteQualifiedName(string report, string subsystemId) => $"investigation_note:{report}:{subsystemId}";

    /// <summary>Nombre normalizado de una tabla: sin espacios alrededor y en mayúsculas invariantes.</summary>
    public static string NormalizeTable(string name) => name.Trim().ToUpperInvariant();

    /// <summary>
    /// Predicado SQL «el nodo con alias <paramref name="alias"/> no es una tabla creada por <see cref="SqlTableEnricher"/>»: los
    /// lectores de tablas del grafo anteriores a E2 (inventario, grafo de tipos, facetas) lo usan para que el mapa de dominios y el
    /// inventario no cambien según haya quedado o no la pasada E2 de un análisis anterior en la copia.
    /// </summary>
    public static string NotDerivedTablePredicate(string alias) =>
        $"NOT (CASE WHEN json_valid({alias}.properties) THEN coalesce(json_extract({alias}.properties, '$.origin') = '{EnrichmentMarks.OriginEnricher}' "
        + $"AND EXISTS (SELECT 1 FROM json_each({alias}.properties, '$.enriched_by') WHERE value = '{SqlTableEnricher}'), 0) ELSE 0 END)";

    /// <summary>
    /// Predicado SQL «la arista con alias <paramref name="alias"/> no es de E2»: E2 también enlaza con las tablas que ya traía el grafo
    /// de otro indexador, y esas aristas tampoco deben contar para los lectores anteriores a E2.
    /// </summary>
    public static string NotDerivedEdgePredicate(string alias) =>
        $"NOT (CASE WHEN json_valid({alias}.properties) THEN EXISTS (SELECT 1 FROM json_each({alias}.properties, '$.enriched_by') "
        + $"WHERE value = '{SqlTableEnricher}') ELSE 0 END)";
}

/// <summary>Escritura común de E1-E3: conexión, nodos, aristas y limpieza de la pasada anterior.</summary>
internal sealed class AnalysisGraphStore(SqliteConnection conn, SqliteTransaction tx, string project) : IDisposable
{
    public SqliteConnection Connection => conn;
    public SqliteTransaction Transaction => tx;
    public string Project => project;

    public static async Task<AnalysisGraphStore> OpenAsync(string databasePath, CancellationToken ct)
    {
        EnrichmentMarks.AssertWorkingCopy(databasePath);
        await GraphSchema.EnsureAsync(databasePath, ct);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false, DefaultTimeout = 30 }.ToString());
        await conn.OpenAsync(ct);
        var tx = conn.BeginTransaction();
        var project = await GraphProject.ResolveAsync(conn, "Codebase", ct, tx);
        return new AnalysisGraphStore(conn, tx, project);
    }

    public SqliteCommand Command(string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    public async Task<int> ExecuteAsync(string sql, CancellationToken ct, Action<SqliteParameterCollection>? bind = null)
    {
        using var cmd = Command(sql);
        bind?.Invoke(cmd.Parameters);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Los nodos de <paramref name="label"/> creados por <paramref name="enricher"/> en una pasada anterior, por nombre cualificado.</summary>
    public async Task<Dictionary<string, long>> LoadCreatedAsync(string enricher, string label, CancellationToken ct)
    {
        using var cmd = Command($"SELECT qualified_name, id FROM nodes WHERE label = @label AND {EnrichmentMarks.CreatedByPredicate()}");
        cmd.Parameters.AddWithValue("@label", label);
        cmd.Parameters.AddWithValue("@enricher", enricher);
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)] = reader.GetInt64(1);
        return result;
    }

    /// <summary>Borra las aristas de <paramref name="edgeTypes"/> con la marca de <paramref name="enricher"/> (filtra por tipo antes que por JSON).</summary>
    public async Task<int> RemoveMarkedEdgesAsync(string enricher, IReadOnlyList<string> edgeTypes, CancellationToken ct) =>
        edgeTypes.Count == 0
            ? 0
            : await ExecuteAsync(
                $"DELETE FROM edges WHERE type IN ({string.Join(", ", edgeTypes.Select((_, i) => $"@t{i}"))}) AND {EnrichmentMarks.EnrichedByPredicate()}",
                ct, p =>
                {
                    for (var i = 0; i < edgeTypes.Count; i++)
                        p.AddWithValue($"@t{i}", edgeTypes[i]);
                    p.AddWithValue("@enricher", enricher);
                });

    /// <summary>
    /// Borra, con todas sus aristas, los nodos de <paramref name="previous"/> que esta pasada no ha vuelto a escribir. Los que sí se
    /// conservan con su id, y con él las aristas que otros dejaron hacia ellos (<c>DOC_DESCRIBES</c> de un documento, <c>ABOUT</c> de una nota).
    /// </summary>
    public async Task<(int Nodes, int Edges)> RemoveStaleAsync(IReadOnlyDictionary<string, long> previous, IReadOnlySet<long> kept, CancellationToken ct)
    {
        var stale = previous.Values.Where(id => !kept.Contains(id)).ToList();
        if (stale.Count == 0)
            return (0, 0);
        return await EnrichmentMarks.RemoveNodesAsync(conn, tx,
            $"SELECT id FROM nodes WHERE id IN ({string.Join(", ", stale.Select(id => id.ToString(CultureInfo.InvariantCulture)))})", null, ct);
    }

    /// <summary>
    /// Reescribe el nodo <paramref name="qualifiedName"/> si está en <paramref name="previous"/> (nombre y propiedades nuevas, mismo id) y,
    /// si no, lo crea. Devuelve su id.
    /// </summary>
    public async Task<long> UpsertNodeAsync(IReadOnlyDictionary<string, long> previous, string label, string name, string qualifiedName,
        string enricher, DateTimeOffset at, JsonObject properties, CancellationToken ct)
    {
        if (!previous.TryGetValue(qualifiedName, out var id))
            return await InsertNodeAsync(label, name, qualifiedName, enricher, at, properties, ct);

        await ExecuteAsync("UPDATE nodes SET name = @name, properties = @props WHERE id = @id", ct, p =>
        {
            p.AddWithValue("@name", name);
            p.AddWithValue("@props", EnrichmentMarks.CreatedNodeProperties(enricher, at, properties).ToJsonString());
            p.AddWithValue("@id", id);
        });
        return id;
    }

    /// <summary>Inserta un nodo creado por <paramref name="enricher"/>, sin fichero, y devuelve su id.</summary>
    public async Task<long> InsertNodeAsync(string label, string name, string qualifiedName, string enricher, DateTimeOffset at,
        JsonObject properties, CancellationToken ct, string? origin = null)
    {
        var props = EnrichmentMarks.CreatedNodeProperties(enricher, at, properties);
        if (origin != null)
            props["origin"] = origin;
        using var cmd = Command("""
            INSERT INTO nodes (project, project_id, label, name, qualified_name, file_path, file, start_line, end_line, line_start, line_end, properties)
            VALUES (@project, 1, @label, @name, @qn, '', '', 0, 0, 0, 0, @props);
            SELECT last_insert_rowid();
            """);
        cmd.Parameters.AddWithValue("@project", project);
        cmd.Parameters.AddWithValue("@label", label);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@qn", qualifiedName);
        cmd.Parameters.AddWithValue("@props", props.ToJsonString());
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <summary>Inserta una arista con la marca de <paramref name="enricher"/>; false si ya existía (misma terna origen, destino y tipo).</summary>
    public async Task<bool> InsertEdgeAsync(long source, long target, string type, string enricher, JsonObject? properties, CancellationToken ct)
    {
        using var cmd = Command("""
            INSERT OR IGNORE INTO edges (project, project_id, source_id, target_id, type, properties)
            VALUES (@project, 1, @s, @t, @type, @props);
            """);
        cmd.Parameters.AddWithValue("@project", project);
        cmd.Parameters.AddWithValue("@s", source);
        cmd.Parameters.AddWithValue("@t", target);
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@props", EnrichmentMarks.EdgeProperties(enricher, properties).ToJsonString());
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public void Commit() => tx.Commit();

    public void Dispose()
    {
        tx.Dispose();
        conn.Dispose();
    }

    public static JsonArray ToArray(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);

    public static JsonArray ToArray(IEnumerable<int> values) => new([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);
}
