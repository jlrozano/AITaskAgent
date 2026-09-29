using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace CodebaseAnalysis.Core.Enrichment;

/// <summary>
/// Marca de origen y limpieza de lo que escribe un enriquecedor sobre la copia del grafo (D8, P12). Los nodos creados
/// llevan <c>origin:"enricher"</c>, <c>enriched_by:[…]</c> y <c>enriched_at</c>; las aristas escritas y las propiedades
/// completadas llevan <c>enriched_by</c>. Hay nodos de cbm con <c>properties</c> que no es JSON válido, así que ninguna
/// consulta sobre <c>enriched_by</c> llega a <c>json_each</c> sin pasar antes por <c>json_valid</c>.
/// </summary>
public static class EnrichmentMarks
{
    public const string OriginEnricher = "enricher";

    /// <summary>Clave con la que se conservan unas <c>properties</c> de cbm que no eran un objeto JSON válido.</summary>
    public const string RawPropertiesKey = "cbm_raw_properties";

    /// <summary>
    /// Predicado SQL «la fila lleva la marca del enriquecedor <paramref name="enricherParameter"/>». Es un <c>CASE</c> y no
    /// un <c>AND</c> porque SQLite puede reordenar los términos de un <c>WHERE</c> y evaluar <c>json_each</c> sobre un
    /// JSON inválido, que es un error y no un falso.
    /// </summary>
    public static string EnrichedByPredicate(string enricherParameter = "@enricher", string column = "properties") =>
        $"(CASE WHEN json_valid({column}) THEN EXISTS (SELECT 1 FROM json_each({column}, '$.enriched_by') WHERE value = {enricherParameter}) ELSE 0 END)";

    /// <summary>Predicado SQL «nodo creado por el enriquecedor»: además de la marca, <c>origin = 'enricher'</c>.</summary>
    public static string CreatedByPredicate(string enricherParameter = "@enricher", string column = "properties") =>
        $"(CASE WHEN json_valid({column}) THEN json_extract({column}, '$.origin') = '{OriginEnricher}' AND EXISTS (SELECT 1 FROM json_each({column}, '$.enriched_by') WHERE value = {enricherParameter}) ELSE 0 END)";

    /// <summary>
    /// Los enriquecedores escriben en la copia del análisis, nunca en la BD que mantiene codebase-memory-mcp en
    /// <c>~/.cache/codebase-memory-mcp</c> (D8). Se comprueba antes de abrir nada.
    /// </summary>
    /// <exception cref="InvalidOperationException">Si la ruta cae dentro de la caché de cbm.</exception>
    public static void AssertWorkingCopy(string databasePath)
    {
        var cache = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codebase-memory-mcp")));
        var full = Path.GetFullPath(databasePath);
        if (full.Equals(cache, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(cache + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"'{databasePath}' es la BD de codebase-memory-mcp; los enriquecedores solo escriben en la copia del análisis.");
    }

    /// <summary>Caché de páginas de la conexión de escritura, en KiB (512 MiB).</summary>
    public const int BulkWriteCacheKib = 512 * 1024;

    /// <summary>
    /// Abre la copia del análisis para una escritura masiva (criterio 9). La tabla <c>edges</c> de la copia lleva nueve
    /// índices (los de cbm más los de <c>GraphSchema</c>) y cada arista borrada o insertada los toca todos: con la caché
    /// por defecto (2 MiB) la transacción vuelca al WAL las mismas páginas una y otra vez, y la escritura Java sobre un repo
    /// de 8.400 ficheros pasaba de 100 s. Los PRAGMA son de esta conexión y no cambian el fichero: <c>journal_mode</c> (WAL,
    /// que es persistente y comparten las demás conexiones) no se toca, y <c>synchronous = OFF</c> solo quita los fsync de
    /// esta escritura, que es aceptable porque la copia se regenera desde cbm. Sin pool, para que la caché de 512 MiB se
    /// libere al cerrar y no se quede viva en el pool durante el resto del análisis.
    /// </summary>
    /// <exception cref="InvalidOperationException">Si la ruta cae dentro de la caché de cbm.</exception>
    public static async Task<SqliteConnection> OpenForBulkWriteAsync(string databasePath, CancellationToken ct)
    {
        AssertWorkingCopy(databasePath);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA cache_size = -{BulkWriteCacheKib}; PRAGMA temp_store = MEMORY; PRAGMA synchronous = OFF;";
        await cmd.ExecuteNonQueryAsync(ct);
        return conn;
    }

    /// <summary>
    /// Borra la salida de la pasada anterior del enriquecedor: sus nodos creados (con todas sus aristas y vectores) y las
    /// aristas que lleven su marca. Las propiedades que completó en nodos de cbm no se tocan: la pasada nueva las reescribe.
    /// </summary>
    public static async Task<(int Nodes, int Edges)> RemovePreviousOutputAsync(
        SqliteConnection conn, SqliteTransaction tx, string enricher, CancellationToken ct)
    {
        var (nodes, edgesOfNodes) = await RemoveNodesAsync(
            conn, tx, $"SELECT id FROM nodes WHERE {CreatedByPredicate()}",
            p => p.AddWithValue("@enricher", enricher), ct);

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM edges WHERE {EnrichedByPredicate()}";
        cmd.Parameters.AddWithValue("@enricher", enricher);
        var edges = await cmd.ExecuteNonQueryAsync(ct);
        return (nodes, edgesOfNodes + edges);
    }

    /// <summary>
    /// Retira los nodos cuyo <c>file_path</c> es una de las raíces (relativas, con <c>/</c>) o cuelga de ella, con sus
    /// aristas en los dos sentidos y sus <c>node_vectors</c>. <c>nodes_fts</c> no se toca: es <i>contentless</i> y la
    /// app no lo consulta.
    /// </summary>
    public static async Task<(int Nodes, int Edges)> RemoveNodesUnderAsync(
        SqliteConnection conn, SqliteTransaction tx, IEnumerable<string> relativeRoots, CancellationToken ct)
    {
        int nodes = 0, edges = 0;
        foreach (var root in relativeRoots.Select(r => r.Replace('\\', '/').Trim('/')).Where(r => r.Length > 0).Distinct())
        {
            // substr en lugar de LIKE: una ruta con '_' o '%' no debe actuar como comodín.
            var (n, e) = await RemoveNodesAsync(
                conn, tx,
                "SELECT id FROM nodes WHERE file_path = @root OR substr(file_path, 1, length(@root) + 1) = @root || '/'",
                p => p.AddWithValue("@root", root), ct);
            nodes += n;
            edges += e;
        }
        return (nodes, edges);
    }

    /// <summary>
    /// Borra los nodos que devuelve <paramref name="selectIdsSql"/> (una consulta que devuelve una columna de ids), con
    /// sus aristas en los dos sentidos y sus vectores. Devuelve nodos y aristas borrados.
    /// </summary>
    public static async Task<(int Nodes, int Edges)> RemoveNodesAsync(
        SqliteConnection conn, SqliteTransaction tx, string selectIdsSql, Action<SqliteParameterCollection>? bind, CancellationToken ct)
    {
        await ExecuteAsync(conn, tx, "CREATE TEMP TABLE IF NOT EXISTS enrichment_doomed(id INTEGER PRIMARY KEY)", ct);
        await ExecuteAsync(conn, tx, "DELETE FROM temp.enrichment_doomed", ct);

        using (var fill = conn.CreateCommand())
        {
            fill.Transaction = tx;
            fill.CommandText = $"INSERT OR IGNORE INTO temp.enrichment_doomed(id) {selectIdsSql}";
            bind?.Invoke(fill.Parameters);
            await fill.ExecuteNonQueryAsync(ct);
        }

        var edges = await ExecuteAsync(conn, tx, """
            DELETE FROM edges
            WHERE source_id IN (SELECT id FROM temp.enrichment_doomed)
               OR target_id IN (SELECT id FROM temp.enrichment_doomed)
            """, ct);
        if (await EnrichmentRuns.TableExistsAsync(conn, "node_vectors", ct, tx))
            await ExecuteAsync(conn, tx, "DELETE FROM node_vectors WHERE node_id IN (SELECT id FROM temp.enrichment_doomed)", ct);
        var nodes = await ExecuteAsync(conn, tx, "DELETE FROM nodes WHERE id IN (SELECT id FROM temp.enrichment_doomed)", ct);
        await ExecuteAsync(conn, tx, "DELETE FROM temp.enrichment_doomed", ct);
        return (nodes, edges);
    }

    /// <summary>
    /// Objeto de propiedades de un nodo de cbm. Si no es un objeto JSON válido se conserva entero bajo
    /// <see cref="RawPropertiesKey"/>, como hace <c>json_patch(json_object('cbm_raw_properties', properties), …)</c>.
    /// </summary>
    public static JsonObject ParseProperties(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];
        try
        {
            if (JsonNode.Parse(raw) is JsonObject obj)
                return obj;
        }
        catch (JsonException)
        {
        }
        return new JsonObject { [RawPropertiesKey] = raw };
    }

    /// <summary>
    /// Completa las propiedades con <paramref name="patch"/> y añade el enriquecedor a <c>enriched_by</c> sin quitar los
    /// de otros enriquecedores. Un valor null del parche no borra lo que hubiera: el enriquecedor no sabía más que cbm.
    /// </summary>
    public static JsonObject Merge(string? raw, JsonObject patch, string enricher)
    {
        var properties = ParseProperties(raw);
        foreach (var (key, value) in patch)
        {
            if (value != null)
                properties[key] = value.DeepClone();
        }
        AddMark(properties, enricher);
        return properties;
    }

    /// <summary>Añade el enriquecedor a <c>enriched_by</c> (lista sin repetidos).</summary>
    public static void AddMark(JsonObject properties, string enricher)
    {
        var marks = new List<string>();
        switch (properties["enriched_by"])
        {
            case JsonArray array:
                marks.AddRange(array.Select(v => v?.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null).OfType<string>());
                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                marks.Add(value.GetValue<string>());
                break;
        }
        if (!marks.Contains(enricher, StringComparer.Ordinal))
            marks.Add(enricher);
        properties["enriched_by"] = new JsonArray(marks.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray());
    }

    /// <summary>Propiedades de un nodo creado por el enriquecedor, más las propias del nodo.</summary>
    public static JsonObject CreatedNodeProperties(string enricher, DateTimeOffset enrichedAt, JsonObject? properties = null)
    {
        var result = new JsonObject();
        if (properties != null)
        {
            foreach (var (key, value) in properties)
            {
                if (value != null)
                    result[key] = value.DeepClone();
            }
        }
        result["origin"] = OriginEnricher;
        result["enriched_by"] = new JsonArray(JsonValue.Create(enricher));
        result["enriched_at"] = enrichedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        return result;
    }

    /// <summary>Propiedades de una arista escrita por el enriquecedor: la marca primero, después las propias.</summary>
    public static JsonObject EdgeProperties(string enricher, JsonObject? properties = null)
    {
        var result = new JsonObject { ["enriched_by"] = new JsonArray(JsonValue.Create(enricher)) };
        if (properties != null)
        {
            foreach (var (key, value) in properties)
            {
                if (value != null && key != "enriched_by")
                    result[key] = value.DeepClone();
            }
        }
        return result;
    }

    private static async Task<int> ExecuteAsync(SqliteConnection conn, SqliteTransaction tx, string sql, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
