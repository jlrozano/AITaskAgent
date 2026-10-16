using CodebaseAnalysis.Core.Support;

namespace CodebaseAnalysis.Core.Preparation;

/// <summary>
/// Recorrido de ficheros del repositorio para el inventario: lo que hay en disco, no lo que indexó cbm. Aplica las mismas
/// exclusiones que <c>RepositoryScan</c> (carpetas de <see cref="RepositoryPathFilter"/>, <c>.gradle</c>, <c>.idea</c>, las
/// salidas de compilación <c>target</c>/<c>build</c>/<c>out</c> en la raíz o junto a un fichero de build, la carpeta de
/// informes y los enlaces) salvo las raíces de test: aquí no se descartan, se clasifican después con
/// <see cref="SourceSetClassifier"/> para poder contarlas aparte.
/// </summary>
public static class RepositoryFileWalker
{
    /// <summary>Carpetas de herramientas que nunca son fuentes (como en <c>RepositoryScan</c>).</summary>
    private static readonly HashSet<string> ToolingDirectories = new(StringComparer.OrdinalIgnoreCase) { ".gradle", ".idea" };

    /// <summary>Salidas de compilación: solo en la raíz o junto a un fichero de build; en otro sitio pueden ser un paquete (<c>com/acme/out</c>).</summary>
    private static readonly HashSet<string> BuildOutputDirectories = new(StringComparer.OrdinalIgnoreCase) { "target", "build", "out" };

    /// <summary>Ficheros que marcan la raíz de un módulo con salidas de compilación.</summary>
    public static readonly IReadOnlySet<string> BuildFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "pom.xml", "build.gradle", "build.gradle.kts", "build.xml"
    };

    /// <summary>Rutas relativas al repositorio, con <c>/</c> y en orden ordinal. Vacío si la raíz no existe.</summary>
    /// <param name="repositoryRoot">Raíz del repositorio.</param>
    /// <param name="excludedDirectory">Directorio adicional a excluir (la carpeta de informes).</param>
    public static IReadOnlyList<string> Walk(string repositoryRoot, string? excludedDirectory = null)
    {
        var root = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(root))
            return [];

        var excluded = string.IsNullOrWhiteSpace(excludedDirectory)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(excludedDirectory));
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] subdirectories;
            string[] entries;
            try
            {
                subdirectories = Directory.GetDirectories(directory);
                entries = Directory.GetFiles(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            var besideBuildFile = string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)
                || entries.Any(f => BuildFiles.Contains(Path.GetFileName(f)));
            foreach (var subdirectory in subdirectories)
            {
                var name = Path.GetFileName(subdirectory);
                if (ToolingDirectories.Contains(name)
                    || (besideBuildFile && BuildOutputDirectories.Contains(name))
                    || (excluded != null && string.Equals(Path.GetFullPath(subdirectory), excluded, StringComparison.OrdinalIgnoreCase))
                    || RepositoryPathFilter.IsExcluded(root, subdirectory, excludedDirectory)
                    || IsLink(subdirectory))
                    continue;
                pending.Push(subdirectory);
            }

            foreach (var file in entries)
            {
                if (!IsLink(file))
                    files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static bool IsLink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
