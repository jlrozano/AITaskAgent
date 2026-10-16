using System.Text;
using CodebaseAnalysis.Core.Preparation;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="RepositoryFileWalker"/> y <see cref="ProductionLineCounter"/> (T4): líneas físicas contadas en disco, por fichero y
/// por lenguaje, con lectura tolerante de la codificación, sin seguir enlaces ni entrar en las carpetas de herramientas.
/// </summary>
public sealed class ProductionLineCounterTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Write(string relative, string content, Encoding? encoding = null)
    {
        var path = Path.Combine(_temp.Path, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        encoding ??= new UTF8Encoding(false);
        File.WriteAllBytes(path, [.. encoding.GetPreamble(), .. encoding.GetBytes(content)]);
        return path;
    }

    private static readonly PreparationOptions Options = new();

    [Fact]
    public void It_counts_physical_lines_per_file_and_per_language()
    {
        Write("src/main/java/a/A.java", "class A {\n  int x;\n}\n");
        Write("src/main/java/a/B.java", "class B {}");
        Write("web/Page.jsp", "<html>\r\n<body/>\r\n</html>\r\n");
        Write("Billing/Invoice.cs", "namespace X;\r\nclass Invoice {}\r\n");

        var result = ProductionLineCounter.Count(_temp.Path, Options);
        var production = result.Production;

        Assert.Equal(
            [("Billing/Invoice.cs", "csharp", 2), ("src/main/java/a/A.java", "java", 3), ("src/main/java/a/B.java", "java", 1), ("web/Page.jsp", "jsp", 3)],
            production.Select(f => (f.Path, f.Language, f.Lines)));
        Assert.Equal(9, production.Sum(f => (long)f.Lines));

        var languages = ProductionLineCounter.ByLanguage(production);
        Assert.Equal([("csharp", 1, 2L), ("java", 2, 4L), ("jsp", 1, 3L)], languages.Select(l => (l.Language, l.Files, l.Lines)));
    }

    [Fact]
    public void A_last_line_without_a_terminator_counts_and_an_empty_file_is_zero()
    {
        Write("a/NoNewline.java", "one\ntwo");
        Write("a/Empty.java", "");
        Write("a/BlankLines.java", "\n\n\n");
        Write("a/OnlyText.java", "x");

        var lines = ProductionLineCounter.Count(_temp.Path, Options).Production.ToDictionary(f => f.Path, f => f.Lines);

        Assert.Equal(2, lines["a/NoNewline.java"]);
        Assert.Equal(0, lines["a/Empty.java"]);
        Assert.Equal(3, lines["a/BlankLines.java"]);
        Assert.Equal(1, lines["a/OnlyText.java"]);
    }

    [Fact]
    public void A_latin1_file_is_counted_without_failing()
    {
        Write("a/Acentos.java", "// descripción\nclass Acentos { String s = \"ñandú\"; }\n", Encoding.Latin1);
        File.WriteAllBytes(Path.Combine(_temp.Path, "a", "Roto.java"), [0x63, 0xE9, 0x0A, 0xFF, 0xFE, 0x0A, 0x61]);

        var lines = ProductionLineCounter.Count(_temp.Path, Options).Production.ToDictionary(f => f.Path, f => f.Lines);

        Assert.Equal(2, lines["a/Acentos.java"]);
        Assert.Equal(3, lines["a/Roto.java"]);
    }

    [Fact]
    public void A_utf16_file_with_bom_is_counted_by_its_text_lines()
    {
        Write("a/Wide.java", "uno\ndos\ntres\n", new UnicodeEncoding(false, true));
        Write("a/Wide2.java", "uno\r\ndos", new UnicodeEncoding(true, true));

        var lines = ProductionLineCounter.Count(_temp.Path, Options).Production.ToDictionary(f => f.Path, f => f.Lines);

        Assert.Equal(3, lines["a/Wide.java"]);
        Assert.Equal(2, lines["a/Wide2.java"]);
    }

    [Fact]
    public void Only_production_files_with_a_counted_extension_are_in_production()
    {
        Write("src/main/java/A.java", "a\n");
        Write("src/test/java/ATest.java", "a\nb\n");
        Write("src/main/resources/app.properties", "k=v\n");
        Write("docs/readme.md", "# x\n");
        Write("Qa/Smoke.java", "s\n");
        Write("web/app.js", "j\n");
        Write("web/app.min.js", "m\n");

        var result = ProductionLineCounter.Count(_temp.Path, Options);

        Assert.Equal(["src/main/java/A.java", "web/app.js"], result.Production.Select(f => f.Path));
        // Los demás source sets con código también se cuentan, para el resumen del inventario.
        Assert.Equal(["Qa/Smoke.java", "src/main/java/A.java", "src/test/java/ATest.java", "web/app.js", "web/app.min.js"], result.Files.Select(f => f.Path));
        Assert.Equal(["qa", "production", "test", "production", "generated"], result.Files.Select(f => f.SourceSet));
    }

    [Fact]
    public void The_counted_extensions_are_configurable()
    {
        Write("a/A.java", "a\n");
        Write("a/b.py", "p1\np2\n");

        var result = ProductionLineCounter.Count(_temp.Path, new PreparationOptions { CountedExtensions = [".py"] });

        Assert.Equal([("a/b.py", "python", 2)], result.Production.Select(f => (f.Path, f.Language, f.Lines)));
    }

    [Fact]
    public void Tooling_directories_build_outputs_and_the_reports_folder_are_not_walked()
    {
        Write("pom.xml", "<project/>");
        Write("target/classes/Gen.java", "g\n");
        Write("build/x/Out.java", "g\n");
        Write("node_modules/lib/index.js", "n\n");
        Write(".git/hooks/Hook.java", "h\n");
        Write("bin/Debug/App.cs", "b\n");
        Write(".idea/Plugin.java", "i\n");
        Write("reports/Report.java", "r\n");
        Write("src/main/java/Keep.java", "k\n");
        // Un paquete llamado como una salida de compilación, lejos de un fichero de build, sí es código.
        Write("src/main/java/com/acme/out/Real.java", "r\n");

        var result = ProductionLineCounter.Count(_temp.Path, Options, excludedDirectory: Path.Combine(_temp.Path, "reports"));

        Assert.Equal(["src/main/java/Keep.java", "src/main/java/com/acme/out/Real.java"], result.Files.Select(f => f.Path));
    }

    [Fact]
    public void A_build_output_folder_without_a_build_file_beside_it_is_a_package()
    {
        Write("lib/build/Real.java", "r\n");
        Write("lib/target/Real2.java", "r\n");

        var result = ProductionLineCounter.Count(_temp.Path, Options);

        Assert.Equal(["lib/build/Real.java", "lib/target/Real2.java"], result.Files.Select(f => f.Path));
        // Pero el clasificador toma «target» por salida generada aunque el recorrido entrase: solo «build» cuenta como producción.
        Assert.Equal(["lib/build/Real.java"], result.Production.Select(f => f.Path));
    }

    [Fact]
    public void Links_are_ignored()
    {
        var real = Write("real/A.java", "a\nb\n");
        var file = Path.Combine(_temp.Path, "src", "Link.java");
        var directory = Path.Combine(_temp.Path, "linked");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            File.CreateSymbolicLink(file, real);
            Directory.CreateSymbolicLink(directory, Path.Combine(_temp.Path, "real"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Sin permiso para crear enlaces simbólicos en esta máquina (Windows sin modo desarrollador): no hay nada que comprobar.
            return;
        }

        var result = ProductionLineCounter.Count(_temp.Path, Options);

        Assert.Equal(["real/A.java"], result.Files.Select(f => f.Path));
    }

    [Fact]
    public void The_result_is_the_same_in_two_runs_and_ordered_by_ordinal_path()
    {
        foreach (var name in new[] { "b/Z.java", "c/a.java", "a/m.java", "a/N.java", "_x/q.java", "a/10.java", "a/9.java" })
            Write(name, "x\n");

        var first = ProductionLineCounter.Count(_temp.Path, Options).Files.Select(f => f.Path).ToList();
        var second = ProductionLineCounter.Count(_temp.Path, Options).Files.Select(f => f.Path).ToList();

        Assert.Equal(first, second);
        Assert.Equal(first.OrderBy(p => p, StringComparer.Ordinal), first);
    }

    [Fact]
    public void An_unreadable_file_is_a_warning_and_not_an_error()
    {
        var path = Write("a/Locked.java", "x\n");
        Write("a/Fine.java", "y\n");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = ProductionLineCounter.Count(_temp.Path, Options);

        Assert.Contains(result.Production, f => f.Path == "a/Fine.java" && f.Lines == 1);
        Assert.Single(result.Warnings);
        Assert.Contains("Locked.java", result.Warnings[0]);
    }

    [Fact]
    public void The_walker_lists_every_non_skipped_file_relative_with_slashes_and_sorted()
    {
        Write("pom.xml", "<project/>");
        Write("a/b/c.txt", "x");
        Write("a/B.java", "x");
        Write(".git/config", "x");

        Assert.Equal(["a/B.java", "a/b/c.txt", "pom.xml"], RepositoryFileWalker.Walk(_temp.Path));
        Assert.Empty(RepositoryFileWalker.Walk(Path.Combine(_temp.Path, "no-existe")));
    }

    [Fact]
    public void Test_roots_are_honoured_by_the_counter()
    {
        Write("qa-suite/unit/FooCase.java", "a\nb\n");
        Write("main/Foo.java", "a\n");

        var result = ProductionLineCounter.Count(_temp.Path, Options, testRoots: ["qa-suite/unit"]);

        Assert.Equal(["main/Foo.java"], result.Production.Select(f => f.Path));
    }
}
