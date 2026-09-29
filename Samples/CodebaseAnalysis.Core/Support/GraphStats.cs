using Microsoft.Data.Sqlite;

namespace CodebaseAnalysis.Core.Support;

/// <summary>Recuento de nodos y aristas del grafo, para el estado del workspace y el prompt del chat.</summary>
public static class GraphStats
{
    /// <summary>
    /// (nodos, aristas), o null si la BD no existe o no se puede leer. Abre sin pool: una conexión
    /// agrupada mantendría el fichero abierto cuando el análisis lo sobrescribe.
    /// </summary>
    public static async Task<(long Nodes, long Edges)?> TryCountAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            return null;

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();

            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT (SELECT COUNT(*) FROM nodes), (SELECT COUNT(*) FROM edges)";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? (reader.GetInt64(0), reader.GetInt64(1)) : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }
}
