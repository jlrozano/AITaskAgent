namespace CodebaseAnalysis.Core.Enrichment;

/// <summary>
/// Raíces del código de test del repositorio analizado (D15): no se analizan y sus nodos se retiran de la copia del grafo.
/// Salen del <c>build/testSourceDirectory</c> del <c>pom.xml</c> raíz, de <c>src/test/java</c> si existe y de
/// <c>JavaAnalyzer:TestRoots</c>. Se devuelven relativas al repositorio, con <c>/</c> y sin <c>./</c> ni barra final, que
/// es como cbm guarda <c>file_path</c>.
/// </summary>
public static class TestRoots
{
    private const string MavenConvention = "src/test/java";

    public static IReadOnlyList<string> Resolve(string repositoryRoot, IEnumerable<string>? configured = null)
    {
        var roots = new List<string>();

        var fromPom = RootPom.Value(RootPom.Load(repositoryRoot), "build", "testSourceDirectory");
        if (fromPom != null)
            Add(roots, repositoryRoot, fromPom);

        if (Directory.Exists(Path.Combine(repositoryRoot, MavenConvention)))
            Add(roots, repositoryRoot, MavenConvention);

        foreach (var root in configured ?? [])
            Add(roots, repositoryRoot, root);

        return roots;
    }

    /// <summary>
    /// Ruta relativa normalizada, o null si queda fuera del repositorio o es la propia raíz (retirar la raíz entera
    /// vaciaría el grafo).
    /// </summary>
    public static string? Normalize(string repositoryRoot, string path)
    {
        var value = path.Trim();
        foreach (var variable in new[] { "${basedir}", "${project.basedir}" })
        {
            if (value.StartsWith(variable, StringComparison.Ordinal))
                value = value[variable.Length..].TrimStart('/', '\\');
        }

        if (value.Length == 0)
            return null;

        var root = Path.GetFullPath(repositoryRoot);
        var full = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, value));
        var relative = Path.GetRelativePath(root, full).Replace('\\', '/').TrimEnd('/');

        if (relative is "." or "" || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return null;
        return relative;
    }

    /// <summary>True si <paramref name="relativePath"/> es una de las raíces o cuelga de ella.</summary>
    public static bool Contains(IEnumerable<string> roots, string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        return roots.Any(r =>
            path.Equals(r, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static void Add(List<string> roots, string repositoryRoot, string path)
    {
        var normalized = Normalize(repositoryRoot, path);
        if (normalized != null && !roots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            roots.Add(normalized);
    }
}
