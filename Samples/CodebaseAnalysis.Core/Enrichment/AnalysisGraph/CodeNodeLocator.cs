using Microsoft.Data.Sqlite;

namespace CodebaseAnalysis.Core.Enrichment.AnalysisGraph;

/// <summary>Un nodo de código al que se ancla una <c>ruta:línea</c>.</summary>
/// <param name="Id">Id del nodo.</param>
/// <param name="Label">Su etiqueta.</param>
/// <param name="QualifiedName">Su nombre cualificado.</param>
/// <param name="File">Su fichero, tal como está en el grafo.</param>
/// <param name="FileLevel">True si no hay nodo que contenga la línea y se ancló al nodo del fichero (<c>File</c> o <c>Module</c>).</param>
public sealed record LocatedNode(long Id, string Label, string QualifiedName, string File, bool FileLevel);

/// <summary>
/// Resuelve <c>ruta:línea</c> al nodo de código más específico que contiene la línea: entre los nodos del fichero cuyo rango
/// <c>[start_line, end_line]</c> la incluye, el de rango más corto (a igualdad, el que empieza más abajo y después el de id menor).
/// Si ninguno la contiene, el nodo del fichero (<c>File</c> o <c>Module</c>); si el fichero no tiene nodos, nada. La ruta se busca
/// exacta (sin distinguir mayúsculas y con <c>/</c>), si no está como sufijo único de una ruta del grafo (<c>Foo.java</c> o
/// <c>pkg/Foo.java</c> citados sin la raíz) y, por último, como ruta absoluta que acaba en una ruta del grafo.
/// </summary>
internal sealed class CodeNodeLocator
{
    /// <summary>Labels que no son código: estructura, conocimiento y lo que escribe el propio análisis.</summary>
    internal static readonly HashSet<string> NotCode = new(StringComparer.Ordinal)
    {
        "Folder", "Project", "Package", "CsProject",
        AnalysisGraphLabels.Subsystem, AnalysisGraphLabels.Table, AnalysisGraphLabels.InvestigationNote, "AccessTable",
        "UserDocument", "UserDocSection",
        AnalysisGraphLabels.Finding, AnalysisGraphLabels.Anchor, AnalysisGraphLabels.Summary, AnalysisGraphLabels.Batch, AnalysisGraphLabels.Run
    };

    /// <summary>Nodos que representan el fichero entero: solo se usan si ningún otro contiene la línea.</summary>
    private static readonly HashSet<string> FileLabels = new(StringComparer.Ordinal) { "File", "Module" };

    private sealed record Candidate(long Id, string Label, string QualifiedName, string File, int Start, int End);

    private const string FileExpr = "replace(coalesce(nullif(n.file_path, ''), n.file, ''), '\\', '/')";

    private readonly Dictionary<string, List<Candidate>> _byFile;
    private readonly Dictionary<string, string> _allFiles;
    private readonly Dictionary<string, string?> _resolvedPaths = new(StringComparer.OrdinalIgnoreCase);

    private CodeNodeLocator(Dictionary<string, List<Candidate>> byFile, IEnumerable<string> allFiles)
    {
        _byFile = byFile;
        _allFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in allFiles)
            _allFiles.TryAdd(file, file);
    }

    /// <summary>Carga los nodos de los ficheros pedidos, que vienen con la ruta relativa del repositorio (las de un escaneo de disco).</summary>
    public static async Task<CodeNodeLocator> LoadAsync(SqliteConnection conn, SqliteTransaction? tx, IEnumerable<string> files, CancellationToken ct)
    {
        var byFile = await LoadCandidatesAsync(conn, tx, files.Select(Normalize).Where(f => f.Length > 0), ct);
        return new CodeNodeLocator(byFile, byFile.Keys);
    }

    /// <summary>
    /// Para las citas de un texto: lee las rutas de todos los ficheros del grafo, resuelve con ellas <paramref name="citedPaths"/> (que
    /// pueden venir sin la raíz o con la ruta absoluta) y carga solo los nodos de los ficheros que casan.
    /// </summary>
    public static async Task<CodeNodeLocator> LoadForCitationsAsync(SqliteConnection conn, SqliteTransaction? tx, IEnumerable<string> citedPaths, CancellationToken ct)
    {
        var allFiles = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT DISTINCT {FileExpr} FROM nodes n WHERE {FileExpr} <> ''";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                allFiles.Add(reader.GetString(0));
        }

        var resolver = new CodeNodeLocator(new Dictionary<string, List<Candidate>>(), allFiles);
        var wanted = citedPaths.Select(resolver.ResolveFile).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var byFile = wanted.Count == 0
            ? new Dictionary<string, List<Candidate>>(StringComparer.OrdinalIgnoreCase)
            : await LoadCandidatesAsync(conn, tx, wanted, ct);
        return new CodeNodeLocator(byFile, allFiles);
    }

    private static async Task<Dictionary<string, List<Candidate>>> LoadCandidatesAsync(
        SqliteConnection conn, SqliteTransaction? tx, IEnumerable<string> files, CancellationToken ct)
    {
        await Execute(conn, tx, "CREATE TEMP TABLE IF NOT EXISTS locator_files(path TEXT PRIMARY KEY COLLATE NOCASE)", ct);
        await Execute(conn, tx, "DELETE FROM temp.locator_files", ct);
        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT OR IGNORE INTO temp.locator_files(path) VALUES (@p)";
            var p = insert.Parameters.Add("@p", SqliteType.Text);
            foreach (var file in files)
            {
                p.Value = file;
                await insert.ExecuteNonQueryAsync(ct);
            }
        }

        var byFile = new Dictionary<string, List<Candidate>>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                SELECT n.id, n.label, n.qualified_name, {FileExpr},
                       coalesce(nullif(n.line_start, 0), n.start_line, 0), coalesce(nullif(n.line_end, 0), n.end_line, 0)
                FROM nodes n
                WHERE {FileExpr} COLLATE NOCASE IN (SELECT path FROM temp.locator_files)
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var label = reader.GetString(1);
                if (NotCode.Contains(label))
                    continue;
                var file = reader.GetString(3);
                if (!byFile.TryGetValue(file, out var list))
                    byFile[file] = list = [];
                list.Add(new Candidate(reader.GetInt64(0), label, reader.GetString(2), file,
                    reader.IsDBNull(4) ? 0 : reader.GetInt32(4), reader.IsDBNull(5) ? 0 : reader.GetInt32(5)));
            }
        }
        await Execute(conn, tx, "DELETE FROM temp.locator_files", ct);
        return byFile;
    }

    /// <summary>La ruta del grafo a la que corresponde <paramref name="path"/>; null si no hay ninguna o el sufijo es ambiguo.</summary>
    public string? ResolveFile(string path)
    {
        var normalized = Normalize(path);
        if (normalized.Length == 0)
            return null;
        if (_resolvedPaths.TryGetValue(normalized, out var cached))
            return cached;

        if (!_allFiles.TryGetValue(normalized, out var resolved))
        {
            var matches = _allFiles.Keys.Where(k => k.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (matches.Count == 1)
                resolved = matches[0];
            else if (matches.Count == 0)
            {
                // Ruta absoluta o con la raíz del repositorio delante: la ruta del grafo más larga que sea sufijo suyo.
                resolved = _allFiles.Keys.Where(k => normalized.EndsWith("/" + k, StringComparison.OrdinalIgnoreCase)).MaxBy(k => k.Length);
            }
        }
        _resolvedPaths[normalized] = resolved;
        return resolved;
    }

    /// <summary>El nodo más específico que contiene <paramref name="line"/> en <paramref name="path"/>; null si no se puede anclar.</summary>
    public LocatedNode? Locate(string path, int line)
    {
        if (ResolveFile(path) is not { } file || !_byFile.TryGetValue(file, out var candidates))
            return null;

        var best = candidates
            .Where(c => !FileLabels.Contains(c.Label) && c.Start > 0 && c.Start <= line && line <= Math.Max(c.End, c.Start))
            .OrderBy(c => Math.Max(c.End, c.Start) - c.Start)
            .ThenByDescending(c => c.Start)
            .ThenBy(c => c.Id)
            .FirstOrDefault();
        if (best != null)
            return new LocatedNode(best.Id, best.Label, best.QualifiedName, best.File, false);

        var fileNode = candidates.Where(c => FileLabels.Contains(c.Label)).OrderBy(c => c.Label == "File" ? 0 : 1).ThenBy(c => c.Id).FirstOrDefault();
        return fileNode == null ? null : new LocatedNode(fileNode.Id, fileNode.Label, fileNode.QualifiedName, fileNode.File, true);
    }

    /// <summary>El nodo del fichero (<c>File</c> o <c>Module</c>) de <paramref name="path"/>; null si el fichero no está en el grafo.</summary>
    public LocatedNode? LocateFile(string path)
    {
        if (ResolveFile(path) is not { } file || !_byFile.TryGetValue(file, out var candidates))
            return null;
        var fileNode = candidates.Where(c => FileLabels.Contains(c.Label)).OrderBy(c => c.Label == "File" ? 0 : 1).ThenBy(c => c.Id).FirstOrDefault();
        return fileNode == null ? null : new LocatedNode(fileNode.Id, fileNode.Label, fileNode.QualifiedName, fileNode.File, true);
    }

    private static string Normalize(string path)
    {
        var p = path.Trim().Trim('`', '"', '\'').Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal))
            p = p[2..];
        return p.TrimStart('/');
    }

    private static async Task Execute(SqliteConnection conn, SqliteTransaction? tx, string sql, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
