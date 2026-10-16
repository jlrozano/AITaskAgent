using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Support;

namespace CodebaseAnalysis.Core.Enrichment.AnalysisGraph;

/// <summary>Recuentos de <see cref="SqlTableGraphWriter.WriteAsync"/>.</summary>
public sealed record SqlTableGraphStats
{
    /// <summary>Nodos <c>Table</c> propios escritos (creados o reescritos).</summary>
    public int TablesCreated { get; init; }

    /// <summary>Tablas que ya tenían un nodo <c>Table</c>/<c>AccessTable</c> de otro indexador con el mismo nombre: se enlazan a él.</summary>
    public int TablesReused { get; init; }

    public int QueriesEdges { get; init; }
    public int ModifiesEdges { get; init; }

    /// <summary>Referencias (tabla nombrada por una sentencia) recibidas.</summary>
    public int References { get; init; }

    /// <summary>Referencias ancladas en el nodo que contiene su línea.</summary>
    public int ReferencesResolved { get; init; }

    /// <summary>Referencias sin nodo que contenga la línea, ancladas en el nodo del fichero.</summary>
    public int ReferencesFileLevel { get; init; }

    /// <summary>Referencias de un fichero sin nodos en el grafo: no tienen origen y no dejan arista.</summary>
    public int ReferencesUnresolved { get; init; }

    public int PreviousNodesRemoved { get; init; }
    public int PreviousEdgesRemoved { get; init; }
}

/// <summary>
/// E2 (plan 2026-10-06 §4.1): escribe en la copia del grafo las tablas que nombra el SQL escrito en cadenas
/// (<see cref="SqlTableScanner"/>). Un nodo <c>Table</c> por nombre normalizado (<see cref="AnalysisGraphLabels.NormalizeTable"/>; si
/// otro indexador ya dejó un <c>Table</c> o <c>AccessTable</c> con ese nombre, se usa el suyo) y, desde el nodo de código más específico
/// que contiene la línea del literal (<see cref="CodeNodeLocator"/>), una arista <c>MODIFIES</c> si la sentencia escribe en la tabla
/// (<see cref="SqlTableReference.Writes"/>) o <c>QUERIES</c> si la lee. La arista agrega todas las sentencias del mismo nodo sobre la
/// misma tabla: <c>file</c>, <c>line</c> (la primera), <c>lines</c>, <c>statements</c> y <c>count</c>.
/// </summary>
/// <remarks>
/// Idempotente, en una transacción: las aristas de la pasada anterior se sustituyen; una tabla que sigue nombrada conserva su nodo (y
/// con él las <c>DOC_DESCRIBES</c> de los documentos que la mencionan) con las propiedades nuevas, y las que ya no se nombran se borran.
/// </remarks>
public static class SqlTableGraphWriter
{
    private sealed class EdgeAggregate(string file)
    {
        public string File { get; } = file;
        public SortedSet<int> Lines { get; } = [];
        public SortedSet<string> Statements { get; } = new(StringComparer.Ordinal);
        public int Count { get; set; }
        public bool FileLevel { get; set; }
    }

    /// <exception cref="InvalidOperationException">Si la BD es la de cbm.</exception>
    public static async Task<SqlTableGraphStats> WriteAsync(
        string databasePath, IReadOnlyList<SqlTableReference> references, CancellationToken ct, DateTimeOffset? at = null)
    {
        var when = at ?? DateTimeOffset.UtcNow;
        using var store = await AnalysisGraphStore.OpenAsync(databasePath, ct);
        var previous = await store.LoadCreatedAsync(AnalysisGraphLabels.SqlTableEnricher, AnalysisGraphLabels.Table, ct);
        var previousEdges = await store.RemoveMarkedEdgesAsync(
            AnalysisGraphLabels.SqlTableEnricher, [AnalysisGraphLabels.Queries, AnalysisGraphLabels.Modifies], ct);

        // Las tablas que ya tiene el grafo por otro indexador (cbm con un dialecto que las reconoce, la ingesta de Access).
        var ownIds = previous.Values.ToHashSet();
        var existing = new Dictionary<string, long>(StringComparer.Ordinal);
        using (var cmd = store.Command("SELECT id, name FROM nodes WHERE label IN ('Table', 'AccessTable') ORDER BY id"))
        {
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!ownIds.Contains(reader.GetInt64(0)))
                    existing.TryAdd(AnalysisGraphLabels.NormalizeTable(reader.GetString(1)), reader.GetInt64(0));
            }
        }

        var tableIds = new Dictionary<string, long>(StringComparer.Ordinal);
        int created = 0, reused = 0;
        foreach (var group in references.GroupBy(r => AnalysisGraphLabels.NormalizeTable(r.Name), StringComparer.Ordinal)
                     .Where(g => g.Key.Length > 0).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (existing.TryGetValue(group.Key, out var known))
            {
                tableIds[group.Key] = known;
                reused++;
                continue;
            }
            tableIds[group.Key] = await store.UpsertNodeAsync(previous, AnalysisGraphLabels.Table, group.Key, AnalysisGraphLabels.TableQualifiedName(group.Key),
                AnalysisGraphLabels.SqlTableEnricher, when, new JsonObject
                {
                    ["source"] = "sql_literal",
                    ["schemas"] = AnalysisGraphStore.ToArray(group.Select(r => r.Schema).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)),
                    ["spellings"] = AnalysisGraphStore.ToArray(group.Select(r => r.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                    ["references"] = group.Count(),
                    ["files"] = group.Select(r => r.File).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    ["writes"] = group.Count(r => r.Writes)
                }, ct);
            created++;
        }

        var locator = await CodeNodeLocator.LoadAsync(store.Connection, store.Transaction, references.Select(r => r.File).Distinct(StringComparer.OrdinalIgnoreCase), ct);
        var edges = new Dictionary<(long Source, long Target, string Type), EdgeAggregate>();
        int resolved = 0, fileLevel = 0, unresolved = 0;
        foreach (var reference in references)
        {
            if (!tableIds.TryGetValue(AnalysisGraphLabels.NormalizeTable(reference.Name), out var tableId))
                continue;
            if (locator.Locate(reference.File, reference.Line) is not { } node)
            {
                unresolved++;
                continue;
            }
            if (node.FileLevel)
                fileLevel++;
            else
                resolved++;

            var type = reference.Writes ? AnalysisGraphLabels.Modifies : AnalysisGraphLabels.Queries;
            if (!edges.TryGetValue((node.Id, tableId, type), out var aggregate))
                edges[(node.Id, tableId, type)] = aggregate = new EdgeAggregate(reference.File.Replace('\\', '/'));
            aggregate.Lines.Add(reference.Line);
            aggregate.Statements.Add(reference.Statement);
            aggregate.Count++;
            aggregate.FileLevel |= node.FileLevel;
        }

        int queries = 0, modifies = 0;
        foreach (var ((source, target, type), aggregate) in edges)
        {
            var properties = new JsonObject
            {
                ["file"] = aggregate.File,
                ["line"] = aggregate.Lines.Min,
                ["lines"] = AnalysisGraphStore.ToArray(aggregate.Lines),
                ["statements"] = AnalysisGraphStore.ToArray(aggregate.Statements),
                ["count"] = aggregate.Count
            };
            if (aggregate.FileLevel)
                properties["anchored_to"] = "file";
            if (!await store.InsertEdgeAsync(source, target, type, AnalysisGraphLabels.SqlTableEnricher, properties, ct))
                continue;
            if (type == AnalysisGraphLabels.Modifies)
                modifies++;
            else
                queries++;
        }

        var (previousNodes, staleEdges) = await store.RemoveStaleAsync(previous, tableIds.Values.ToHashSet(), ct);
        store.Commit();
        return new SqlTableGraphStats
        {
            TablesCreated = created,
            TablesReused = reused,
            QueriesEdges = queries,
            ModifiesEdges = modifies,
            References = references.Count,
            ReferencesResolved = resolved,
            ReferencesFileLevel = fileLevel,
            ReferencesUnresolved = unresolved,
            PreviousNodesRemoved = previousNodes,
            PreviousEdgesRemoved = previousEdges + staleEdges
        };
    }
}
