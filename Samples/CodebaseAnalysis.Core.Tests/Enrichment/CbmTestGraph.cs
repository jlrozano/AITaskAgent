using System.Globalization;
using CodebaseAnalysis.Core.Support;
using Microsoft.Data.Sqlite;

namespace CodebaseAnalysis.Core.Tests.Enrichment;

/// <summary>
/// Copia sintética de una BD de codebase-memory-mcp: el DDL de cbm (<c>projects</c>, <c>nodes</c>, <c>edges</c> con su
/// columna generada, <c>node_vectors</c>) pasado después por <see cref="GraphSchema.EnsureAsync"/>, que es lo que hace el
/// pipeline con la copia de <c>.codebase/</c>.
/// </summary>
internal sealed class CbmTestGraph : IDisposable
{
    public string DatabasePath { get; }
    public string Project { get; }
    public SqliteConnection Connection { get; }

    private CbmTestGraph(string databasePath, string project, SqliteConnection connection)
    {
        DatabasePath = databasePath;
        Project = project;
        Connection = connection;
    }

    public static async Task<CbmTestGraph> CreateAsync(string databasePath, string project, string indexedAt = "2026-09-28T10:00:00Z")
    {
        using (var conn = new SqliteConnection($"Data Source={databasePath}"))
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE projects (name TEXT PRIMARY KEY, indexed_at TEXT NOT NULL, root_path TEXT NOT NULL);
                CREATE TABLE nodes (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    project TEXT NOT NULL REFERENCES projects(name) ON DELETE CASCADE,
                    label TEXT NOT NULL, name TEXT NOT NULL, qualified_name TEXT NOT NULL,
                    file_path TEXT DEFAULT '', start_line INTEGER DEFAULT 0, end_line INTEGER DEFAULT 0,
                    properties TEXT DEFAULT '{}', UNIQUE(project, qualified_name));
                CREATE TABLE edges (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    project TEXT NOT NULL REFERENCES projects(name) ON DELETE CASCADE,
                    source_id INTEGER NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
                    target_id INTEGER NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
                    type TEXT NOT NULL, properties TEXT DEFAULT '{}',
                    url_path_gen TEXT GENERATED ALWAYS AS (json_extract(properties,'$.url_path')),
                    UNIQUE(source_id, target_id, type));
                CREATE TABLE node_vectors (node_id INTEGER PRIMARY KEY, project TEXT NOT NULL, vector BLOB NOT NULL);
                INSERT INTO projects(name, indexed_at, root_path) VALUES (@project, @indexedAt, 'C:/repo');
                """;
            cmd.Parameters.AddWithValue("@project", project);
            cmd.Parameters.AddWithValue("@indexedAt", indexedAt);
            await cmd.ExecuteNonQueryAsync();
        }

        await GraphSchema.EnsureAsync(databasePath, CancellationToken.None);

        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        return new CbmTestGraph(databasePath, project, connection);
    }

    /// <summary>
    /// Inserta un nodo de cbm y devuelve su id. Rellena también <c>file</c> y <c>line_*</c>, como los deja
    /// <see cref="GraphSchema.EnsureAsync"/> en la copia (aquí el esquema ya se aseguró antes de insertar).
    /// </summary>
    public async Task<long> NodeAsync(string label, string name, string qualifiedName, string filePath = "", int start = 0, int end = 0, string properties = "{}")
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO nodes(project, label, name, qualified_name, file_path, file, start_line, end_line, line_start, line_end, properties)
            VALUES (@project, @label, @name, @qn, @file, @file, @start, @end, @start, @end, @props);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@project", Project);
        cmd.Parameters.AddWithValue("@label", label);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@qn", qualifiedName);
        cmd.Parameters.AddWithValue("@file", filePath);
        cmd.Parameters.AddWithValue("@start", start);
        cmd.Parameters.AddWithValue("@end", end);
        cmd.Parameters.AddWithValue("@props", properties);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    public async Task<long> EdgeAsync(long source, long target, string type, string properties = "{}")
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO edges(project, source_id, target_id, type, properties) VALUES (@project, @s, @t, @type, @props);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@project", Project);
        cmd.Parameters.AddWithValue("@s", source);
        cmd.Parameters.AddWithValue("@t", target);
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@props", properties);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    public async Task VectorAsync(long nodeId)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO node_vectors(node_id, project, vector) VALUES (@id, @project, x'00')";
        cmd.Parameters.AddWithValue("@id", nodeId);
        cmd.Parameters.AddWithValue("@project", Project);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> CountAsync(string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    public async Task<string?> TextAsync(string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteScalarAsync() as string;
    }

    public async Task<List<T>> ListAsync<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object Value)[] parameters)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        var rows = new List<T>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(map(reader));
        return rows;
    }

    public Task<long?> NodeIdAsync(string qualifiedName) =>
        ScalarLongAsync("SELECT id FROM nodes WHERE qualified_name = @qn", ("@qn", qualifiedName));

    private async Task<long?> ScalarLongAsync(string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        var value1 = await cmd.ExecuteScalarAsync();
        return value1 is null or DBNull ? null : Convert.ToInt64(value1, CultureInfo.InvariantCulture);
    }

    public void Dispose() => Connection.Dispose();
}
