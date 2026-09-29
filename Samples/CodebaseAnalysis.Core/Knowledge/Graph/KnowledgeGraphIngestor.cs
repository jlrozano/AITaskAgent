using CodebaseAnalysis.Core.Documents.Extraction;
using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysis.Core.Support;
using Microsoft.Data.Sqlite;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Knowledge.Graph;

/// <summary>Etiquetas, aristas y nombres de los nodos de conocimiento (plan v2 §8.1.1).</summary>
public static class KnowledgeGraphLabels
{
    public const string UserDocument = "UserDocument";
    public const string UserDocSection = "UserDocSection";
    public const string HasSection = "HAS_SECTION";
    public const string DocDescribes = "DOC_DESCRIBES";
    public const string Origin = "user_document";
    public const string QualifiedNamePrefix = "userdoc.";

    public static string DocumentQualifiedName(SourceRef source) => QualifiedNamePrefix + source.Key;

    public static string SectionQualifiedName(SourceRef source, int ordinal) => $"{DocumentQualifiedName(source)}#{ordinal:D4}";
}

/// <summary>
/// Añade y quita fuentes de conocimiento en el grafo (plan v2 §8.1): un nodo <c>UserDocument</c> por fuente, un
/// <c>UserDocSection</c> por sección con <c>HAS_SECTION</c>, y aristas <c>DOC_DESCRIBES</c> a los nodos de código
/// mencionados. Añadir es una transacción que borra lo que hubiera de esa clave e inserta de nuevo, así que es
/// idempotente. Todas las escrituras de conocimiento del proceso pasan por <see cref="WriteGate"/>.
/// </summary>
public static class KnowledgeGraphIngestor
{
    /// <summary>Serializa todas las escrituras de conocimiento del proceso (grafo y embeddings), plan v2 §5.5.</summary>
    public static SemaphoreSlim WriteGate { get; } = new(1, 1);

    private static readonly JsonSerializerOptions PropsJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>True si la BD existe y tiene nodos.</summary>
    public static async Task<bool> HasGraphAsync(string? databasePath, CancellationToken ct) =>
        databasePath != null && await GraphStats.TryCountAsync(databasePath, ct) is { Nodes: > 0 };

    /// <summary>Añade (o reemplaza) la fuente en el grafo y la apunta en el registro.</summary>
    /// <exception cref="KnowledgeOperationException"><c>no_graph</c> o <c>not_found</c>.</exception>
    public static async Task<GraphKnowledgeState> AddAsync(string databasePath, KnowledgePaths paths, SourceRef source, CancellationToken ct)
    {
        if (!await HasGraphAsync(databasePath, ct))
            throw new KnowledgeOperationException(KnowledgeErrorCodes.NoGraph, "No hay grafo: ejecuta el análisis antes de añadir documentación al grafo.");

        await WriteGate.WaitAsync(ct);
        try
        {
            // Se lee dentro del cerrojo: una baja concurrente (borrar el documento o el propio) ya no deja la fuente
            // escrita en el grafo después de quitarla.
            var content = KnowledgeSourceReader.TryRead(paths, source)
                ?? throw new KnowledgeOperationException(KnowledgeErrorCodes.NotFound, $"No existe la fuente '{source.Key}' o no tiene texto.");
            await WriteAsync(databasePath, content, ct);
            new GraphSourceRegistry(paths).Add(source);
        }
        finally
        {
            WriteGate.Release();
        }
        return await GetStateAsync(databasePath, paths, source, ct);
    }

    /// <summary>Quita la fuente del grafo y del registro.</summary>
    public static async Task<GraphKnowledgeState> RemoveAsync(string? databasePath, KnowledgePaths paths, SourceRef source, CancellationToken ct)
    {
        await WriteGate.WaitAsync(ct);
        try
        {
            if (databasePath != null && File.Exists(databasePath))
            {
                await using var conn = await OpenAsync(databasePath, ct);
                using var tx = conn.BeginTransaction();
                await DeleteSourceAsync(conn, tx, source, ct);
                await tx.CommitAsync(ct);
            }
            new GraphSourceRegistry(paths).Remove(source);
        }
        finally
        {
            WriteGate.Release();
        }
        return await GetStateAsync(databasePath, paths, source, ct);
    }

    /// <summary>
    /// Estado (plan v2 §8.1.3): <c>present</c> si el nodo existe con el SHA actual; <c>stale</c> si con otro;
    /// <c>pending</c> si está en el registro y no en el grafo; <c>absent</c> en otro caso.
    /// </summary>
    public static async Task<GraphKnowledgeState> GetStateAsync(string? databasePath, KnowledgePaths paths, SourceRef source, CancellationToken ct) =>
        (await GetStatesAsync(databasePath, paths, [source], ct))[0];

    /// <summary>
    /// <see cref="GetStateAsync"/> de varias fuentes con una sola conexión y una sola lectura del registro, en el orden
    /// de <paramref name="sources"/>: los listados lo piden para todas sus filas.
    /// </summary>
    public static async Task<IReadOnlyList<GraphKnowledgeState>> GetStatesAsync(string? databasePath, KnowledgePaths paths, IReadOnlyList<SourceRef> sources, CancellationToken ct)
    {
        var keys = new GraphSourceRegistry(paths).Sources().Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var registered = sources.Select(s => keys.Contains(s.Key)).ToList();
        GraphKnowledgeState Missing(int i) => registered[i] ? new GraphKnowledgeState(GraphKnowledgeStates.Pending, 0, 0, null) : GraphKnowledgeState.Absent;

        if (sources.Count == 0 || databasePath == null || !File.Exists(databasePath))
            return sources.Select((_, i) => Missing(i)).ToList();

        try
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync(ct);
            var result = new List<GraphKnowledgeState>(sources.Count);
            for (var i = 0; i < sources.Count; i++)
                result.Add(await QueryStateAsync(conn, paths, sources[i], ct) ?? Missing(i));
            return result;
        }
        catch (SqliteException)
        {
            return sources.Select((_, i) => Missing(i)).ToList();
        }
    }

    /// <summary>Estado de una fuente con el nodo en el grafo; null si no tiene nodo.</summary>
    private static async Task<GraphKnowledgeState?> QueryStateAsync(SqliteConnection conn, KnowledgePaths paths, SourceRef source, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT json_extract(properties, '$.content_sha256'), json_extract(properties, '$.added_at'),
                   (SELECT COUNT(1) FROM nodes WHERE qualified_name = @qn OR qualified_name LIKE @like ESCAPE '!'),
                   (SELECT COUNT(1) FROM edges WHERE source_id IN (SELECT id FROM nodes WHERE qualified_name = @qn OR qualified_name LIKE @like ESCAPE '!'))
            FROM nodes WHERE qualified_name = @qn LIMIT 1;";
        cmd.Parameters.AddWithValue("@qn", KnowledgeGraphLabels.DocumentQualifiedName(source));
        cmd.Parameters.AddWithValue("@like", Escape(KnowledgeGraphLabels.DocumentQualifiedName(source)) + "#%");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var sha = reader.IsDBNull(0) ? null : reader.GetString(0);
        DateTimeOffset? addedAt = !reader.IsDBNull(1) && DateTimeOffset.TryParse(reader.GetString(1), out var at) ? at : null;
        var state = sha != null && sha == KnowledgeSourceReader.CurrentSha(paths, source) ? GraphKnowledgeStates.Present : GraphKnowledgeStates.Stale;
        return new GraphKnowledgeState(state, reader.GetInt32(2), reader.GetInt32(3), addedAt);
    }

    /// <summary>
    /// Reincorpora las fuentes del registro de los tipos pedidos cuyo contenido existe (plan v2 §8.1.4). No toca el
    /// registro. Devuelve cuántas se reincorporaron.
    /// </summary>
    public static async Task<int> ReincorporateAsync(string databasePath, KnowledgePaths paths, IReadOnlySet<SourceKind> kinds, CancellationToken ct)
    {
        var sources = new GraphSourceRegistry(paths).Sources().Where(s => kinds.Contains(s.Kind)).ToList();
        if (sources.Count == 0 || !File.Exists(databasePath))
            return 0;

        var count = 0;
        await WriteGate.WaitAsync(ct);
        try
        {
            foreach (var source in sources)
            {
                if (KnowledgeSourceReader.TryRead(paths, source) is { } content)
                {
                    await WriteAsync(databasePath, content, ct);
                    count++;
                }
            }
        }
        finally
        {
            WriteGate.Release();
        }
        return count;
    }

    /// <summary>Quita del grafo (no del registro) las fuentes de un tipo: los informes del catálogo antes de auditar.</summary>
    public static async Task<int> RetireAsync(string databasePath, KnowledgePaths paths, SourceKind kind, CancellationToken ct)
    {
        var sources = new GraphSourceRegistry(paths).Sources().Where(s => s.Kind == kind).ToList();
        if (sources.Count == 0 || !File.Exists(databasePath))
            return 0;

        await WriteGate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(databasePath, ct);
            using var tx = conn.BeginTransaction();
            var removed = 0;
            foreach (var source in sources)
                removed += await DeleteSourceAsync(conn, tx, source, ct);
            await tx.CommitAsync(ct);
            return removed;
        }
        finally
        {
            WriteGate.Release();
        }
    }

    /// <summary>Símbolos de código enlazados por <c>DOC_DESCRIBES</c> desde una fuente (para la faceta de los auditores).</summary>
    public static async Task<IReadOnlyList<string>> LinkedSymbolsAsync(SqliteConnection conn, SourceRef source, int max, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT t.qualified_name FROM edges e
            JOIN nodes s ON s.id = e.source_id JOIN nodes t ON t.id = e.target_id
            WHERE e.type = 'DOC_DESCRIBES' AND s.qualified_name LIKE @like ESCAPE '!'
            ORDER BY t.qualified_name LIMIT @max;";
        cmd.Parameters.AddWithValue("@like", Escape(KnowledgeGraphLabels.DocumentQualifiedName(source)) + "#%");
        cmd.Parameters.AddWithValue("@max", max);
        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

    private static async Task WriteAsync(string databasePath, KnowledgeSourceContent content, CancellationToken ct)
    {
        await GraphSchema.EnsureAsync(databasePath, ct);
        await using var conn = await OpenAsync(databasePath, ct);
        using var tx = conn.BeginTransaction();
        await DeleteSourceAsync(conn, tx, content.Source, ct);

        var project = await GraphProject.ResolveAsync(conn, "Codebase", ct, tx);
        var resolver = await MentionResolver.LoadAsync(conn, tx, ct);
        var lineCount = Math.Max(1, content.Content.Split('\n').Length);
        var docId = await InsertNodeAsync(conn, tx, project, KnowledgeGraphLabels.UserDocument, content.Title,
            KnowledgeGraphLabels.DocumentQualifiedName(content.Source), 1, lineCount, new Dictionary<string, object?>
            {
                ["origin"] = KnowledgeGraphLabels.Origin,
                ["source_kind"] = content.Source.KindName,
                ["source_id"] = content.Source.Id,
                ["media_type"] = content.MediaType,
                ["content_sha256"] = content.Sha256,
                ["section_count"] = content.Sections.Count,
                ["added_at"] = DateTimeOffset.UtcNow.ToString("o"),
                ["content_path"] = content.ContentRelativePath
            }, ct);

        var edges = 0;
        foreach (var section in content.Sections)
        {
            var text = MarkdownSections.Slice(content.Content, section);
            var sectionId = await InsertNodeAsync(conn, tx, project, KnowledgeGraphLabels.UserDocSection, section.Heading,
                KnowledgeGraphLabels.SectionQualifiedName(content.Source, section.Ordinal), section.StartLine, section.EndLine,
                new Dictionary<string, object?>
                {
                    ["origin"] = KnowledgeGraphLabels.Origin,
                    ["source_key"] = content.Source.Key,
                    ["level"] = section.Level,
                    ["char_count"] = text.Length,
                    ["docstring"] = text.Length > 400 ? text[..400] : text
                }, ct);
            await InsertEdgeAsync(conn, tx, project, docId, sectionId, KnowledgeGraphLabels.HasSection,
                new Dictionary<string, object?> { ["ordinal"] = section.Ordinal }, ct);

            foreach (var mention in resolver.Resolve(text))
            {
                if (edges >= MentionResolver.MaxEdgesPerSource)
                    break;
                await InsertEdgeAsync(conn, tx, project, sectionId, mention.TargetId, KnowledgeGraphLabels.DocDescribes,
                    new Dictionary<string, object?>
                    {
                        ["mention_type"] = mention.MentionType,
                        ["mention"] = mention.Mention,
                        ["confidence"] = mention.Confidence,
                        ["ambiguous"] = mention.Ambiguous
                    }, ct);
                edges++;
            }
        }

        await tx.CommitAsync(ct);
    }

    private static async Task<int> DeleteSourceAsync(SqliteConnection conn, SqliteTransaction tx, SourceRef source, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            CREATE TEMP TABLE IF NOT EXISTS userdoc_ids(id INTEGER PRIMARY KEY);
            DELETE FROM userdoc_ids;
            INSERT INTO userdoc_ids SELECT id FROM nodes WHERE qualified_name = @qn OR qualified_name LIKE @like ESCAPE '!';
            DELETE FROM edges WHERE source_id IN (SELECT id FROM userdoc_ids) OR target_id IN (SELECT id FROM userdoc_ids);
            DELETE FROM nodes WHERE id IN (SELECT id FROM userdoc_ids);
            SELECT COUNT(1) FROM userdoc_ids;";
        cmd.Parameters.AddWithValue("@qn", KnowledgeGraphLabels.DocumentQualifiedName(source));
        cmd.Parameters.AddWithValue("@like", Escape(KnowledgeGraphLabels.DocumentQualifiedName(source)) + "#%");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<long> InsertNodeAsync(SqliteConnection conn, SqliteTransaction tx, string project, string label, string name,
        string qualifiedName, int startLine, int endLine, Dictionary<string, object?> properties, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO nodes (project, project_id, label, name, qualified_name, file_path, file, start_line, end_line, line_start, line_end, properties)
            VALUES (@project, 1, @label, @name, @qn, '', '', @start, @end, @start, @end, @props);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@project", project);
        cmd.Parameters.AddWithValue("@label", label);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@qn", qualifiedName);
        cmd.Parameters.AddWithValue("@start", startLine);
        cmd.Parameters.AddWithValue("@end", endLine);
        cmd.Parameters.AddWithValue("@props", JsonSerializer.Serialize(properties, PropsJson));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task InsertEdgeAsync(SqliteConnection conn, SqliteTransaction tx, string project, long sourceId, long targetId,
        string type, Dictionary<string, object?> properties, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT OR IGNORE INTO edges (project, project_id, source_id, target_id, type, properties)
            VALUES (@project, 1, @s, @t, @type, @props);";
        cmd.Parameters.AddWithValue("@project", project);
        cmd.Parameters.AddWithValue("@s", sourceId);
        cmd.Parameters.AddWithValue("@t", targetId);
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@props", JsonSerializer.Serialize(properties, PropsJson));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<SqliteConnection> OpenAsync(string databasePath, CancellationToken ct)
    {
        // Sin pool: una conexión agrupada mantendría el fichero abierto cuando el análisis lo sobrescribe.
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false, DefaultTimeout = 30 }.ToString());
        await conn.OpenAsync(ct);
        return conn;
    }

    private static string Escape(string value) => value.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");
}
