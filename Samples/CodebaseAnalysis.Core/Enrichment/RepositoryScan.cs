using CodebaseAnalysis.Core.Support;

namespace CodebaseAnalysis.Core.Enrichment;

/// <summary>Lenguajes con enriquecedor que activa la búsqueda en disco (D2).</summary>
[Flags]
public enum DetectedLanguages
{
    None = 0,
    Java = 1,
    CSharp = 2
}

/// <summary>
/// Resultado de <see cref="RepositoryScan.Scan"/>. Todas las rutas son relativas a <see cref="Root"/>, con <c>/</c>, y
/// ordenadas; una raíz web vacía es la propia raíz del repositorio.
/// </summary>
public sealed record RepositoryScanResult
{
    public required string Root { get; init; }
    public IReadOnlyList<string> TestRoots { get; init; } = [];
    public IReadOnlyList<string> JavaFiles { get; init; } = [];
    public IReadOnlyList<string> CSharpFiles { get; init; } = [];
    public IReadOnlyList<string> Jars { get; init; } = [];
    public IReadOnlyList<string> StrutsConfigs { get; init; } = [];

    /// <summary>XML con un elemento <c>&lt;tiles-definitions</c>.</summary>
    public IReadOnlyList<string> TilesDefinitions { get; init; } = [];

    /// <summary><c>.jsp</c> y <c>.jspf</c>.</summary>
    public IReadOnlyList<string> JspFiles { get; init; } = [];

    /// <summary>Carpetas que contienen <c>WEB-INF</c>: contra ellas se resuelven las rutas de contexto (<c>/pages/x.jsp</c>).</summary>
    public IReadOnlyList<string> WebRoots { get; init; } = [];

    /// <summary>
    /// XML de configuración (<c>.xml</c> y <c>.config</c>, sin los ficheros de build como <c>pom.xml</c> o <c>build.xml</c>):
    /// donde Spring, EJB, <c>web.xml</c>, Struts o .NET nombran clases que instancian por reflexión. Incluye los de Struts
    /// y Tiles.
    /// </summary>
    public IReadOnlyList<string> ConfigurationXmlFiles { get; init; } = [];

    public DetectedLanguages Languages { get; init; }

    public string FullPath(string relativePath) =>
        Path.GetFullPath(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
}

/// <summary>
/// Recorrido único del repositorio para la selección de enriquecedores (§Selección por lenguaje): lo que hay en disco, no
/// lo que haya decidido indexar cbm. No entra en las carpetas de <see cref="RepositoryPathFilter"/>, en <c>.gradle</c> ni
/// <c>.idea</c>, en las de salida de compilación (<c>target</c>, <c>build</c>, <c>out</c> en la raíz o junto a un
/// <c>pom.xml</c>, <c>build.gradle(.kts)</c> o <c>build.xml</c>) ni en las raíces de test, y no sigue enlaces de directorio.
/// </summary>
public static class RepositoryScan
{
    /// <summary>Carpetas de herramientas que nunca son fuentes, estén donde estén.</summary>
    private static readonly HashSet<string> ToolingDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gradle", ".idea"
    };

    /// <summary>
    /// Salidas de compilación. Solo se excluyen en la raíz del repositorio o junto a un fichero de build: en cualquier otro
    /// sitio pueden ser un paquete Java (<c>com/acme/out</c>).
    /// </summary>
    private static readonly HashSet<string> BuildOutputDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "target", "build", "out"
    };

    private static readonly HashSet<string> BuildFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "pom.xml", "build.gradle", "build.gradle.kts", "build.xml"
    };

    /// <summary>Bytes que se leen de cada XML para buscar <c>&lt;tiles-definitions</c>.</summary>
    private const int XmlSniffBytes = 4096;

    /// <param name="repositoryRoot">Raíz del repositorio.</param>
    /// <param name="testRoots">Raíces de test ya resueltas con <see cref="TestRoots.Resolve"/>.</param>
    /// <param name="excludedDirectory">Directorio adicional a excluir (la carpeta de informes).</param>
    public static RepositoryScanResult Scan(string repositoryRoot, IReadOnlyList<string> testRoots, string? excludedDirectory = null)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var excluded = string.IsNullOrWhiteSpace(excludedDirectory)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(excludedDirectory));
        var java = new List<string>();
        var csharp = new List<string>();
        var jars = new List<string>();
        var struts = new List<string>();
        var tiles = new List<string>();
        var jsps = new List<string>();
        var webRoots = new List<string>();
        var configurationXml = new List<string>();

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var relativeDirectory = Relative(root, directory);

            IEnumerable<string> subdirectories;
            IEnumerable<string> files;
            try
            {
                subdirectories = Directory.EnumerateDirectories(directory).ToList();
                files = Directory.EnumerateFiles(directory).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            var besideBuildFile = relativeDirectory.Length == 0 || files.Any(f => BuildFiles.Contains(Path.GetFileName(f)));
            foreach (var subdirectory in subdirectories)
            {
                var name = Path.GetFileName(subdirectory);
                if (name.Equals("WEB-INF", StringComparison.OrdinalIgnoreCase))
                    webRoots.Add(relativeDirectory);

                // RepositoryPathFilter excluye lo que cuelga del directorio adicional, no el directorio en sí.
                if (ToolingDirectories.Contains(name)
                    || (besideBuildFile && BuildOutputDirectories.Contains(name))
                    || (excluded != null && string.Equals(Path.GetFullPath(subdirectory), excluded, StringComparison.OrdinalIgnoreCase))
                    || RepositoryPathFilter.IsExcluded(root, subdirectory, excludedDirectory)
                    || TestRoots.Contains(testRoots, Relative(root, subdirectory))
                    || IsLink(subdirectory))
                    continue;
                pending.Push(subdirectory);
            }

            foreach (var file in files)
            {
                var relative = Relative(root, file);
                var name = Path.GetFileName(file);
                switch (Path.GetExtension(file).ToLowerInvariant())
                {
                    case ".java":
                        java.Add(relative);
                        break;
                    case ".cs":
                        csharp.Add(relative);
                        break;
                    case ".jar":
                        jars.Add(relative);
                        break;
                    case ".jsp":
                    case ".jspf":
                        jsps.Add(relative);
                        break;
                    case ".xml":
                        if (!BuildFiles.Contains(name))
                            configurationXml.Add(relative);
                        if (name.StartsWith("struts-config", StringComparison.OrdinalIgnoreCase))
                            struts.Add(relative);
                        else if (IsTilesDefinitions(file))
                            tiles.Add(relative);
                        break;
                    case ".config":
                        configurationXml.Add(relative);
                        break;
                }
            }
        }

        var languages = DetectedLanguages.None;
        if (java.Count > 0)
            languages |= DetectedLanguages.Java;
        if (csharp.Count > 0)
            languages |= DetectedLanguages.CSharp;

        return new RepositoryScanResult
        {
            Root = root,
            TestRoots = testRoots,
            JavaFiles = Sorted(java),
            CSharpFiles = Sorted(csharp),
            Jars = Sorted(jars),
            StrutsConfigs = Sorted(struts),
            TilesDefinitions = Sorted(tiles),
            JspFiles = Sorted(jsps),
            WebRoots = Sorted(webRoots),
            ConfigurationXmlFiles = Sorted(configurationXml),
            Languages = languages
        };
    }

    private static string Relative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative == "." ? "" : relative;
    }

    private static bool IsLink(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static bool IsTilesDefinitions(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[XmlSniffBytes];
            var read = stream.Read(buffer, 0, buffer.Length);
            // Latin-1 conserva los bytes ASCII tal cual, sea cual sea la codificación del XML.
            return System.Text.Encoding.Latin1.GetString(buffer, 0, read)
                .Contains("<tiles-definitions", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static List<string> Sorted(List<string> paths)
    {
        paths.Sort(StringComparer.Ordinal);
        return paths;
    }
}
