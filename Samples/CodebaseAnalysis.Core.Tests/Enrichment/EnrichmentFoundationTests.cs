using System.Text;
using CodebaseAnalysis.Core.Enrichment;
using Microsoft.Extensions.Configuration;

namespace CodebaseAnalysis.Core.Tests.Enrichment;

public class RepositoryScanTests
{
    private static void Write(string root, string relative, string text = "")
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    [Fact]
    public void Test_repo_activates_java_and_finds_web_files_without_the_test_root()
    {
        using var temp = new TempDirectory();
        var root = JavaTestRepo.WriteTo(temp.Path);

        var testRoots = TestRoots.Resolve(root);
        var scan = RepositoryScan.Scan(root, testRoots);

        Assert.Equal(["test"], testRoots);
        Assert.Equal(DetectedLanguages.Java, scan.Languages);
        Assert.Equal(JavaTestRepo.SourceFiles.OrderBy(p => p, StringComparer.Ordinal), scan.JavaFiles);
        Assert.Empty(scan.CSharpFiles);
        Assert.Equal([JavaTestRepo.StrutsConfigPath], scan.StrutsConfigs);
        Assert.Equal([JavaTestRepo.TilesDefsPath], scan.TilesDefinitions);
        Assert.Equal(4, scan.JspFiles.Count);
        Assert.Equal(["WebContent"], scan.WebRoots);
        Assert.Equal(Path.Combine(root, "src", "com", "acme", "Bean.java"), scan.FullPath(JavaTestRepo.BeanPath));
    }

    [Theory]
    [InlineData(new[] { "src/A.cs" }, DetectedLanguages.CSharp)]
    [InlineData(new[] { "src/A.java", "tools/B.cs" }, DetectedLanguages.Java | DetectedLanguages.CSharp)]
    [InlineData(new[] { "README.md", "app/index.ts" }, DetectedLanguages.None)]
    [InlineData(new string[0], DetectedLanguages.None)]
    public void Languages_follow_the_files_on_disk(string[] files, DetectedLanguages expected)
    {
        using var temp = new TempDirectory();
        foreach (var file in files)
            Write(temp.Path, file);

        Assert.Equal(expected, RepositoryScan.Scan(temp.Path, []).Languages);
    }

    [Fact]
    public void Build_output_tooling_folders_and_test_roots_are_not_scanned()
    {
        using var temp = new TempDirectory();
        foreach (var excluded in new[] { "target", "build", ".gradle", ".idea", "out", "bin", "obj", "node_modules", ".git", ".codebase" })
            Write(temp.Path, $"{excluded}/gen/X.java");
        Write(temp.Path, "src/test/java/com/acme/XTest.java");
        Write(temp.Path, "qa/it/YTest.java");
        Write(temp.Path, "qa/lib/junit.jar");
        Write(temp.Path, "src/main/java/com/acme/outline/Z.java");
        Write(temp.Path, "src/main/java/com/acme/Cabinet.java");
        Write(temp.Path, "lib/a.jar");

        var testRoots = TestRoots.Resolve(temp.Path, ["qa"]);
        var scan = RepositoryScan.Scan(temp.Path, testRoots);

        Assert.Equal(["src/test/java", "qa"], testRoots);
        Assert.Equal(["src/main/java/com/acme/Cabinet.java", "src/main/java/com/acme/outline/Z.java"], scan.JavaFiles);
        Assert.Equal(["lib/a.jar"], scan.Jars);
    }

    [Fact]
    public void Build_output_names_are_packages_unless_at_the_root_or_beside_a_build_file()
    {
        using var temp = new TempDirectory();
        Write(temp.Path, "src/com/acme/out/X.java");
        Write(temp.Path, "src/com/acme/build/Y.java");
        Write(temp.Path, "target/X.java");
        Write(temp.Path, "modulo/pom.xml");
        Write(temp.Path, "modulo/target/Gen.java");
        Write(temp.Path, "modulo/src/com/acme/target/Z.java");
        Write(temp.Path, "app/build.gradle.kts");
        Write(temp.Path, "app/build/Gen.java");
        Write(temp.Path, "legacy/build.xml");
        Write(temp.Path, "legacy/out/Gen.java");
        Write(temp.Path, "src/com/acme/.idea/W.java");

        var scan = RepositoryScan.Scan(temp.Path, []);

        Assert.Equal(["modulo/src/com/acme/target/Z.java", "src/com/acme/build/Y.java", "src/com/acme/out/X.java"], scan.JavaFiles);
    }

    [Fact]
    public void Excluded_directory_is_honoured()
    {
        using var temp = new TempDirectory();
        Write(temp.Path, "src/A.java");
        Write(temp.Path, "reports/B.java");

        var scan = RepositoryScan.Scan(temp.Path, [], Path.Combine(temp.Path, "reports"));

        Assert.Equal(["src/A.java"], scan.JavaFiles);
    }
}

public class TestRootsTests
{
    private static void Pom(string root, string build) =>
        File.WriteAllText(Path.Combine(root, "pom.xml"), $"""
            <?xml version="1.0"?>
            <project><build>{build}</build></project>
            """);

    [Theory]
    [InlineData("<testSourceDirectory>./test</testSourceDirectory>", "test")]
    [InlineData("<testSourceDirectory>test/</testSourceDirectory>", "test")]
    [InlineData("<testSourceDirectory>${basedir}/src/it</testSourceDirectory>", "src/it")]
    [InlineData("<testSourceDirectory>${project.basedir}/qa</testSourceDirectory>", "qa")]
    public void Pom_test_source_directory_is_normalized(string build, string expected)
    {
        using var temp = new TempDirectory();
        Pom(temp.Path, build);

        Assert.Equal([expected], TestRoots.Resolve(temp.Path));
    }

    [Fact]
    public void Roots_outside_the_repository_the_root_itself_and_duplicates_are_dropped()
    {
        using var temp = new TempDirectory();
        Pom(temp.Path, "<testSourceDirectory>./test</testSourceDirectory>");

        var roots = TestRoots.Resolve(temp.Path, ["../elsewhere", ".", "test/", "./e2e", Path.Combine(temp.Path, "it")]);

        Assert.Equal(["test", "e2e", "it"], roots);
    }

    [Fact]
    public void Without_pom_or_convention_there_are_no_roots()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "pom.xml"), "<project><build>");

        Assert.Empty(TestRoots.Resolve(temp.Path));
    }

    [Fact]
    public void Contains_matches_by_path_segment()
    {
        string[] roots = ["test"];

        Assert.True(TestRoots.Contains(roots, "test"));
        Assert.True(TestRoots.Contains(roots, "test/com/A.java"));
        Assert.False(TestRoots.Contains(roots, "testing/A.java"));
        Assert.False(TestRoots.Contains(roots, "src/test/A.java"));
    }
}

public class SourceEncodingTests
{
    [Fact]
    public void Latin1_bytes_use_the_default_encoding()
    {
        var bytes = Encoding.Latin1.GetBytes("// Añade la acción");

        var (text, encoding) = SourceEncoding.Decode(bytes, Encoding.Latin1);

        Assert.Equal("// Añade la acción", text);
        Assert.Equal(Encoding.Latin1.WebName, encoding.WebName);
    }

    [Fact]
    public void Valid_utf8_wins_over_the_default_encoding()
    {
        var bytes = new UTF8Encoding(false).GetBytes("// Año de la acción");

        var (text, encoding) = SourceEncoding.Decode(bytes, Encoding.Latin1);

        Assert.Equal("// Año de la acción", text);
        Assert.Equal("utf-8", encoding.WebName);
    }

    [Fact]
    public void Utf8_bom_is_detected_and_stripped()
    {
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(false).GetBytes("class Año {}")).ToArray();

        var (text, encoding) = SourceEncoding.Decode(bytes, Encoding.Latin1);

        Assert.Equal("class Año {}", text);
        Assert.Equal("utf-8", encoding.WebName);
    }

    [Fact]
    public void Default_comes_from_configuration_then_pom_then_utf8()
    {
        using var temp = new TempDirectory();
        Assert.Equal("utf-8", SourceEncoding.ResolveDefault(temp.Path, "").Name);

        JavaTestRepo.WriteTo(temp.Path);
        var fromPom = SourceEncoding.ResolveDefault(temp.Path, null);
        Assert.Equal("iso-8859-1", fromPom.Name);
        Assert.Equal("pom.xml", fromPom.Origin);

        var fromConfig = SourceEncoding.ResolveDefault(temp.Path, "Cp1252");
        Assert.Equal("windows-1252", fromConfig.Name);
        Assert.Equal("JavaAnalyzer:DefaultEncoding", fromConfig.Origin);

        Assert.Throws<InvalidOperationException>(() => SourceEncoding.ResolveDefault(temp.Path, "no-existe"));
    }

    [Fact]
    public void Test_repo_files_decode_correctly_with_the_pom_default()
    {
        using var temp = new TempDirectory();
        var root = JavaTestRepo.WriteTo(temp.Path);
        var fallback = SourceEncoding.ResolveDefault(root, "").Encoding;

        var servicio = SourceEncoding.ReadFile(Path.Combine(root, JavaTestRepo.ServicioPath), fallback);
        var bean = SourceEncoding.ReadFile(Path.Combine(root, JavaTestRepo.BeanPath), fallback);

        Assert.Contains("Añade la acción", servicio.Text);
        Assert.Equal("iso-8859-1", servicio.Encoding.WebName);
        Assert.Contains("Año de la acción", bean.Text);
        Assert.Equal("utf-8", bean.Encoding.WebName);
    }
}

public class EnrichmentOptionsTests
{
    [Fact]
    public void Defaults_apply_without_configuration()
    {
        var java = JavaAnalyzerOptions.FromConfiguration(TestConfig.FromPairs());
        var struts = StrutsJspOptions.FromConfiguration(TestConfig.FromPairs());

        Assert.True(java.Enabled);
        Assert.Equal("", java.JdkPath);
        Assert.Equal(["-Xmx8g", "-XX:+UseG1GC"], java.JvmOptions);
        Assert.Equal(0, java.BatchSize);
        Assert.Equal(1, java.MaxParallelBatches);
        Assert.Empty(java.ProvidedClasspath);
        Assert.Empty(java.TestRoots);
        Assert.Equal("", java.DefaultEncoding);
        Assert.Equal(900, java.TimeoutSeconds);
        Assert.Equal(3600, java.StepTimeoutSeconds);
        Assert.Equal(TimeSpan.FromHours(1), java.StepTimeout);
        Assert.False(java.KeepIntermediate);
        Assert.True(struts.Enabled);
        Assert.Equal(3, struts.SuspiciousImportPatterns.Count);
        Assert.Single(struts.BusinessImportPatterns);
    }

    [Fact]
    public void Values_and_lists_are_read_from_the_sections()
    {
        var configuration = TestConfig.FromPairs(
            ("JavaAnalyzer:Enabled", "false"),
            ("JavaAnalyzer:JdkPath", @"C:\jdk17"),
            ("JavaAnalyzer:JvmOptions:0", "-Xmx2g"),
            ("JavaAnalyzer:BatchSize", "2000"),
            ("JavaAnalyzer:MaxParallelBatches", "2"),
            ("JavaAnalyzer:ProvidedClasspath:0", @"C:\provided"),
            ("JavaAnalyzer:ProvidedClasspath:1", @"C:\x\servlet-api.jar"),
            ("JavaAnalyzer:TestRoots:0", "qa"),
            ("JavaAnalyzer:DefaultEncoding", "ISO-8859-1"),
            ("JavaAnalyzer:TimeoutSeconds", "60"),
            ("JavaAnalyzer:StepTimeoutSeconds", "0"),
            ("JavaAnalyzer:KeepIntermediate", "true"),
            ("StrutsJsp:Enabled", "false"),
            ("StrutsJsp:SuspiciousImportPatterns:0", "^com\\.acme\\.dao"),
            ("StrutsJsp:BusinessImportPatterns:0", "\\.bo\\."));

        var java = JavaAnalyzerOptions.FromConfiguration(configuration);
        var struts = StrutsJspOptions.FromConfiguration(configuration);

        Assert.False(java.Enabled);
        Assert.Equal(@"C:\jdk17", java.JdkPath);
        Assert.Equal(["-Xmx2g"], java.JvmOptions);
        Assert.Equal(2000, java.BatchSize);
        Assert.Equal(2, java.MaxParallelBatches);
        Assert.Equal([@"C:\provided", @"C:\x\servlet-api.jar"], java.ProvidedClasspath);
        Assert.Equal(["qa"], java.TestRoots);
        Assert.Equal("ISO-8859-1", java.DefaultEncoding);
        Assert.Equal(60, java.TimeoutSeconds);
        Assert.Equal(0, java.StepTimeoutSeconds);
        Assert.Equal(Timeout.InfiniteTimeSpan, java.StepTimeout);
        Assert.True(java.KeepIntermediate);
        Assert.False(struts.Enabled);
        Assert.Equal(["^com\\.acme\\.dao"], struts.SuspiciousImportPatterns);
        Assert.Equal(["\\.bo\\."], struts.BusinessImportPatterns);
    }

    [Fact]
    public void Sample_appsettings_declares_both_sections_with_the_defaults()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.analysis.json");
        var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();
        var java = JavaAnalyzerOptions.FromConfiguration(configuration);
        var defaults = new JavaAnalyzerOptions();

        Assert.True(configuration.GetSection(JavaAnalyzerOptions.SectionName).Exists());
        Assert.True(configuration.GetSection(StrutsJspOptions.SectionName).Exists());
        Assert.Equal(defaults.Enabled, java.Enabled);
        Assert.Equal(defaults.JdkPath, java.JdkPath);
        Assert.Equal(defaults.JvmOptions, java.JvmOptions);
        Assert.Equal(defaults.BatchSize, java.BatchSize);
        Assert.Equal(defaults.MaxParallelBatches, java.MaxParallelBatches);
        Assert.Empty(java.ProvidedClasspath);
        Assert.Empty(java.TestRoots);
        Assert.Equal(defaults.DefaultEncoding, java.DefaultEncoding);
        Assert.Equal(defaults.TimeoutSeconds, java.TimeoutSeconds);
        Assert.Equal(defaults.StepTimeoutSeconds, java.StepTimeoutSeconds);
        Assert.Equal(defaults.KeepIntermediate, java.KeepIntermediate);
        Assert.Equal("false", configuration["JavaAnalyzer:KeepIntermediate"], ignoreCase: true);
        Assert.Equal(new StrutsJspOptions().SuspiciousImportPatterns, StrutsJspOptions.FromConfiguration(configuration).SuspiciousImportPatterns);
        Assert.Equal(new StrutsJspOptions().BusinessImportPatterns, StrutsJspOptions.FromConfiguration(configuration).BusinessImportPatterns);
    }
}

public class EnrichmentRunsTests
{
    private static EnrichmentRun Run(string status = EnrichmentRuns.StatusOk, string index = "p|2026-09-28T10:00:00Z", string config = "cfg", string version = "1") => new()
    {
        Enricher = "JavaAnalyzer",
        EnricherVersion = version,
        ToolVersion = "17.0.20.1",
        IndexFingerprint = index,
        ConfigFingerprint = config,
        StartedAt = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero),
        DurationMs = 1234,
        Status = status,
        Outcome = status == EnrichmentRuns.StatusOk
            ? new EnrichmentOutcome
            {
                FilesAnalyzed = 6, FilesUnmatched = 1, NodesCreated = 2, NodesUpdated = 10, NodesRemoved = 3,
                CbmEdgesReplaced = 4, EdgesCreated = 9, Unresolved = "{}", Diagnostics = "{\"error\":1}", Details = "{}"
            }
            : null,
        Message = status == EnrichmentRuns.StatusOk ? null : "sin JDK"
    };

    [Fact]
    public async Task Index_fingerprint_is_project_name_and_indexed_at()
    {
        using var temp = new TempDirectory();
        using var graph = await CbmTestGraph.CreateAsync(Path.Combine(temp.Path, "codebase.db"), "C-repo");
        await graph.NodeAsync("Class", "A", "C-repo.src.A.A", "src/A.java", 1, 2);

        Assert.Equal("C-repo|2026-09-28T10:00:00Z", await EnrichmentRuns.IndexFingerprintAsync(graph.Connection, CancellationToken.None));
    }

    [Fact]
    public async Task Index_fingerprint_carries_the_cbm_version_of_that_indexing_and_another_version_invalidates_it()
    {
        using var temp = new TempDirectory();
        using var graph = await CbmTestGraph.CreateAsync(Path.Combine(temp.Path, "codebase.db"), "C-repo");
        var conn = graph.Connection;
        var ct = CancellationToken.None;

        await EnrichmentRuns.RecordCbmVersionAsync(conn, "0.6.1", ct);
        var withVersion = await EnrichmentRuns.IndexFingerprintAsync(conn, ct);
        Assert.Equal("C-repo|2026-09-28T10:00:00Z|cbm=0.6.1", withVersion);
        await EnrichmentRuns.InsertAsync(conn, Run(index: withVersion!), ct);
        Assert.True(await EnrichmentRuns.HasSuccessfulRunAsync(conn, "JavaAnalyzer", "1", withVersion, "cfg", ct));

        // La misma indexación apuntada con otra versión de cbm ya no casa con la pasada anterior (H-9).
        await EnrichmentRuns.RecordCbmVersionAsync(conn, "0.7.0", ct);
        var otherVersion = await EnrichmentRuns.IndexFingerprintAsync(conn, ct);
        Assert.Equal("C-repo|2026-09-28T10:00:00Z|cbm=0.7.0", otherVersion);
        Assert.False(await EnrichmentRuns.HasSuccessfulRunAsync(conn, "JavaAnalyzer", "1", otherVersion, "cfg", ct));

        // Una indexación que no apuntó versión (otro indexed_at) no hereda la de la anterior.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE projects SET indexed_at = '2026-09-29T08:00:00Z'";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        Assert.Equal("C-repo|2026-09-29T08:00:00Z", await EnrichmentRuns.IndexFingerprintAsync(conn, ct));
    }

    [Theory]
    [InlineData("codebase-memory-mcp 0.6.1", "0.6.1")]
    [InlineData("level=info msg=mem.init budget_mb=16116\ncodebase-memory-mcp 0.6.1\r\n", "0.6.1")]
    [InlineData("codebase-memory-mcp v1.2.0-beta.1", "1.2.0-beta.1")]
    [InlineData("Usage: codebase-memory-mcp cli <tool>", null)]
    [InlineData("", null)]
    public void Cbm_version_is_read_from_the_version_line(string output, string? expected) =>
        Assert.Equal(expected, Core.Support.CodebaseMemoryLocator.ParseVersion(output));

    [Fact]
    public async Task Index_fingerprint_is_null_without_projects_table()
    {
        using var temp = new TempDirectory();
        var db = Path.Combine(temp.Path, "codebase.db");
        await Core.Support.GraphSchema.EnsureAsync(db, CancellationToken.None);
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}");
        await conn.OpenAsync();

        Assert.Null(await EnrichmentRuns.IndexFingerprintAsync(conn, CancellationToken.None));
        Assert.False(await EnrichmentRuns.HasSuccessfulRunAsync(conn, "JavaAnalyzer", "1", null, "cfg", CancellationToken.None));
    }

    [Fact]
    public async Task Successful_run_matches_only_the_same_enricher_version_and_fingerprints()
    {
        using var temp = new TempDirectory();
        using var graph = await CbmTestGraph.CreateAsync(Path.Combine(temp.Path, "codebase.db"), "p");
        var conn = graph.Connection;
        var ct = CancellationToken.None;

        Assert.False(await EnrichmentRuns.HasSuccessfulRunAsync(conn, "JavaAnalyzer", "1", "p|2026-09-28T10:00:00Z", "cfg", ct));

        await EnrichmentRuns.InsertAsync(conn, Run(EnrichmentRuns.StatusFailed), ct);
        Assert.False(await EnrichmentRuns.HasSuccessfulRunAsync(conn, "JavaAnalyzer", "1", "p|2026-09-28T10:00:00Z", "cfg", ct));

        var id = await EnrichmentRuns.InsertAsync(conn, Run(), ct);
        Assert.True(id > 0);
        Assert.True(await EnrichmentRuns.HasSuccessfulRunAsync(conn, "JavaAnalyzer", "1", "p|2026-09-28T10:00:00Z", "cfg", ct));
        Assert.False(await EnrichmentRuns.HasSuccessfulRunAsync(conn, "JavaAnalyzer", "2", "p|2026-09-28T10:00:00Z", "cfg", ct));
        Assert.False(await EnrichmentRuns.HasSuccessfulRunAsync(conn, "JavaAnalyzer", "1", "p|2026-09-29T10:00:00Z", "cfg", ct));
        Assert.False(await EnrichmentRuns.HasSuccessfulRunAsync(conn, "JavaAnalyzer", "1", "p|2026-09-28T10:00:00Z", "otra", ct));
        Assert.False(await EnrichmentRuns.HasSuccessfulRunAsync(conn, "StrutsJspPlugin", "1", "p|2026-09-28T10:00:00Z", "cfg", ct));
    }

    [Fact]
    public async Task Inserted_rows_round_trip_with_all_columns()
    {
        using var temp = new TempDirectory();
        using var graph = await CbmTestGraph.CreateAsync(Path.Combine(temp.Path, "codebase.db"), "p");
        var ct = CancellationToken.None;
        await EnrichmentRuns.InsertAsync(graph.Connection, Run(EnrichmentRuns.StatusSkipped), ct);
        var okId = await EnrichmentRuns.InsertAsync(graph.Connection, Run(), ct);

        var runs = await EnrichmentRuns.ListAsync(graph.Connection, ct, "JavaAnalyzer");

        Assert.Equal(2, runs.Count);
        Assert.Null(runs[0].Outcome);
        Assert.Equal("sin JDK", runs[0].Message);
        var ok = runs[1];
        Assert.Equal(okId, ok.Id);
        Assert.Equal(Run() with { Id = okId, Outcome = Run().Outcome! with { RunId = okId } }, ok);
    }

    [Fact]
    public void Config_fingerprint_depends_on_values_and_their_boundaries()
    {
        var a = EnrichmentRuns.ConfigFingerprint(["a", "bc"]);

        Assert.Equal(64, a.Length);
        Assert.Equal(a, EnrichmentRuns.ConfigFingerprint(["a", "bc"]));
        Assert.NotEqual(a, EnrichmentRuns.ConfigFingerprint(["ab", "c"]));
        Assert.NotEqual(EnrichmentRuns.ConfigFingerprint([null]), EnrichmentRuns.ConfigFingerprint([""]));
    }
}

public class EnrichmentMarksTests
{
    private const string Enricher = "JavaAnalyzer";

    [Fact]
    public async Task Previous_output_is_removed_even_with_invalid_json_nodes_present()
    {
        using var temp = new TempDirectory();
        using var graph = await CbmTestGraph.CreateAsync(Path.Combine(temp.Path, "codebase.db"), "p");
        var broken = await graph.NodeAsync("Class", "Roto", "p.src.Roto.Roto", "src/Roto.java", 1, 9,
            "{\"base_classes\":[\"extends\nBase\"]}");
        var a = await graph.NodeAsync("Method", "a", "p.src.A.A.a", "src/A.java", 2, 3, "{\"enriched_by\":[\"JavaAnalyzer\"]}");
        var b = await graph.NodeAsync("Method", "b", "p.src.A.A.b", "src/A.java", 4, 5);
        var created = await graph.NodeAsync("Method", "a(int)", "p.src.A.A.a(int)", "src/A.java", 6, 7,
            "{\"origin\":\"enricher\",\"enriched_by\":[\"JavaAnalyzer\"],\"enriched_at\":\"2026-09-28T00:00:00Z\"}");
        var otherEnricher = await graph.NodeAsync("Route", "/x", "__route__ANY__/x", "WebContent/WEB-INF/struts-config.xml", 3, 3,
            "{\"origin\":\"enricher\",\"enriched_by\":[\"StrutsJspPlugin\"]}");
        await graph.VectorAsync(created);
        await graph.VectorAsync(b);
        await graph.EdgeAsync(a, b, "CALLS", "{\"enriched_by\":[\"JavaAnalyzer\"],\"count\":1}");
        await graph.EdgeAsync(b, a, "CALLS", "{\"callee\":\"a\"}");
        await graph.EdgeAsync(b, created, "CALLS", "{}");
        // En cbm las aristas siempre llevan JSON válido: url_path_gen es json_extract(properties) y rechaza el inválido.
        await graph.EdgeAsync(broken, b, "CALLS", "{\"callee\":\"b\"}");
        await graph.EdgeAsync(otherEnricher, a, "HANDLES", "{\"enriched_by\":[\"StrutsJspPlugin\"]}");

        using var tx = graph.Connection.BeginTransaction();
        var (nodes, edges) = await EnrichmentMarks.RemovePreviousOutputAsync(graph.Connection, tx, Enricher, CancellationToken.None);
        tx.Commit();

        Assert.Equal(1, nodes);
        Assert.Equal(2, edges);
        Assert.Null(await graph.NodeIdAsync("p.src.A.A.a(int)"));
        Assert.NotNull(await graph.NodeIdAsync("p.src.A.A.a"));
        Assert.NotNull(await graph.NodeIdAsync("p.src.Roto.Roto"));
        Assert.NotNull(await graph.NodeIdAsync("__route__ANY__/x"));
        Assert.Equal(3, await graph.CountAsync("SELECT COUNT(1) FROM edges"));
        Assert.Equal(1, await graph.CountAsync("SELECT COUNT(1) FROM node_vectors"));
    }

    [Fact]
    public async Task Nodes_under_test_roots_are_removed_with_edges_in_both_directions_and_vectors()
    {
        using var temp = new TempDirectory();
        using var graph = await CbmTestGraph.CreateAsync(Path.Combine(temp.Path, "codebase.db"), "p");
        var folder = await graph.NodeAsync("Folder", "test", "p.test", "test");
        var testMethod = await graph.NodeAsync("Method", "probar", "p.test.com.acme.ServicioTest.ServicioTest.probar", "test/com/acme/ServicioTest.java", 4, 4);
        var keep1 = await graph.NodeAsync("Class", "T", "p.testing.T.T", "testing/T.java", 1, 1);
        var keep2 = await graph.NodeAsync("Class", "S", "p.src.test_x.S.S", "src/test_x/S.java", 1, 1);
        await graph.VectorAsync(testMethod);
        await graph.EdgeAsync(testMethod, keep1, "CALLS");
        await graph.EdgeAsync(keep2, testMethod, "TESTS");
        await graph.EdgeAsync(folder, keep1, "CONTAINS_FILE");
        await graph.EdgeAsync(keep1, keep2, "CALLS");

        using var tx = graph.Connection.BeginTransaction();
        var (nodes, edges) = await EnrichmentMarks.RemoveNodesUnderAsync(graph.Connection, tx, ["test/", "test_"], CancellationToken.None);
        tx.Commit();

        Assert.Equal(2, nodes);
        Assert.Equal(3, edges);
        Assert.Equal(2, await graph.CountAsync("SELECT COUNT(1) FROM nodes"));
        Assert.Equal(1, await graph.CountAsync("SELECT COUNT(1) FROM edges"));
        Assert.Equal(0, await graph.CountAsync("SELECT COUNT(1) FROM node_vectors"));
    }

    [Fact]
    public void Merge_wraps_invalid_json_and_keeps_other_enrichers_marks()
    {
        var wrapped = EnrichmentMarks.Merge("{\"base_classes\":[\"a\nb\"]}", new() { ["cyclomatic"] = 3, ["docstring"] = null }, Enricher);
        Assert.Equal("{\"base_classes\":[\"a\nb\"]}", wrapped[EnrichmentMarks.RawPropertiesKey]!.GetValue<string>());
        Assert.Equal(3, wrapped["cyclomatic"]!.GetValue<int>());
        Assert.False(wrapped.ContainsKey("docstring"));
        Assert.Equal("[\"JavaAnalyzer\"]", wrapped["enriched_by"]!.ToJsonString());

        var merged = EnrichmentMarks.Merge("{\"docstring\":\"cbm\",\"enriched_by\":[\"StrutsJspPlugin\",\"JavaAnalyzer\"]}",
            new() { ["docstring"] = null, ["is_static"] = true }, Enricher);
        Assert.Equal("cbm", merged["docstring"]!.GetValue<string>());
        Assert.True(merged["is_static"]!.GetValue<bool>());
        Assert.Equal("[\"StrutsJspPlugin\",\"JavaAnalyzer\"]", merged["enriched_by"]!.ToJsonString());
    }

    [Fact]
    public void Created_nodes_and_edges_carry_the_marks()
    {
        var node = EnrichmentMarks.CreatedNodeProperties(Enricher, new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero), new() { ["overload_of"] = "q" });
        var edge = EnrichmentMarks.EdgeProperties(Enricher, new() { ["count"] = 2 });

        Assert.Equal("enricher", node["origin"]!.GetValue<string>());
        Assert.Equal("[\"JavaAnalyzer\"]", node["enriched_by"]!.ToJsonString());
        Assert.Equal("2026-09-28T12:00:00.0000000+00:00", node["enriched_at"]!.GetValue<string>());
        Assert.Equal("q", node["overload_of"]!.GetValue<string>());
        Assert.Equal("{\"enriched_by\":[\"JavaAnalyzer\"],\"count\":2}", edge.ToJsonString());
    }

    [Fact]
    public void The_cbm_cache_database_is_never_a_working_copy()
    {
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codebase-memory-mcp");

        Assert.Throws<InvalidOperationException>(() => EnrichmentMarks.AssertWorkingCopy(Path.Combine(cache, "repo.db")));
        Assert.Throws<InvalidOperationException>(() => EnrichmentMarks.AssertWorkingCopy(Path.Combine(cache, "sub", "..", "repo.db")));
        EnrichmentMarks.AssertWorkingCopy(Path.Combine(Path.GetTempPath(), ".codebase", "codebase.db"));
        EnrichmentMarks.AssertWorkingCopy(cache + "-copia.db");
    }
}
