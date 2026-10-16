using Microsoft.Data.Sqlite;

namespace CodebaseAnalysis.Core.Enrichment;

/// <summary>
/// Salida de compilación y ficheros generados (<c>bin/</c>, <c>obj/</c>, <c>*.g.cs</c>, <c>*.Designer.cs</c>…) que cbm
/// indexa como si fueran código: los .json y .xml de <c>bin/</c> acababan como miles de nodos <c>Class</c> y
/// <c>Variable</c> que falseaban los totales, las métricas, los ciclos y las comunidades. Las reglas son las de
/// <c>CbmExclusionFilter</c> del enriquecedor Roslyn (que ya omitía esos ficheros al enriquecer pero no los quitaba del
/// grafo), limitadas a lo que es salida de compilación o generado.
/// </summary>
/// <remarks>
/// Se retiran del grafo por ruta, no con un <c>.cbmignore</c>: la regla es no escribir nunca en el repositorio analizado
/// (lo mismo que hace <c>DetectAndIndexGraphStep</c> con la salida del propio pipeline). El fichero se enlaza en el CLI
/// del enriquecedor, así que solo depende de Microsoft.Data.Sqlite.
/// </remarks>
public static class GeneratedArtifacts
{
    /// <summary>Carpetas de salida de compilación; se comparan por segmento, así que «Cabinet» o «combined» no cuentan.</summary>
    private static readonly string[] BuildOutputFolders = ["bin", "obj"];

    /// <summary>Sufijos de ficheros C# generados (compilador, diseñadores, XAML/Razor, MSBuild).</summary>
    private static readonly string[] GeneratedSuffixes =
        [".g.cs", ".g.i.cs", ".generated.cs", ".Designer.cs", ".AssemblyInfo.cs", ".AssemblyAttributes.cs"];

    /// <summary>Fragmentos de nombre de fichero generado (los que crea MSBuild en <c>obj/</c>).</summary>
    private static readonly string[] GeneratedFragments = [".generated.", "TemporaryGeneratedFile_"];

    private static readonly char[] PathSeparators = ['/', '\\'];

    /// <summary>¿La ruta (relativa al repositorio, con <c>/</c> o <c>\</c>) es salida de compilación o un fichero generado?</summary>
    public static bool IsGenerated(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var segments = path.Split(PathSeparators,StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => BuildOutputFolders.Contains(s, StringComparer.OrdinalIgnoreCase)))
            return true;

        var fileName = segments[^1];
        return GeneratedSuffixes.Any(s => fileName.EndsWith(s, StringComparison.OrdinalIgnoreCase))
            || GeneratedFragments.Any(f => fileName.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// La misma regla como consulta de ids sobre <c>nodes</c>. LIKE no distingue mayúsculas (ASCII), como el filtro de C#;
    /// <c>_</c> y <c>%</c> se escapan para que no actúen como comodín.
    /// </summary>
    private const string SelectGeneratedIdsSql = """
        SELECT id FROM nodes
        WHERE file_path IS NOT NULL AND file_path <> '' AND (
              ('/' || replace(file_path, '\', '/') || '/') LIKE '%/bin/%'
           OR ('/' || replace(file_path, '\', '/') || '/') LIKE '%/obj/%'
           OR replace(file_path, '\', '/') LIKE '%.g.cs'
           OR replace(file_path, '\', '/') LIKE '%.g.i.cs'
           OR replace(file_path, '\', '/') LIKE '%.generated.cs'
           OR replace(file_path, '\', '/') LIKE '%.Designer.cs'
           OR replace(file_path, '\', '/') LIKE '%.AssemblyInfo.cs'
           OR replace(file_path, '\', '/') LIKE '%.AssemblyAttributes.cs'
           OR replace(file_path, '\', '/') LIKE '%.generated.%'
           OR replace(file_path, '\', '/') LIKE '%TemporaryGeneratedFile!_%' ESCAPE '!')
        """;

    /// <summary>
    /// Retira del grafo los nodos de salida de compilación y de ficheros generados, con sus aristas y vectores. Es
    /// idempotente: sobre un grafo ya limpio no encuentra nada. Devuelve nodos y aristas borrados.
    /// </summary>
    public static Task<(int Nodes, int Edges)> RemoveFromGraphAsync(SqliteConnection conn, SqliteTransaction tx, CancellationToken ct) =>
        EnrichmentMarks.RemoveNodesAsync(conn, tx, SelectGeneratedIdsSql, null, ct);
}
