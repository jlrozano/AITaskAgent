namespace CodebaseAnalysis.Core.Support;

/// <summary>
/// Directorios que ningún escáner del pipeline debe recorrer. Se comparan por segmento de la ruta
/// relativa al repositorio: un <c>Contains("bin")</c> sobre la ruta completa descartaba también
/// "Cabinet", "combined" o cualquier repositorio alojado bajo una carpeta con esos nombres.
/// </summary>
public static class RepositoryPathFilter
{
    private static readonly HashSet<string> ExcludedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".codebase"
    };

    private static readonly char[] Separators = ['\\', '/'];

    /// <param name="repositoryRoot">Raíz del repositorio analizado.</param>
    /// <param name="path">Fichero a evaluar.</param>
    /// <param name="excludedDirectory">Directorio adicional a excluir (p. ej. la carpeta de informes).</param>
    public static bool IsExcluded(string repositoryRoot, string path, string? excludedDirectory = null)
    {
        var fullPath = Path.GetFullPath(path);

        if (!string.IsNullOrWhiteSpace(excludedDirectory))
        {
            var excludedPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(excludedDirectory)) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(excludedPrefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var relativePath = Path.GetRelativePath(repositoryRoot, fullPath);
        return relativePath
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Any(ExcludedSegments.Contains);
    }
}
