namespace CodebaseAnalysis.Core.Preparation;

/// <summary>
/// Agrupa ficheros por la estructura de carpetas con profundidad adaptativa (T10): es la base del mapa de dominios cuando el
/// grafo no tiene dependencias suficientes (<see cref="TypeGraph.IsPoor"/>), y la que reparte los ficheros sin tipos. Puro y
/// determinista.
/// </summary>
/// <remarks>
/// Desciende por el árbol mientras una carpeta tenga más ficheros de los que cabe esperar de un solo subsistema
/// (<see cref="GroupLimit"/>): las cadenas de una sola rama (<c>src/main/java/com/acme</c>) se atraviesan sin crear grupo, una
/// carpeta con varias ramas significativas se parte en ellas, y lo que no llega al mínimo se queda como residuo del padre.
/// </remarks>
public static class StructuralClusterer
{
    /// <summary>Tamaño por encima del cual una carpeta se parte: lo mayor entre cuatro veces el mínimo y el 10 % del repositorio.</summary>
    public static int GroupLimit(int totalFiles, int minFiles) => Math.Max(4 * minFiles, (int)Math.Ceiling(totalFiles * 0.10));

    /// <summary>
    /// Los grupos de ficheros (cada fichero en exactamente uno), ordenados por la ruta de su primer fichero; dentro de cada grupo,
    /// los ficheros por ruta ordinal.
    /// </summary>
    /// <param name="files">Rutas relativas con <c>/</c>.</param>
    /// <param name="minFiles">Ficheros por debajo de los cuales una rama no se separa de su carpeta padre.</param>
    public static IReadOnlyList<IReadOnlyList<string>> GroupByFolders(IReadOnlyList<string> files, int minFiles)
    {
        var root = new Folder();
        foreach (var file in files.Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal))
        {
            var folder = root;
            var segments = file.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (!folder.Children.TryGetValue(segments[i], out var child))
                    folder.Children[segments[i]] = child = new Folder();
                folder = child;
            }
            folder.Files.Add(file);
        }
        root.Count();

        var limit = GroupLimit(root.Total, Math.Max(1, minFiles));
        var groups = new List<IReadOnlyList<string>>();
        Collect(root, limit, Math.Max(1, minFiles), groups);
        return [.. groups.OrderBy(g => g[0], StringComparer.Ordinal)];
    }

    private static void Collect(Folder folder, int limit, int minFiles, List<IReadOnlyList<string>> groups)
    {
        if (folder.Total == 0)
            return;

        var significant = folder.Children.Values.Where(c => c.Total >= minFiles).ToList();
        if (folder.Total <= limit || significant.Count == 0)
        {
            groups.Add([.. folder.AllFiles().OrderBy(f => f, StringComparer.Ordinal)]);
            return;
        }

        foreach (var child in significant)
            Collect(child, limit, minFiles, groups);

        // El residuo (ficheros propios y ramas pequeñas) forma su propio grupo; si es pequeño lo absorberá quien llama.
        var residual = folder.Files.Concat(folder.Children.Values.Where(c => c.Total < minFiles).SelectMany(c => c.AllFiles()))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (residual.Count > 0)
            groups.Add(residual);
    }

    private sealed class Folder
    {
        public SortedDictionary<string, Folder> Children { get; } = new(StringComparer.Ordinal);
        public List<string> Files { get; } = [];
        public int Total { get; private set; }

        public int Count() => Total = Files.Count + Children.Values.Sum(c => c.Count());

        public IEnumerable<string> AllFiles() => Files.Concat(Children.Values.SelectMany(c => c.AllFiles()));
    }
}
