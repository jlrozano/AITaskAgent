using CodebaseAnalysis.Core.Enrichment;

namespace CodebaseAnalysis.Core.Preparation;

/// <summary>Los tipos de source set del inventario (<see cref="SourceSetSummary.SourceSet"/>, <see cref="Subsystem.SourceSet"/>).</summary>
public static class SourceSets
{
    public const string Production = "production";
    public const string Test = "test";
    public const string Qa = "qa";
    public const string Debug = "debug";
    public const string Generated = "generated";
    public const string Samples = "samples";
    public const string Unknown = "unknown";

    /// <summary>Todos, en el orden en que se listan en el inventario.</summary>
    public static readonly IReadOnlyList<string> All = [Production, Test, Qa, Debug, Generated, Samples, Unknown];

    /// <summary>Solo producción cuenta para las líneas de código, el mapa de dominios y el plan de cobertura.</summary>
    public static bool IsProduction(string sourceSet) => sourceSet == Production;
}

/// <summary>
/// Clasifica una ruta relativa al repositorio en un source set por convención de ruta (T3). Función pura, sin tocar el disco.
/// No adivina: la ausencia de evidencia es <see cref="SourceSets.Unknown"/>.
/// </summary>
/// <remarks>
/// <para>Precedencia: <c>generated</c> &gt; raíz de test configurada &gt; <c>test</c> &gt; <c>qa</c> &gt; <c>debug</c> &gt; <c>samples</c>
/// &gt; producción. Un proyecto de test dentro de <c>samples/</c> es test, y un fichero generado en <c>src/test</c> es generado.</para>
/// <para>Producción tiene dos pruebas: la convención Maven/Gradle <c>src/main</c> (que además exime los paquetes que se llamen
/// <c>test</c>, <c>debug</c> o <c>samples</c> por debajo, no lo generado) y, sin ella, ser un fichero con extensión de código
/// que no cae en ninguna otra categoría ni en una carpeta de herramientas de la raíz (<c>tools</c>, <c>scripts</c>, <c>docs</c>...): es
/// el caso de los repositorios Ant y .NET, donde el código fuente de producción no lleva ninguna marca. Todo lo demás (un
/// <c>README</c>, un script, una configuración suelta) es <c>unknown</c>.</para>
/// </remarks>
public static class SourceSetClassifier
{
    private static readonly HashSet<string> GeneratedDirectories = new(StringComparer.Ordinal)
    {
        "generated", "generated-sources", "generated-test-sources", "generated_sources", "gensrc", "__generated__", ".generated",
        "target", "obj"
    };

    private static readonly string[] GeneratedFileSuffixes =
        [".g.cs", ".g.i.cs", ".generated.cs", ".designer.cs", ".min.js", ".min.css", ".bundle.js", "_pb2.py", ".pb.go"];

    private static readonly HashSet<string> TestDirectories = new(StringComparer.Ordinal)
    {
        "test", "tests", "__tests__", "unittest", "unittests", "integrationtest", "integrationtests", "functionaltest",
        "functionaltests", "testfixtures", "androidtest", "uitest"
    };

    private static readonly string[] TestDirectorySuffixes =
        [".tests", ".test", ".unittests", ".integrationtests", ".specs", "-tests", "-test", "_tests", "_test"];

    private static readonly HashSet<string> SampleDirectories = new(StringComparer.Ordinal)
    {
        "samples", "sample", "examples", "example", "demo", "demos"
    };

    /// <summary>
    /// Carpetas donde el código, si lo hay, no es de la aplicación y no se puede decir qué es. Solo cuentan en la raíz del
    /// repositorio (primer segmento de la ruta): <c>tools/release.java</c> es un script, pero <c>Core/LLM/Tools/ITool.cs</c> es una
    /// función del producto que se llama «Tools».
    /// </summary>
    private static readonly HashSet<string> UnknownRootDirectories = new(StringComparer.Ordinal)
    {
        "tools", "scripts", "script", "buildsrc", "docs", "doc", "ci", "benchmarks", "benchmark"
    };

    /// <summary>Carpetas ocultas de infraestructura del repositorio: no son la aplicación a cualquier profundidad.</summary>
    private static readonly HashSet<string> UnknownAnywhereDirectories = new(StringComparer.Ordinal) { ".github", ".vscode" };

    /// <param name="relativePath">Ruta relativa al repositorio, con <c>/</c> o <c>\</c>.</param>
    /// <param name="codeExtensions">Extensiones (con punto, en minúsculas) que son código; por defecto <see cref="PreparationOptions.DefaultCountedExtensions"/>.</param>
    /// <param name="testRoots">Raíces de test del repositorio (<see cref="TestRoots.Resolve"/>), relativas y con <c>/</c>.</param>
    public static string Classify(string relativePath, IReadOnlyCollection<string>? codeExtensions = null, IReadOnlyList<string>? testRoots = null)
    {
        var path = (relativePath ?? "").Trim().Replace('\\', '/').Trim('/');
        if (path.Length == 0)
            return SourceSets.Unknown;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var fileName = segments[^1];
        var directories = segments[..^1].Select(s => s.ToLowerInvariant()).ToArray();
        var lowerFile = fileName.ToLowerInvariant();

        if (directories.Any(GeneratedDirectories.Contains) || GeneratedFileSuffixes.Any(suffix => lowerFile.EndsWith(suffix, StringComparison.Ordinal)))
            return SourceSets.Generated;

        if (testRoots is { Count: > 0 } && TestRoots.Contains(testRoots, path))
            return SourceSets.Test;

        // src/main: lo que cuelga de él (un paquete llamado «test») es producción; las marcas válidas son las de antes.
        var mainIndex = IndexOfSrcMain(directories);
        var scanned = mainIndex >= 0 ? directories[..mainIndex] : directories;

        if (HasTestDirectory(scanned) || (mainIndex < 0 && IsTestFile(fileName, lowerFile)))
            return SourceSets.Test;
        if (scanned.Contains("qa"))
            return SourceSets.Qa;
        if (scanned.Contains("debug"))
            return SourceSets.Debug;
        if (scanned.Any(SampleDirectories.Contains))
            return SourceSets.Samples;

        if (mainIndex >= 0)
            return SourceSets.Production;
        if (scanned.Any(UnknownAnywhereDirectories.Contains) || (scanned.Length > 0 && UnknownRootDirectories.Contains(scanned[0])))
            return SourceSets.Unknown;

        var extension = Path.GetExtension(lowerFile);
        var isCode = extension.Length > 0 && (codeExtensions ?? PreparationOptions.DefaultCountedExtensions).Contains(extension);
        return isCode ? SourceSets.Production : SourceSets.Unknown;
    }

    private static int IndexOfSrcMain(string[] directories)
    {
        for (var i = 0; i + 1 < directories.Length; i++)
        {
            if (directories[i] == "src" && directories[i + 1] == "main")
                return i;
        }
        return -1;
    }

    /// <summary>Una carpeta de test por nombre (<c>test</c>, <c>Foo.Tests</c>, <c>foo-test</c>) o <c>src/it</c> (integración de Gradle).</summary>
    private static bool HasTestDirectory(string[] directories)
    {
        for (var i = 0; i < directories.Length; i++)
        {
            var directory = directories[i];
            if (TestDirectories.Contains(directory) || TestDirectorySuffixes.Any(suffix => directory.EndsWith(suffix, StringComparison.Ordinal)))
                return true;
            if (directory == "it" && i > 0 && directories[i - 1] == "src")
                return true;
        }
        return false;
    }

    /// <summary>
    /// Fichero de test por su nombre: <c>FooTest</c>/<c>FooTests</c>/<c>FooTestCase</c> y <c>TestFoo</c> (con la T mayúscula,
    /// para no confundir <c>Contest</c> o <c>Latest</c>) y <c>foo.spec.ts</c>/<c>foo.test.js</c>.
    /// </summary>
    private static bool IsTestFile(string fileName, string lowerFile)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var lowerStem = Path.GetFileNameWithoutExtension(lowerFile);
        if (lowerStem.EndsWith(".spec", StringComparison.Ordinal) || lowerStem.EndsWith(".test", StringComparison.Ordinal))
            return true;

        foreach (var suffix in new[] { "Test", "Tests", "TestCase" })
        {
            if (stem.Length > suffix.Length && stem.EndsWith(suffix, StringComparison.Ordinal))
                return true;
        }

        return stem.Length > 4 && stem.StartsWith("Test", StringComparison.Ordinal) && char.IsUpper(stem[4]);
    }
}
