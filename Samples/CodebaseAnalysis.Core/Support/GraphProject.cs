using Microsoft.Data.Sqlite;

namespace CodebaseAnalysis.Core.Support;

/// <summary>
/// Nombre de proyecto con el que escribir nodos y aristas nuevos. El esquema del indexador declara
/// <c>project NOT NULL</c> y lo usa en la clave única, así que un nodo añadido por el pipeline debe
/// llevar el mismo valor que los del indexador: sin él, el enriquecimiento por LLM fallaba entero,
/// y con el nombre de la carpeta la BD acababa con dos proyectos distintos.
/// </summary>
public static class GraphProject
{
    public static async Task<string> ResolveAsync(SqliteConnection conn, string fallback, CancellationToken ct, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT project FROM nodes
            WHERE project IS NOT NULL AND project <> ''
            GROUP BY project ORDER BY COUNT(1) DESC LIMIT 1;";
        return await cmd.ExecuteScalarAsync(ct) as string ?? fallback;
    }
}
