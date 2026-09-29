using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;

namespace CodebaseAnalysis.Core.Knowledge.Embeddings;

/// <summary>Una fuente indexada (fila de <c>sources</c>).</summary>
public sealed record EmbeddedSource(
    string SourceKey, string Kind, string Ref, string? Title, string ContentSha256, string Model, int Dimensions,
    int ChunkCount, int? TokenCount, DateTimeOffset IndexedAt);

/// <summary>Un trozo con su vector, listo para guardar.</summary>
public sealed record EmbeddedChunk(Chunk Chunk, float[] Vector);

/// <summary>Un resultado de búsqueda.</summary>
public sealed record EmbeddingHit(string SourceKey, int Ordinal, string? Heading, string Text, int? StartLine, int? EndLine, float Score);

/// <summary>
/// <c>&lt;repo&gt;/.codebase/knowledge/embeddings.db</c> en modo WAL (plan v2 §8.2.3). Indexar una fuente es una
/// transacción que reemplaza sus trozos; la búsqueda carga una vez en memoria los vectores del modelo actual (ya
/// normalizados, así que el coseno es un producto escalar) y la caché se invalida con cada escritura.
/// </summary>
public sealed class EmbeddingStore(string databasePath)
{
    public const int SchemaVersion = 1;

    /// <summary>Tope de trozos por repositorio.</summary>
    public const int MaxChunksPerRepository = 50_000;

    private static readonly ConcurrentDictionary<string, (long Version, List<Cached> Rows)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> Versions = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Cached(string SourceKey, int Ordinal, string? Heading, string Text, int? StartLine, int? EndLine, float[] Vector, string Model, int Dimensions);

    public string DatabasePath { get; } = Path.GetFullPath(databasePath);

    public bool Exists => File.Exists(DatabasePath);

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false, DefaultTimeout = 30 }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sources(
              source_key TEXT PRIMARY KEY, kind TEXT NOT NULL, ref TEXT NOT NULL, title TEXT,
              content_sha256 TEXT NOT NULL, model TEXT NOT NULL, dimensions INTEGER NOT NULL,
              chunk_count INTEGER NOT NULL, token_count INTEGER, indexed_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS chunks(
              id INTEGER PRIMARY KEY, source_key TEXT NOT NULL REFERENCES sources(source_key) ON DELETE CASCADE,
              ordinal INTEGER NOT NULL, heading TEXT, text TEXT NOT NULL, start_line INTEGER, end_line INTEGER,
              vector BLOB NOT NULL,
              UNIQUE(source_key, ordinal));
            CREATE INDEX IF NOT EXISTS idx_chunks_source ON chunks(source_key);
            INSERT OR IGNORE INTO meta(key, value) VALUES ('schema_version', '1');";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public EmbeddedSource? GetSource(string sourceKey)
    {
        if (!Exists)
            return null;
        using var conn = Open();
        return ReadSources(conn, "WHERE source_key = @key", sourceKey).FirstOrDefault();
    }

    public IReadOnlyList<EmbeddedSource> ListSources()
    {
        if (!Exists)
            return [];
        using var conn = Open();
        return ReadSources(conn, string.Empty, null);
    }

    /// <summary>Reemplaza los trozos de una fuente en una transacción.</summary>
    /// <exception cref="Reports.KnowledgeOperationException">Si se superaría el tope de trozos del repositorio.</exception>
    public void Replace(SourceRef source, string? title, string contentSha, string model, int dimensions, IReadOnlyList<EmbeddedChunk> chunks, int? tokenCount)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var count = conn.CreateCommand())
        {
            count.Transaction = tx;
            count.CommandText = "SELECT COUNT(1) FROM chunks WHERE source_key <> @key;";
            count.Parameters.AddWithValue("@key", source.Key);
            var others = Convert.ToInt32(count.ExecuteScalar());
            if (others + chunks.Count > MaxChunksPerRepository)
            {
                throw new Reports.KnowledgeOperationException(Reports.KnowledgeErrorCodes.InvalidRequest,
                    $"Se superaría el máximo de {MaxChunksPerRepository} trozos de embeddings del repositorio.");
            }
        }

        Execute(conn, tx, "DELETE FROM chunks WHERE source_key = @key; DELETE FROM sources WHERE source_key = @key;", ("@key", source.Key));
        Execute(conn, tx, @"
            INSERT INTO sources(source_key, kind, ref, title, content_sha256, model, dimensions, chunk_count, token_count, indexed_at)
            VALUES (@key, @kind, @ref, @title, @sha, @model, @dims, @count, @tokens, @at);",
            ("@key", source.Key), ("@kind", source.KindName), ("@ref", source.Id), ("@title", title), ("@sha", contentSha),
            ("@model", model), ("@dims", dimensions), ("@count", chunks.Count), ("@tokens", tokenCount), ("@at", DateTimeOffset.UtcNow.ToString("o")));

        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = @"
                INSERT INTO chunks(source_key, ordinal, heading, text, start_line, end_line, vector)
                VALUES (@key, @ord, @heading, @text, @start, @end, @vector);";
            var key = insert.Parameters.Add("@key", SqliteType.Text);
            var ord = insert.Parameters.Add("@ord", SqliteType.Integer);
            var heading = insert.Parameters.Add("@heading", SqliteType.Text);
            var text = insert.Parameters.Add("@text", SqliteType.Text);
            var start = insert.Parameters.Add("@start", SqliteType.Integer);
            var end = insert.Parameters.Add("@end", SqliteType.Integer);
            var vector = insert.Parameters.Add("@vector", SqliteType.Blob);
            foreach (var c in chunks)
            {
                key.Value = source.Key;
                ord.Value = c.Chunk.Ordinal;
                heading.Value = (object?)c.Chunk.Heading ?? DBNull.Value;
                text.Value = c.Chunk.Text;
                start.Value = c.Chunk.StartLine;
                end.Value = c.Chunk.EndLine;
                vector.Value = MemoryMarshal.AsBytes(Normalize(c.Vector).AsSpan()).ToArray();
                insert.ExecuteNonQuery();
            }
        }

        tx.Commit();
        Invalidate();
    }

    public bool Remove(SourceRef source)
    {
        if (!Exists)
            return false;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        Execute(conn, tx, "DELETE FROM chunks WHERE source_key = @key;", ("@key", source.Key));
        var removed = Execute(conn, tx, "DELETE FROM sources WHERE source_key = @key;", ("@key", source.Key)) > 0;
        tx.Commit();
        Invalidate();
        return removed;
    }

    /// <summary>
    /// Los <paramref name="topK"/> trozos más parecidos a <paramref name="query"/> entre los del modelo y dimensiones
    /// dados, opcionalmente filtrados por clave de fuente.
    /// </summary>
    public IReadOnlyList<EmbeddingHit> Search(float[] query, string model, int dimensions, int topK, Func<string, bool>? sourceFilter = null)
    {
        if (!Exists)
            return [];

        var q = Normalize(query);
        return LoadCached()
            .Where(r => r.Model == model && r.Dimensions == dimensions && r.Vector.Length == q.Length && (sourceFilter?.Invoke(r.SourceKey) ?? true))
            .Select(r => (Row: r, Score: Dot(q, r.Vector)))
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => new EmbeddingHit(x.Row.SourceKey, x.Row.Ordinal, x.Row.Heading, x.Row.Text, x.Row.StartLine, x.Row.EndLine, x.Score))
            .ToList();
    }

    private List<Cached> LoadCached()
    {
        var version = Versions.GetOrAdd(DatabasePath, 0);
        if (Cache.TryGetValue(DatabasePath, out var cached) && cached.Version == version)
            return cached.Rows;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT c.source_key, c.ordinal, c.heading, c.text, c.start_line, c.end_line, c.vector, s.model, s.dimensions
            FROM chunks c JOIN sources s ON s.source_key = c.source_key;";
        var rows = new List<Cached>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var blob = (byte[])reader.GetValue(6);
            rows.Add(new Cached(reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetInt32(5),
                MemoryMarshal.Cast<byte, float>(blob).ToArray(), reader.GetString(7), reader.GetInt32(8)));
        }
        Cache[DatabasePath] = (version, rows);
        return rows;
    }

    private void Invalidate() => Versions.AddOrUpdate(DatabasePath, 1, (_, v) => v + 1);

    private static List<EmbeddedSource> ReadSources(SqliteConnection conn, string where, string? key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT source_key, kind, ref, title, content_sha256, model, dimensions, chunk_count, token_count, indexed_at FROM sources {where};";
        if (key != null)
            cmd.Parameters.AddWithValue("@key", key);
        var result = new List<EmbeddedSource>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new EmbeddedSource(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7), reader.IsDBNull(8) ? null : reader.GetInt32(8),
                DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture)));
        }
        return result;
    }

    private static int Execute(SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Vector con norma L2 1 (el cero se deja igual).</summary>
    public static float[] Normalize(float[] vector)
    {
        var norm = MathF.Sqrt(Dot(vector, vector));
        if (norm == 0 || MathF.Abs(norm - 1) < 1e-6f)
            return vector;
        return vector.Select(v => v / norm).ToArray();
    }

    /// <summary>Producto escalar con <see cref="Vector{T}"/>.</summary>
    public static float Dot(float[] a, float[] b)
    {
        var sum = 0f;
        var i = 0;
        var width = Vector<float>.Count;
        var acc = Vector<float>.Zero;
        for (; i <= a.Length - width; i += width)
            acc += new Vector<float>(a, i) * new Vector<float>(b, i);
        sum += Vector.Dot(acc, Vector<float>.One);
        for (; i < a.Length; i++)
            sum += a[i] * b[i];
        return sum;
    }
}
