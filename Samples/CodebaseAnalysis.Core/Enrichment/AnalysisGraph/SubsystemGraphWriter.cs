using System.Globalization;
using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Preparation;

namespace CodebaseAnalysis.Core.Enrichment.AnalysisGraph;

/// <summary>Recuentos de <see cref="SubsystemGraphWriter.WriteAsync"/>.</summary>
public sealed record SubsystemGraphStats
{
    public int Subsystems { get; init; }

    /// <summary>Aristas <c>CONTAINS</c> hacia tipos, JSP y definiciones de sus ficheros.</summary>
    public int ContainsMembers { get; init; }

    /// <summary>Aristas <c>CONTAINS</c> hacia rutas que atiende una clase del subsistema (<c>HANDLES</c>).</summary>
    public int ContainsRoutes { get; init; }

    /// <summary>Aristas <c>CONTAINS</c> hacia el nodo <c>File</c> de un fichero sin tipos ni JSP en el grafo.</summary>
    public int ContainsFiles { get; init; }

    public int DependsOn { get; init; }

    /// <summary>Notas de investigación sin <c>ABOUT</c> que se han enlazado con su subsistema.</summary>
    public int NotesRelinked { get; init; }

    public int PreviousNodesRemoved { get; init; }
    public int PreviousEdgesRemoved { get; init; }
}

/// <summary>
/// E1 (plan 2026-10-06 §4.2): escribe el mapa de dominios en la copia del grafo. Un nodo <c>Subsystem</c> por subsistema (id, nombre,
/// descripción, LOC, ficheros, término dominante, raíces, tipos clave, entradas y tablas en <c>properties</c>), aristas <c>CONTAINS</c>
/// hacia los tipos, JSP y definiciones Tiles de sus ficheros, hacia las rutas que atienden sus clases y, si un fichero no tiene
/// ninguno de esos nodos, hacia su nodo <c>File</c>; y <c>DEPENDS_ON</c> entre subsistemas.
/// </summary>
/// <remarks>
/// <para><b>Dependencias</b>: el mapa no las trae, así que se derivan del código. Hay <c>DEPENDS_ON</c> de A a B si alguna arista de
/// código (<see cref="DependencyEdgeTypes"/>) sale de un nodo de un fichero de A y llega a un nodo de un fichero de B. <c>weight</c> es
/// el número de esas aristas y <c>via</c> su desglose por tipo; no se filtra por peso: la vista del chat las ordena de mayor a menor.
/// Las aristas heurísticas de cbm cuentan igual que las resueltas, de modo que el peso es una señal de acoplamiento y no una prueba.</para>
/// <para><b>Idempotente</b>, en una transacción: sustituye las <c>CONTAINS</c> y <c>DEPENDS_ON</c> de la pasada anterior, reescribe con
/// el mismo id el subsistema que sigue en el mapa (su id es estable entre ejecuciones) y borra los que ya no están. Las notas de
/// investigación (E3) cuyo subsistema no tenía nodo se enlazan (<c>ABOUT</c>) por su <c>subsystem_id</c>.</para>
/// </remarks>
public static class SubsystemGraphWriter
{
    /// <summary>Entradas que se guardan en las propiedades de cada subsistema (el total va en <c>entry_points_total</c>).</summary>
    public const int MaxEntryPointsInProperties = 100;

    /// <summary>Labels de los miembros a los que apunta <c>CONTAINS</c>: tipos, JSP y definiciones Tiles.</summary>
    public static IReadOnlyList<string> MemberLabels { get; } =
        [.. TypeGraphBuilder.TypeLabels, "Jsp", "TilesDefinition"];

    /// <summary>Aristas de código que cuentan como dependencia entre subsistemas.</summary>
    public static IReadOnlyList<string> DependencyEdgeTypes { get; } =
    [
        "CALLS", "USAGE", "USES_TYPE", "READS", "WRITES", "INHERITS", "IMPLEMENTS", "OVERRIDES", "THROWS",
        "FORWARDS_TO", "SUBMITS_TO", "INCLUDES", "RENDERS"
    ];

    private const string FileExpr = "replace(coalesce(nullif(n.file_path, ''), n.file, ''), '\\', '/')";

    /// <exception cref="InvalidOperationException">Si la BD es la de cbm.</exception>
    public static async Task<SubsystemGraphStats> WriteAsync(string databasePath, DomainMap map, CancellationToken ct, DateTimeOffset? at = null)
    {
        var when = at ?? DateTimeOffset.UtcNow;
        using var store = await AnalysisGraphStore.OpenAsync(databasePath, ct);
        var previous = await store.LoadCreatedAsync(AnalysisGraphLabels.SubsystemEnricher, AnalysisGraphLabels.Subsystem, ct);
        var previousEdges = await store.RemoveMarkedEdgesAsync(
            AnalysisGraphLabels.SubsystemEnricher, [AnalysisGraphLabels.Contains, AnalysisGraphLabels.DependsOn], ct);

        await store.ExecuteAsync("""
            CREATE TEMP TABLE IF NOT EXISTS subsystem_files(path TEXT PRIMARY KEY COLLATE NOCASE, subsystem_id TEXT NOT NULL);
            CREATE TEMP TABLE IF NOT EXISTS subsystem_nodes(subsystem_id TEXT PRIMARY KEY, node_id INTEGER NOT NULL);
            DELETE FROM temp.subsystem_files;
            DELETE FROM temp.subsystem_nodes;
            """, ct);

        var nodeIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var subsystem in map.Subsystems)
        {
            var id = await store.UpsertNodeAsync(previous, AnalysisGraphLabels.Subsystem, subsystem.Name,
                AnalysisGraphLabels.SubsystemQualifiedName(subsystem.Id), AnalysisGraphLabels.SubsystemEnricher, when, Properties(map, subsystem), ct);
            nodeIds[subsystem.Id] = id;
            await store.ExecuteAsync("INSERT INTO temp.subsystem_nodes(subsystem_id, node_id) VALUES (@s, @n)", ct, p =>
            {
                p.AddWithValue("@s", subsystem.Id);
                p.AddWithValue("@n", id);
            });

            using var insert = store.Command("INSERT OR IGNORE INTO temp.subsystem_files(path, subsystem_id) VALUES (@p, @s)");
            var path = insert.Parameters.Add("@p", Microsoft.Data.Sqlite.SqliteType.Text);
            insert.Parameters.AddWithValue("@s", subsystem.Id);
            foreach (var file in subsystem.Files.Select(f => f.Replace('\\', '/')).Where(f => f.Length > 0))
            {
                path.Value = file;
                await insert.ExecuteNonQueryAsync(ct);
            }
        }

        var mark = $"json_array('{AnalysisGraphLabels.SubsystemEnricher}')";
        var labels = string.Join(", ", MemberLabels.Select(l => $"'{l}'"));

        // Tipos, JSP y definiciones de los ficheros de cada subsistema.
        var members = await store.ExecuteAsync($"""
            INSERT OR IGNORE INTO edges (project, project_id, source_id, target_id, type, properties)
            SELECT @project, 1, s.node_id, n.id, '{AnalysisGraphLabels.Contains}', json_object('enriched_by', {mark}, 'member', 'code')
            FROM nodes n
            JOIN temp.subsystem_files f ON f.path = {FileExpr}
            JOIN temp.subsystem_nodes s ON s.subsystem_id = f.subsystem_id
            WHERE n.label IN ({labels})
            """, ct, p => p.AddWithValue("@project", store.Project));

        // Las rutas que atiende una clase o un método del subsistema.
        var routes = await store.ExecuteAsync($"""
            INSERT OR IGNORE INTO edges (project, project_id, source_id, target_id, type, properties)
            SELECT DISTINCT @project, 1, s.node_id, r.id, '{AnalysisGraphLabels.Contains}', json_object('enriched_by', {mark}, 'member', 'route')
            FROM edges h
            JOIN nodes n ON n.id = h.source_id
            JOIN nodes r ON r.id = h.target_id AND r.label = 'Route'
            JOIN temp.subsystem_files f ON f.path = {FileExpr}
            JOIN temp.subsystem_nodes s ON s.subsystem_id = f.subsystem_id
            WHERE h.type = 'HANDLES'
            """, ct, p => p.AddWithValue("@project", store.Project));

        // Un fichero sin tipos ni JSP en el grafo (un script, una configuración indexada) queda representado por su nodo File.
        var files = await store.ExecuteAsync($"""
            INSERT OR IGNORE INTO edges (project, project_id, source_id, target_id, type, properties)
            SELECT @project, 1, s.node_id, n.id, '{AnalysisGraphLabels.Contains}', json_object('enriched_by', {mark}, 'member', 'file')
            FROM nodes n
            JOIN temp.subsystem_files f ON f.path = {FileExpr}
            JOIN temp.subsystem_nodes s ON s.subsystem_id = f.subsystem_id
            WHERE n.label = 'File'
              AND NOT EXISTS (SELECT 1 FROM nodes m WHERE m.label IN ({labels})
                              AND replace(coalesce(nullif(m.file_path, ''), m.file, ''), '\', '/') = f.path COLLATE NOCASE)
            """, ct, p => p.AddWithValue("@project", store.Project));

        var dependsOn = await WriteDependenciesAsync(store, nodeIds, ct);
        var (previousNodes, staleEdges) = await store.RemoveStaleAsync(previous, nodeIds.Values.ToHashSet(), ct);
        var relinked = await RelinkNotesAsync(store, nodeIds, ct);

        await store.ExecuteAsync("DELETE FROM temp.subsystem_files; DELETE FROM temp.subsystem_nodes;", ct);
        store.Commit();
        return new SubsystemGraphStats
        {
            Subsystems = nodeIds.Count,
            ContainsMembers = members,
            ContainsRoutes = routes,
            ContainsFiles = files,
            DependsOn = dependsOn,
            NotesRelinked = relinked,
            PreviousNodesRemoved = previousNodes,
            PreviousEdgesRemoved = previousEdges + staleEdges
        };
    }

    private static JsonObject Properties(DomainMap map, Subsystem subsystem)
    {
        var entryPoints = subsystem.EntryPoints
            .Select(e => e.File.Length == 0 ? $"{e.Kind} {e.Name}" : $"{e.Kind} {e.Name} · {e.File}{(e.Line > 0 ? ":" + e.Line.ToString(CultureInfo.InvariantCulture) : "")}")
            .Take(MaxEntryPointsInProperties);
        return new JsonObject
        {
            ["subsystem_id"] = subsystem.Id,
            ["description"] = subsystem.Description,
            ["kind"] = subsystem.Kind,
            ["name_origin"] = subsystem.NameOrigin,
            ["priority"] = subsystem.Priority.ToString().ToLowerInvariant(),
            ["source_set"] = subsystem.SourceSet,
            ["roots"] = AnalysisGraphStore.ToArray(subsystem.Roots),
            ["dominant_term"] = subsystem.DominantTerm,
            ["loc"] = subsystem.Metrics.Loc,
            ["files"] = subsystem.Files.Count,
            ["key_types"] = AnalysisGraphStore.ToArray(subsystem.KeyTypes),
            ["entry_points"] = AnalysisGraphStore.ToArray(entryPoints),
            ["entry_points_total"] = subsystem.EntryPoints.Count,
            ["tables"] = AnalysisGraphStore.ToArray(subsystem.Tables),
            ["centrality"] = subsystem.Metrics.Centrality,
            ["map_basis"] = map.Basis,
            ["map_generated_at"] = map.GeneratedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            ["graph_fingerprint"] = map.GraphFingerprint
        };
    }

    private static async Task<int> WriteDependenciesAsync(AnalysisGraphStore store, Dictionary<string, long> nodeIds, CancellationToken ct)
    {
        var types = string.Join(", ", DependencyEdgeTypes.Select(t => $"'{t}'"));
        var counts = new Dictionary<(string From, string To), SortedDictionary<string, long>>();
        using (var cmd = store.Command($"""
            SELECT fa.subsystem_id, fb.subsystem_id, e.type, COUNT(1)
            FROM edges e
            JOIN nodes n ON n.id = e.source_id
            JOIN temp.subsystem_files fa ON fa.path = {FileExpr}
            JOIN nodes b ON b.id = e.target_id
            JOIN temp.subsystem_files fb ON fb.path = replace(coalesce(nullif(b.file_path, ''), b.file, ''), '\', '/')
            WHERE e.type IN ({types}) AND fa.subsystem_id <> fb.subsystem_id
            GROUP BY fa.subsystem_id, fb.subsystem_id, e.type
            """))
        {
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!counts.TryGetValue(key, out var via))
                    counts[key] = via = new SortedDictionary<string, long>(StringComparer.Ordinal);
                via[reader.GetString(2)] = reader.GetInt64(3);
            }
        }

        var written = 0;
        foreach (var ((from, to), via) in counts.OrderBy(c => c.Key.From, StringComparer.Ordinal).ThenBy(c => c.Key.To, StringComparer.Ordinal))
        {
            if (!nodeIds.TryGetValue(from, out var source) || !nodeIds.TryGetValue(to, out var target))
                continue;
            var viaObject = new JsonObject();
            foreach (var (type, count) in via)
                viaObject[type] = count;
            if (await store.InsertEdgeAsync(source, target, AnalysisGraphLabels.DependsOn, AnalysisGraphLabels.SubsystemEnricher,
                    new JsonObject { ["weight"] = via.Values.Sum(), ["via"] = viaObject }, ct))
                written++;
        }
        return written;
    }

    private static async Task<int> RelinkNotesAsync(AnalysisGraphStore store, Dictionary<string, long> nodeIds, CancellationToken ct)
    {
        var notes = new List<(long Id, string SubsystemId)>();
        using (var cmd = store.Command(
                   "SELECT id, CASE WHEN json_valid(properties) THEN json_extract(properties, '$.subsystem_id') END FROM nodes WHERE label = @label"))
        {
            cmd.Parameters.AddWithValue("@label", AnalysisGraphLabels.InvestigationNote);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(1))
                    notes.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        var relinked = 0;
        foreach (var (noteId, subsystemId) in notes)
        {
            if (nodeIds.TryGetValue(subsystemId, out var subsystemNode)
                && await store.InsertEdgeAsync(noteId, subsystemNode, AnalysisGraphLabels.About, AnalysisGraphLabels.NoteEnricher, null, ct))
                relinked++;
        }
        return relinked;
    }
}
