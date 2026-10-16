using Dapper;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotNetEnricher;

/// <summary>
/// Acceso de escritura al grafo SQLite de codebase-memory-mcp.
/// Compatible con el schema 1.x del motor (tablas: nodes, edges).
///
/// Toda la pasada escribe por una sola conexión y dentro de una sola transacción, que abre y confirma
/// <see cref="EnrichmentPipeline"/>: si algo falla, el grafo queda como estaba. Antes cada operación abría su propia
/// conexión y confirmaba sola, y un fallo a mitad dejaba el grafo a medio enriquecer. Los enriquecedores que
/// consultan con SQL propio usan <see cref="QueryAsync(string, object?)"/> y compañía, que ya pasan la transacción.
/// </summary>
public class GraphRepository(SqliteConnection connection, SqliteTransaction transaction)
{
    private readonly Dictionary<(string Project, string Qn), long> _idCache = [];
    private readonly HashSet<long> _patchedNodes = [];
    private bool? _hasFts;

    public SqliteConnection  Connection  => connection;
    public SqliteTransaction Transaction => transaction;

    /// <summary>Nodos insertados en esta pasada (los que ya existían y se actualizaron no cuentan).</summary>
    public int NodesCreated { get; private set; }

    /// <summary>Nodos distintos cuyas propiedades se han parcheado.</summary>
    public int NodesPatched => _patchedNodes.Count;

    /// <summary>Aristas insertadas o actualizadas.</summary>
    public int EdgesWritten { get; private set; }

    /// <summary>
    /// Procedencia que se sella en todo lo que se escribe por este repositorio (<see cref="ProvenanceStamp"/>); la fija la
    /// pasada. Null (los tests que usan el repositorio suelto): se escribe tal cual.
    /// </summary>
    public ProvenanceStamp? Provenance { get; set; }

    // ─── SQL propio de los enriquecedores (con la transacción de la pasada) ─────

    public Task<IEnumerable<dynamic>> QueryAsync(string sql, object? param = null) =>
        connection.QueryAsync(sql, param, transaction);

    public Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null) =>
        connection.QueryAsync<T>(sql, param, transaction);

    public Task<int> ExecuteAsync(string sql, object? param = null) =>
        connection.ExecuteAsync(sql, param, transaction);

    public Task<T?> ExecuteScalarAsync<T>(string sql, object? param = null) =>
        connection.ExecuteScalarAsync<T>(sql, param, transaction);

    // ─── Node helpers ───────────────────────────────────────────────────────────

    public async Task<long?> GetNodeIdAsync(string project, string qualifiedName)
    {
        if (_idCache.TryGetValue((project, qualifiedName), out var cached))
            return cached;

        var id = await connection.ExecuteScalarAsync<long?>(
            "SELECT id FROM nodes WHERE project = @project AND qualified_name = @qualifiedName",
            new { project, qualifiedName }, transaction);
        // Solo se guardan los aciertos: un nodo que no existe puede crearlo un enriquecedor posterior.
        if (id is long found)
            _idCache[(project, qualifiedName)] = found;
        return id;
    }

    public async Task<bool> NodeExistsAsync(long id, string project)
    {
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM nodes WHERE id = @id AND project = @project",
            new { id, project }, transaction) > 0;
    }

    public async Task<IEnumerable<(long Id, string Label, string Name, string QualifiedName, string FilePath, string Properties)>>
        GetNodesByLabelAsync(string project, string label)
    {
        var rows = await connection.QueryAsync(
            "SELECT id, label, name, qualified_name, file_path, properties FROM nodes WHERE project = @project AND label = @label",
            new { project, label }, transaction);
        return rows.Select(r => ((long)r.id, (string)r.label, (string)r.name,
            (string)r.qualified_name, (string)r.file_path, (string)r.properties));
    }

    /// <summary>
    /// Añade o fusiona propiedades en el JSON 'properties' de un nodo.
    /// No sobreescribe claves existentes que no estén en el patch.
    /// </summary>
    public async Task<int> PatchNodePropertiesAsync(string project, string qualifiedName, object patch)
    {
        var nodeId = await GetNodeIdAsync(project, qualifiedName);
        if (nodeId is null) return 0;
        return await PatchNodePropertiesByIdAsync(nodeId.Value, patch);
    }

    public async Task<int> PatchNodePropertiesByIdAsync(long nodeId, object patch)
    {
        var current = await connection.ExecuteScalarAsync<string>(
            "SELECT properties FROM nodes WHERE id = @id", new { id = nodeId }, transaction);
        var merged = MergeJson(current ?? "{}", patch, Provenance);
        var rows = await connection.ExecuteAsync(
            "UPDATE nodes SET properties = @props WHERE id = @id",
            new { props = merged, id = nodeId }, transaction);
        if (rows > 0) _patchedNodes.Add(nodeId);
        return rows;
    }

    // ─── Node upsert (para nodos nuevos como DiRegistration) ────────────────────

    public async Task<long> UpsertNodeAsync(string project, string label, string name,
        string qualifiedName, string filePath, int startLine, object? properties = null,
        int? endLine = null)
    {
        var props = Stamp(properties, edge: false);
        var endLineVal = endLine ?? startLine;
        var existed = await GetNodeIdAsync(project, qualifiedName) is not null;

        // 1. Insertar o actualizar en la tabla física de nodos
        await connection.ExecuteAsync("""
            INSERT INTO nodes (project, label, name, qualified_name, file_path, start_line, end_line, properties)
            VALUES (@project, @label, @name, @qualifiedName, @filePath, @startLine, @endLineVal, @props)
            ON CONFLICT(project, qualified_name) DO UPDATE SET
                label      = excluded.label,
                name       = excluded.name,
                file_path  = excluded.file_path,
                start_line = excluded.start_line,
                end_line   = excluded.end_line,
                properties = excluded.properties
            """,
            new { project, label, name, qualifiedName, filePath, startLine, endLineVal, props }, transaction);

        var nodeId = await connection.ExecuteScalarAsync<long>(
            "SELECT id FROM nodes WHERE project = @project AND qualified_name = @qualifiedName",
            new { project, qualifiedName }, transaction);
        _idCache[(project, qualifiedName)] = nodeId;
        if (!existed) NodesCreated++;

        // 2. Sincronizar manualmente la tabla virtual de FTS5 (nodes_fts), si la BD la tiene
        //
        // Los FTS5 contentless NO admiten DELETE estándar: el borrado es un INSERT con el sentinel 'delete' y los
        // tokens originales, y con otros tokens corrompe el índice. Solo se indexan los nodos nuevos; un nodo que ya
        // existía conserva su entrada (la pasada anterior de este enriquecedor se borra antes de empezar, así que sus
        // nodos vuelven a ser nuevos). Antes se intentaba el 'delete' siempre y se ignoraba el error.
        if (existed || !await HasFtsAsync()) return nodeId;

        var splitName        = SplitCamelCase(name);
        var splitQualified   = SplitCamelCase(qualifiedName);
        var splitLabel       = SplitCamelCase(label);
        var splitFilePath    = SplitCamelCase(filePath ?? "");

        // OR IGNORE protege ante un rowid que ya estuviera en el índice.
        await connection.ExecuteAsync("""
            INSERT OR IGNORE INTO nodes_fts(rowid, name, qualified_name, label, file_path)
            VALUES (@nodeId, @splitName, @splitQualified, @splitLabel, @splitFilePath)
            """,
            new { nodeId, splitName, splitQualified, splitLabel, splitFilePath }, transaction);

        return nodeId;
    }

    private async Task<bool> HasFtsAsync() =>
        _hasFts ??= await connection.ExecuteScalarAsync<long?>(
            "SELECT 1 FROM sqlite_master WHERE name = 'nodes_fts'", transaction: transaction) is not null;

    private static string SplitCamelCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var sb = new System.Text.StringBuilder();
        // Guardar el nombre original
        sb.Append(input).Append(' ');

        for (int i = 0; i < input.Length; i++)
        {
            char curr = input[i];
            if (i > 0)
            {
                char prev = input[i - 1];
                char next = i + 1 < input.Length ? input[i + 1] : '\0';

                // 1. Minúscula a Mayúscula
                if (char.IsUpper(curr) && char.IsLower(prev))
                {
                    sb.Append(' ');
                }
                // 2. Mayúscula a Mayúscula seguida de Minúscula (ej: XMLParser -> XML Parser)
                else if (char.IsUpper(curr) && char.IsUpper(prev) && char.IsLower(next))
                {
                    sb.Append(' ');
                }
            }

            // Reemplazar delimitadores comunes por espacios
            if (curr is '.' or '/' or '_' or '-' or ':' or '(' or ')')
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(curr);
            }
        }

        return sb.ToString();
    }

    // ─── Edge upsert ────────────────────────────────────────────────────────────

    public async Task<int> UpsertEdgeAsync(string project, long sourceId, long targetId,
        string type, object? properties = null)
    {
        var props = Stamp(properties, edge: true);
        var rows = await connection.ExecuteAsync("""
            INSERT INTO edges (project, source_id, target_id, type, properties)
            VALUES (@project, @sourceId, @targetId, @type, @props)
            ON CONFLICT(source_id, target_id, type) DO UPDATE SET
                properties = excluded.properties
            """,
            new { project, sourceId, targetId, type, props }, transaction);
        EdgesWritten += rows;
        return rows;
    }

    /// <summary>
    /// Borra las aristas de cbm de un tipo que salen de nodos de los ficheros dados (rutas relativas, como el
    /// <c>file_path</c> de cbm) y devuelve sus pares (origen, destino). Solo las de cbm: una arista con <c>enriched_by</c> es
    /// de algún enriquecedor (de esta pasada o del de Java) y no se toca. Lo usan los enriquecedores que sustituyen las
    /// aristas que cbm resuelve por nombre por las exactas del compilador, como el enriquecedor Java de la app.
    /// </summary>
    public Task<List<(long Source, long Target)>> DeleteCbmEdgesFromFilesAsync(
        string project, string type, IReadOnlySet<string> relativeFiles) =>
        WithCbmEdgeSourcesAsync(project, relativeFiles, () => connection.QueryAsync<(long, long)>($"""
            DELETE FROM edges WHERE {CbmEdgesFromSources}
            RETURNING source_id, target_id
            """, new { project, type }, transaction));

    /// <summary>
    /// Las aristas de cbm que <see cref="DeleteCbmEdgesFromFilesAsync"/> borraría, sin borrarlas: id, origen, destino y el
    /// nombre del nodo destino (cbm las resuelve por nombre).
    /// </summary>
    public Task<List<(long Id, long Source, long Target, string TargetName)>> ListCbmEdgesFromFilesAsync(
        string project, string type, IReadOnlySet<string> relativeFiles) =>
        WithCbmEdgeSourcesAsync(project, relativeFiles, () => connection.QueryAsync<(long, long, long, string)>($"""
            SELECT id, source_id, target_id, COALESCE((SELECT t.name FROM nodes t WHERE t.id = edges.target_id), '') FROM edges
            WHERE {CbmEdgesFromSources}
            """, new { project, type }, transaction));

    /// <summary>Borra aristas por id.</summary>
    public async Task<int> DeleteEdgesAsync(IReadOnlyCollection<long> ids) =>
        ids.Count == 0 ? 0 : await connection.ExecuteAsync("DELETE FROM edges WHERE id = @id", ids.Select(id => new { id }), transaction);

    private const string CbmEdgesFromSources = """
        project = @project AND type = @type
          AND source_id IN (SELECT id FROM temp.dne_edge_sources)
          AND NOT (CASE WHEN json_valid(properties) THEN json_type(properties, '$.enriched_by') IS NOT NULL ELSE 0 END)
        """;

    /// <summary>Deja en <c>temp.dne_edge_sources</c> los nodos de los ficheros dados mientras corre <paramref name="run"/>.</summary>
    private async Task<List<T>> WithCbmEdgeSourcesAsync<T>(string project, IReadOnlySet<string> relativeFiles, Func<Task<IEnumerable<T>>> run)
    {
        if (relativeFiles.Count == 0) return [];
        var sources = (await connection.QueryAsync("SELECT id, file_path FROM nodes WHERE project = @project", new { project }, transaction))
            .Where(r => r.file_path is string path && relativeFiles.Contains(path.Replace('\\', '/')))
            .Select(r => (long)r.id)
            .ToList();
        if (sources.Count == 0) return [];

        await connection.ExecuteAsync("CREATE TEMP TABLE IF NOT EXISTS dne_edge_sources (id INTEGER PRIMARY KEY)", transaction: transaction);
        await connection.ExecuteAsync("DELETE FROM temp.dne_edge_sources", transaction: transaction);
        await connection.ExecuteAsync("INSERT OR IGNORE INTO temp.dne_edge_sources (id) VALUES (@id)", sources.Select(id => new { id }), transaction);
        var rows = await run();
        await connection.ExecuteAsync("DELETE FROM temp.dne_edge_sources", transaction: transaction);
        return [.. rows];
    }

    /// <summary>
    /// IDF de los tokens de <c>token_vectors</c>, que crea codebase-memory-mcp al indexar; vacío si la BD no tiene la tabla.
    /// Solo lo usa el nombrado de comunidades de <see cref="Enrichers.GraphAnalyticsEnricher"/>.
    /// </summary>
    public async Task<Dictionary<string, float>> LoadTokenIdfsAsync(string project)
    {
        var exists = await connection.ExecuteScalarAsync<long?>(
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'token_vectors'", transaction: transaction);
        if (exists is null) return [];

        var rows = await connection.QueryAsync(
            "SELECT token, idf FROM token_vectors WHERE project = @project",
            new { project }, transaction);
        var idfs = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var r in rows)
            idfs[(string)r.token] = (float)(long)r.idf;
        return idfs;
    }

    /// <summary>
    /// Returns the qualified_names of all nodes that are targets of HANDLES edges.
    /// Used by HandlesEntryProvider to discover HTTP endpoint entry points.
    /// </summary>
    public async Task<IEnumerable<string>> GetHandlesTargetQnsAsync(string project)
    {
        // source_id = el Method handler; target_id = el nodo Route (URL path).
        // El FlowEnricher necesita el Method para caminar su AST con Roslyn.
        return await connection.QueryAsync<string>("""
            SELECT n.qualified_name
            FROM edges e
            JOIN nodes n ON n.id = e.source_id AND n.project = e.project
            WHERE e.project = @project AND e.type = 'HANDLES'
            """, new { project }, transaction);
    }

    public async Task<int> PatchNodesByLabelAsync(string project, string label, object patch)
    {
        var rows = await connection.QueryAsync(
            "SELECT id FROM nodes WHERE project = @project AND label = @label",
            new { project, label }, transaction);

        int count = 0;
        foreach (var row in rows)
            count += await PatchNodePropertiesByIdAsync((long)row.id, patch);
        return count;
    }

    // ─── JSON merge ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Fusiona el parche sobre las propiedades. Dos claves no se sustituyen: <c>enriched_by</c> se une a la lista que ya
    /// hubiera (antes cada enriquecedor borraba la marca de los anteriores, incluida la de otros enriquecedores de la app,
    /// y la limpieza por marca no los encontraba), y <c>origin</c> no se toca, porque un parche no cambia quién creó el
    /// nodo: un nodo de cbm marcado <c>origin = enricher</c> se borraría como propio al repetir la pasada. Unas
    /// propiedades que no son un objeto JSON válido se conservan, como en el resto de la app.
    /// </summary>
    internal static string MergeJson(string existing, object patch, ProvenanceStamp? provenance = null)
    {
        var existingNode = CodebaseAnalysis.Core.Enrichment.EnrichmentMarks.ParseProperties(existing);
        var patchJson = JsonSerializer.Serialize(patch);
        var patchNode = JsonNode.Parse(patchJson) as JsonObject ?? [];

        foreach (var (key, value) in patchNode)
        {
            switch (key)
            {
                case "origin":
                    continue;
                case "enriched_by" when value is JsonArray marks:
                    foreach (var mark in marks)
                        if (mark?.GetValueKind() == JsonValueKind.String)
                            CodebaseAnalysis.Core.Enrichment.EnrichmentMarks.AddMark(existingNode, mark.GetValue<string>());
                    continue;
                default:
                    existingNode[key] = value?.DeepClone();
                    break;
            }
        }

        provenance?.ApplyToNode(existingNode, keepEnrichedAt: patchNode.ContainsKey("enriched_at"));
        return existingNode.ToJsonString();
    }

    /// <summary>Propiedades de un nodo creado o de una arista, con la procedencia de la pasada si la hay.</summary>
    private string Stamp(object? properties, bool edge)
    {
        if (Provenance is null) return properties is null ? "{}" : JsonSerializer.Serialize(properties);
        JsonObject node = properties is null ? [] : JsonNode.Parse(JsonSerializer.Serialize(properties)) as JsonObject ?? [];
        if (edge) Provenance.ApplyToEdge(node);
        else Provenance.ApplyToNode(node, keepEnrichedAt: true);
        return node.ToJsonString();
    }
}
