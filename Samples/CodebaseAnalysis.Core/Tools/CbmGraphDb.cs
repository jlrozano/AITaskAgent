using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Tools;

public sealed class CbmGraphDb
{
    private readonly string? _defaultDbPath;

    public CbmGraphDb(string? defaultDbPath = null)
    {
        _defaultDbPath = defaultDbPath;
    }

    public string ResolveDbPath(string? explicitDbPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDbPath) && File.Exists(explicitDbPath))
        {
            return Path.GetFullPath(explicitDbPath);
        }

        // Sin fallback a la caché de codebase-memory-mcp: elegir allí el primer *.db daba a las
        // tools el grafo de otro proyecto sin avisar. Si la BD no existe, SelectAsync devuelve vacío.
        return Path.GetFullPath(_defaultDbPath ?? Path.Combine(Directory.GetCurrentDirectory(), ".codebase", "codebase.db"));
    }

    public SqliteConnection OpenReadOnly(string? dbPath = null)
    {
        var resolved = ResolveDbPath(dbPath);
        return new SqliteConnection($"Data Source={resolved};Mode=ReadOnly;Cache=Shared");
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SelectAsync(
        string sql,
        Action<SqliteParameterCollection>? bind = null,
        string? dbPath = null,
        CancellationToken ct = default)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var resolved = ResolveDbPath(dbPath);
        if (!File.Exists(resolved)) return rows;

        try
        {
            using var conn = OpenReadOnly(resolved);
            await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            bind?.Invoke(cmd.Parameters);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CbmGraphDb] Error en SelectAsync: {ex.Message}");
        }

        return rows;
    }

    public async Task<IReadOnlyList<(string name, string qn, string props)>> GetNodesByLabelAsync(
        string label,
        string? dbPath = null,
        CancellationToken ct = default)
    {
        var list = new List<(string, string, string)>();
        var resolved = ResolveDbPath(dbPath);
        if (!File.Exists(resolved)) return list;

        try
        {
            using var conn = OpenReadOnly(resolved);
            await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name, qualified_name, properties FROM nodes WHERE label = @l ORDER BY name";
            cmd.Parameters.AddWithValue("@l", label);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add((
                    reader.IsDBNull(0) ? "" : reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "{}" : reader.GetString(2)));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CbmGraphDb] Error en GetNodesByLabelAsync: {ex.Message}");
        }

        return list;
    }
}
