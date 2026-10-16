using System.Text;
using AITaskAgent.FileTools;

namespace CodebaseAnalysis.Core.Preparation;

/// <summary>Un fichero de código contado: ruta relativa con <c>/</c>, lenguaje, source set y líneas físicas.</summary>
public sealed record CountedFile(string Path, string Language, string SourceSet, int Lines);

/// <summary>El resultado de <see cref="ProductionLineCounter.Count(string, PreparationOptions, string?, IReadOnlyList{string}?)"/>.</summary>
public sealed record LineCountResult
{
    /// <summary>Todos los ficheros con extensión de código, de cualquier source set, en orden ordinal de ruta.</summary>
    public IReadOnlyList<CountedFile> Files { get; init; } = [];

    /// <summary>Los de producción.</summary>
    public IReadOnlyList<CountedFile> Production { get; init; } = [];

    /// <summary>Ficheros que no se pudieron leer (cuentan 0 líneas y no entran en las listas).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Cuenta en disco las líneas físicas de los ficheros de código (T4). No se apoya en el grafo: en el índice de cbm los nodos
/// <c>Class</c> traen <c>"lines":0</c>. Una línea física es un fin de línea (<c>\n</c>, también el de <c>\r\n</c>) más la última
/// si el fichero no termina en uno; un fichero vacío tiene 0.
/// </summary>
/// <remarks>
/// La lectura es tolerante con la codificación: contar <c>0x0A</c> da lo mismo en UTF-8 y en Latin-1 (y no falla con bytes
/// inválidos), y solo los ficheros con BOM de UTF-16 o UTF-32 se decodifican, porque allí el salto de línea ocupa más de un byte.
/// </remarks>
public static class ProductionLineCounter
{
    private const int BufferSize = 64 * 1024;

    private static readonly Dictionary<string, string> LanguageByExtension = new(StringComparer.Ordinal)
    {
        [".java"] = "java",
        [".cs"] = "csharp",
        [".jsp"] = "jsp",
        [".jspf"] = "jsp",
        [".js"] = "javascript",
        [".jsx"] = "javascript",
        [".mjs"] = "javascript",
        [".ts"] = "typescript",
        [".tsx"] = "typescript",
        [".py"] = "python",
        [".go"] = "go",
        [".kt"] = "kotlin",
        [".scala"] = "scala",
        [".vb"] = "vb",
        [".rb"] = "ruby",
        [".php"] = "php"
    };

    /// <summary>Lenguaje de una extensión (con punto, en minúsculas); sin nombre propio, la extensión sin el punto.</summary>
    public static string LanguageOf(string extension) =>
        LanguageByExtension.TryGetValue(extension, out var language) ? language : extension.TrimStart('.');

    /// <summary>Recorre el repositorio (<see cref="RepositoryFileWalker"/>) y cuenta sus ficheros de código.</summary>
    /// <param name="repositoryRoot">Raíz del repositorio.</param>
    /// <param name="options">Aporta las extensiones que cuentan (<see cref="PreparationOptions.CountedExtensions"/>).</param>
    /// <param name="excludedDirectory">Carpeta de informes, que no se recorre.</param>
    /// <param name="testRoots">Raíces de test (<c>Enrichment.TestRoots.Resolve</c>) para clasificar.</param>
    public static LineCountResult Count(
        string repositoryRoot, PreparationOptions options, string? excludedDirectory = null, IReadOnlyList<string>? testRoots = null) =>
        Count(repositoryRoot, RepositoryFileWalker.Walk(repositoryRoot, excludedDirectory), options, testRoots);

    /// <summary>Cuenta los <paramref name="relativeFiles"/> ya recorridos (para no recorrer el repositorio dos veces).</summary>
    public static LineCountResult Count(
        string repositoryRoot, IReadOnlyList<string> relativeFiles, PreparationOptions options, IReadOnlyList<string>? testRoots = null)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var extensions = new HashSet<string>(options.CountedExtensions, StringComparer.Ordinal);
        var files = new List<CountedFile>();
        var warnings = new List<string>();

        foreach (var relative in relativeFiles.OrderBy(p => p, StringComparer.Ordinal))
        {
            var extension = Path.GetExtension(relative).ToLowerInvariant();
            if (!extensions.Contains(extension))
                continue;

            int lines;
            try
            {
                lines = CountLines(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"⚠️ No se pudo leer '{relative}' para contar sus líneas: {ex.Message}");
                continue;
            }

            files.Add(new CountedFile(relative, LanguageOf(extension), SourceSetClassifier.Classify(relative, options.CountedExtensions, testRoots), lines));
        }

        return new LineCountResult
        {
            Files = files,
            Production = [.. files.Where(f => SourceSets.IsProduction(f.SourceSet))],
            Warnings = warnings
        };
    }

    /// <summary>Líneas por lenguaje, ordenadas por nombre de lenguaje.</summary>
    public static IReadOnlyList<LanguageLines> ByLanguage(IEnumerable<CountedFile> files) =>
    [
        .. files.GroupBy(f => f.Language, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new LanguageLines { Language = g.Key, Files = g.Count(), Lines = g.Sum(f => (long)f.Lines) })
    ];

    /// <summary>Líneas físicas de un fichero.</summary>
    /// <exception cref="IOException">Si no se puede abrir o leer.</exception>
    public static int CountLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
        var buffer = new byte[BufferSize];
        var read = stream.Read(buffer, 0, buffer.Length);
        if (read == 0)
            return 0;
        if (HasWideBom(buffer.AsSpan(0, Math.Min(read, 4))))
        {
            using var rest = new MemoryStream();
            rest.Write(buffer, 0, read);
            stream.CopyTo(rest);
            return CountTextLines(TextFileEncoding.Decode(rest.ToArray(), Encoding.Latin1).Text);
        }

        var lines = 0;
        var lastByte = (byte)0;
        while (read > 0)
        {
            var span = buffer.AsSpan(0, read);
            lines += span.Count((byte)'\n');
            lastByte = span[^1];
            read = stream.Read(buffer, 0, buffer.Length);
        }
        return lastByte == (byte)'\n' ? lines : lines + 1;
    }

    private static int CountTextLines(string text)
    {
        if (text.Length == 0)
            return 0;
        var lines = text.Count(c => c == '\n');
        return text[^1] == '\n' ? lines : lines + 1;
    }

    /// <summary>BOM de UTF-16 (LE o BE) o de UTF-32.</summary>
    private static bool HasWideBom(ReadOnlySpan<byte> start) =>
        (start.Length >= 2 && ((start[0] == 0xFF && start[1] == 0xFE) || (start[0] == 0xFE && start[1] == 0xFF)))
        || (start.Length >= 4 && start[0] == 0 && start[1] == 0 && start[2] == 0xFE && start[3] == 0xFF);
}
