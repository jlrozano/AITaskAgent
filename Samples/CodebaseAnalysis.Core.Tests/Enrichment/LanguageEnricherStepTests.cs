using System.Text;
using System.Text.Json.Nodes;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Enrichment.Java;
using CodebaseAnalysis.Core.Enrichment.Web;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace CodebaseAnalysis.Core.Tests.Enrichment;

/// <summary>Contexto de pipeline con configuración y reporter para ejecutar los pasos de enriquecimiento sueltos.</summary>
internal static class EnrichmentStepContext
{
    public static PipelineContext Create(IConfiguration configuration, CapturingReporter reporter, string repository, string database)
    {
        var services = new ServiceCollection().AddSingleton(configuration).BuildServiceProvider();
        var context = new PipelineContext(null, services);
        context.Set(AnalysisContextKeys.Reporter, reporter);
        context.Set(AnalysisContextKeys.PipelineInput, new AnalysisPipelineInput
        {
            RepositoryPath = repository,
            DatabasePath = database,
            OutputReportsPath = Path.Combine(repository, "reports")
        });
        return context;
    }
}

/// <summary>
/// <see cref="LanguageEnricherStep"/> (P1-P3, criterios 1, 8 y 10) sobre <see cref="JavaTestRepo"/> y el grafo sintético de
/// <see cref="JavaGraphFixture"/>. Los que necesitan el JDK van con <see cref="JdkFactAttribute"/>.
/// </summary>
public class LanguageEnricherStepTests(ITestOutputHelper output)
{
    private static readonly string RouteQn = "__route__ANY__/consultar";

    private sealed class Setup : IDisposable
    {
        public required TempDirectory Temp { get; init; }
        public required string Repo { get; init; }
        public required CbmTestGraph Graph { get; init; }

        public void Dispose()
        {
            Graph.Dispose();
            Temp.Dispose();
        }
    }

    /// <summary>Repositorio de prueba y copia sintética del grafo en una carpeta aparte (la BD no cuelga del repositorio).</summary>
    private static async Task<Setup> CreateAsync()
    {
        var temp = new TempDirectory();
        var repo = JavaTestRepo.WriteTo(Path.Combine(temp.Path, "repo"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "db"));
        var graph = await JavaGraphFixture.CreateGraphAsync(Path.Combine(temp.Path, "db", "codebase.db"));
        return new Setup { Temp = temp, Repo = repo, Graph = graph };
    }

    private static IConfiguration Config(params (string Key, string? Value)[] pairs) => TestConfig.FromPairs(pairs);

    private static IConfiguration JdkConfig(params (string Key, string? Value)[] extra) =>
        Config([("JavaAnalyzer:JdkPath", TestJdk.Info!.JdkPath), ("JavaAnalyzer:JvmOptions:0", "-Xmx1g"), .. extra]);

    private static async Task<(GraphStatusResult Result, CapturingReporter Reporter, PipelineContext Context)> RunAsync(
        Setup setup, IConfiguration configuration, GraphStatusResult? input = null)
    {
        var reporter = new CapturingReporter();
        var context = EnrichmentStepContext.Create(configuration, reporter, setup.Repo, setup.Graph.DatabasePath);
        input ??= new GraphStatusResult { DatabasePath = setup.Graph.DatabasePath, ExistedBefore = true, IsEnriched = true };
        var result = await new LanguageEnricherStep().ExecuteAsync(input, context, 1, null, CancellationToken.None);
        return (result, reporter, context);
    }

    private void Dump(CapturingReporter reporter)
    {
        foreach (var (level, source, message) in reporter.Entries.Where(e => !e.Message.StartsWith("   [java]", StringComparison.Ordinal)))
            output.WriteLine($"[{level}] {source}: {message}");
    }

    /// <summary>Todas las filas de nodos y aristas, para comparar el grafo entre pasadas.</summary>
    private static async Task<string> SnapshotAsync(CbmTestGraph graph)
    {
        var nodes = await graph.ListAsync(
            "SELECT id, label, qualified_name, file_path, start_line, end_line, properties FROM nodes ORDER BY id",
            r => $"{r.GetInt64(0)}|{r.GetString(1)}|{r.GetString(2)}|{r.GetString(3)}|{r.GetInt32(4)}|{r.GetInt32(5)}|{r.GetString(6)}");
        var edges = await graph.ListAsync(
            "SELECT id, source_id, target_id, type, properties FROM edges ORDER BY id",
            r => $"{r.GetInt64(0)}|{r.GetInt64(1)}|{r.GetInt64(2)}|{r.GetString(3)}|{r.GetString(4)}");
        return string.Join('\n', nodes.Concat(edges));
    }

    private const string TestNodesSql = "SELECT COUNT(1) FROM nodes WHERE file_path = 'test' OR file_path LIKE 'test/%'";

    /// <summary>Como <see cref="SnapshotAsync"/>, pero sin el código de test ni las aristas que lo tocan (D15).</summary>
    private static async Task<string> SnapshotOutsideTestAsync(CbmTestGraph graph)
    {
        const string outside = "SELECT id FROM nodes WHERE NOT (file_path = 'test' OR file_path LIKE 'test/%')";
        var nodes = await graph.ListAsync(
            $"SELECT id, label, qualified_name, file_path, start_line, end_line, properties FROM nodes WHERE id IN ({outside}) ORDER BY id",
            r => $"{r.GetInt64(0)}|{r.GetString(1)}|{r.GetString(2)}|{r.GetString(3)}|{r.GetInt32(4)}|{r.GetInt32(5)}|{r.GetString(6)}");
        var edges = await graph.ListAsync(
            $"SELECT id, source_id, target_id, type, properties FROM edges WHERE source_id IN ({outside}) AND target_id IN ({outside}) ORDER BY id",
            r => $"{r.GetInt64(0)}|{r.GetInt64(1)}|{r.GetInt64(2)}|{r.GetString(3)}|{r.GetString(4)}");
        return string.Join('\n', nodes.Concat(edges));
    }

    private static Task<List<string>> CallTargetsAsync(CbmTestGraph graph, string sourceQn) =>
        graph.ListAsync("""
            SELECT t.qualified_name FROM edges e
            JOIN nodes s ON s.id = e.source_id JOIN nodes t ON t.id = e.target_id
            WHERE e.type = 'CALLS' AND s.qualified_name = @qn ORDER BY t.qualified_name
            """, r => r.GetString(0), ("@qn", sourceQn));

    private static async Task<JsonObject> PropertiesAsync(CbmTestGraph graph, string qn) =>
        JsonNode.Parse((await graph.TextAsync("SELECT properties FROM nodes WHERE qualified_name = @qn", ("@qn", qn)))!)!.AsObject();

    private static Task<IReadOnlyList<EnrichmentRun>> RunsAsync(CbmTestGraph graph) =>
        EnrichmentRuns.ListAsync(graph.Connection, CancellationToken.None);

    [Fact]
    public async Task Without_java_files_the_step_does_nothing_and_writes_no_rows()
    {
        using var temp = new TempDirectory();
        var repo = Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        await File.WriteAllTextAsync(Path.Combine(repo, "src", "Sample.cs"), "namespace Demo; public class Sample { }");
        using var graph = await CbmTestGraph.CreateAsync(Path.Combine(temp.Path, "codebase.db"), "demo");
        var before = await SnapshotAsync(graph);
        var setup = new Setup { Temp = temp, Repo = repo, Graph = graph };

        var input = new GraphStatusResult { DatabasePath = graph.DatabasePath, TotalNodes = 7 };
        var (result, reporter, context) = await RunAsync(setup, Config(), input);

        Assert.Same(input, result);
        Assert.Equal(DetectedLanguages.CSharp, context.Get<DetectedLanguages>(AnalysisContextKeys.DetectedLanguages));
        var entry = Assert.Single(reporter.Entries);
        Assert.Equal((AnalysisLogLevel.Info, LanguageEnricherStep.StepName, "🔎 Lenguajes detectados en disco: C# (1 fichero .cs)."), entry);
        Assert.Empty(await RunsAsync(graph));
        Assert.Equal(0, await graph.CountAsync("SELECT COUNT(1) FROM sqlite_master WHERE name = 'enrichment_runs'"));
        Assert.Equal(before, await SnapshotAsync(graph));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "enrichment")));
    }

    [Fact]
    public async Task Without_a_jdk_the_step_warns_leaves_the_graph_intact_and_records_a_skipped_run()
    {
        using var setup = await CreateAsync();
        var before = await SnapshotOutsideTestAsync(setup.Graph);

        var (result, reporter, context) = await RunAsync(setup, Config(("JavaAnalyzer:JdkPath", ""), ("StrutsJsp:Enabled", "false")));
        Dump(reporter);

        const string expected = "⚠️ Analizador Java: no hay JDK configurado (JavaAnalyzer:JdkPath); hace falta un JDK 17 o superior. "
            + "Se continúa con el grafo de cbm.";
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Source == LanguageEnricherStep.StepName && e.Message == expected);
        Assert.DoesNotContain(reporter.Entries, e => e.Level == AnalysisLogLevel.Error);
        Assert.Equal(DetectedLanguages.Java, context.Get<DetectedLanguages>(AnalysisContextKeys.DetectedLanguages));
        // Intacto salvo el código de test, que se retira aunque no haya pasada (D15).
        Assert.Equal(before, await SnapshotOutsideTestAsync(setup.Graph));

        var run = Assert.Single(await RunsAsync(setup.Graph));
        Assert.Equal(JavaGraphWriter.EnricherName, run.Enricher);
        Assert.Equal(EnrichmentRuns.StatusSkipped, run.Status);
        Assert.Equal($"{JavaGraphFixture.Project}|{JavaGraphFixture.IndexedAt}", run.IndexFingerprint);
        Assert.StartsWith("no hay JDK configurado", run.Message);
        Assert.Equal(new EnrichmentOutcome { NodesRemoved = 5, RunId = run.Id }, run.Outcome);

        // Un GraphStatusResult nuevo con los recuentos de la BD, que conserva el resto de campos.
        Assert.True(result.IsEnriched);
        Assert.True(result.ExistedBefore);
        Assert.Equal(await setup.Graph.CountAsync("SELECT COUNT(1) FROM nodes"), result.TotalNodes);
        Assert.Equal(await setup.Graph.CountAsync("SELECT COUNT(1) FROM edges"), result.TotalEdges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_the_analyzer_pass_the_test_code_still_leaves_the_graph(bool disabled)
    {
        using var setup = await CreateAsync();
        var graph = setup.Graph;
        Assert.Equal(5, await graph.CountAsync(TestNodesSql));
        var outside = await SnapshotOutsideTestAsync(graph);

        // Sin JDK (fila skipped) o con el analizador desactivado (sin fila); sin Struts, para que solo cuente la retirada.
        var (result, reporter, _) = await RunAsync(setup, disabled
            ? Config(("JavaAnalyzer:Enabled", "false"), ("StrutsJsp:Enabled", "false"))
            : Config(("JavaAnalyzer:JdkPath", ""), ("StrutsJsp:Enabled", "false")));
        Dump(reporter);

        Assert.Equal(0, await graph.CountAsync(TestNodesSql));
        Assert.Equal(0, await graph.CountAsync("""
            SELECT COUNT(1) FROM edges e
            WHERE NOT EXISTS (SELECT 1 FROM nodes n WHERE n.id = e.source_id) OR NOT EXISTS (SELECT 1 FROM nodes n WHERE n.id = e.target_id)
            """));
        Assert.Equal(0, await graph.CountAsync("SELECT COUNT(1) FROM node_vectors v WHERE NOT EXISTS (SELECT 1 FROM nodes n WHERE n.id = v.node_id)"));
        Assert.Equal(outside, await SnapshotOutsideTestAsync(graph));
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Info && e.Source == LanguageEnricherStep.StepName
            && e.Message == "ℹ️ Analizador Java: sin su pasada, se retiran igualmente del grafo 5 nodos del código de test (test).");
        Assert.Equal(await graph.CountAsync("SELECT COUNT(1) FROM nodes"), result.TotalNodes);
        Assert.Equal(await graph.CountAsync("SELECT COUNT(1) FROM edges"), result.TotalEdges);

        var runs = await RunsAsync(graph);
        if (disabled)
            Assert.Empty(runs);
        else
            Assert.Equal(5, Assert.Single(runs).Outcome!.NodesRemoved);
    }

    [Fact]
    public async Task A_jdk_older_than_17_is_skipped_with_its_version_and_struts_still_runs()
    {
        using var setup = await CreateAsync();
        var jdk = Path.Combine(setup.Temp.Path, "jdk8");
        Directory.CreateDirectory(Path.Combine(jdk, "bin"));
        await File.WriteAllTextAsync(Path.Combine(jdk, "release"), "IMPLEMENTOR=\"Prueba\"\nJAVA_VERSION=\"1.8.0_391\"\n");

        var (_, reporter, _) = await RunAsync(setup, Config(("JavaAnalyzer:JdkPath", jdk)));
        Dump(reporter);

        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning
            && e.Message == $"⚠️ Analizador Java: el JDK de '{jdk}' es la versión 1.8.0_391; hace falta la 17 o superior. Se continúa con el grafo de cbm.");
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Success && e.Message.StartsWith("✅ Struts/JSP: 1 acción, 4 JSP (1 sospechosa)", StringComparison.Ordinal));
        var runs = await RunsAsync(setup.Graph);
        Assert.Equal([(JavaGraphWriter.EnricherName, EnrichmentRuns.StatusSkipped), (StrutsJspWriter.EnricherName, EnrichmentRuns.StatusOk)],
            runs.Select(r => (r.Enricher, r.Status)));
        Assert.Equal(0, await setup.Graph.CountAsync($"SELECT COUNT(1) FROM edges WHERE {EnrichmentMarks.EnrichedByPredicate("'JavaAnalyzer'")}"));
    }

    [Fact]
    public async Task A_failing_enricher_is_a_warning_and_a_failed_row_and_the_step_goes_on()
    {
        using var setup = await CreateAsync();

        // Una codificación que no existe hace fallar a los dos enriquecedores antes de tocar el grafo.
        var (result, reporter, _) = await RunAsync(setup, Config(("JavaAnalyzer:JdkPath", ""), ("JavaAnalyzer:DefaultEncoding", "no-existe")));
        Dump(reporter);

        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning
            && e.Message == "⚠️ Analizador Java: falló: JavaAnalyzer:DefaultEncoding: codificación desconocida 'no-existe'. Se continúa con el grafo de cbm.");
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Message.StartsWith("⚠️ Struts/JSP: falló:", StringComparison.Ordinal));
        var runs = await RunsAsync(setup.Graph);
        Assert.Equal([EnrichmentRuns.StatusFailed, EnrichmentRuns.StatusFailed], runs.Select(r => r.Status));
        Assert.All(runs, r => Assert.Contains("no-existe", r.Message));
        // El fallo de Java no deja el código de test en el grafo, y la fila lo cuenta (D15).
        Assert.Equal(0, await setup.Graph.CountAsync(TestNodesSql));
        Assert.Equal(5, runs[0].Outcome!.NodesRemoved);
        Assert.Null(runs[1].Outcome);
        Assert.Equal(await setup.Graph.CountAsync("SELECT COUNT(1) FROM nodes"), result.TotalNodes);
    }

    [Theory]
    [InlineData(null, 3600)]
    [InlineData("7200", 7200)]
    [InlineData("0", -1)]
    public void The_step_timeout_comes_from_the_configuration(string? stepTimeoutSeconds, int expectedSeconds)
    {
        (string, string?)[] pairs = stepTimeoutSeconds == null ? [] : [("JavaAnalyzer:StepTimeoutSeconds", stepTimeoutSeconds)];
        var services = new ServiceCollection().AddSingleton(Config(pairs)).BuildServiceProvider();

        var step = AnalysisPipelineFactory.CreateLanguageEnricherStep(services);

        // 0 es sin límite: InfiniteTimeSpan y no null, que dejaría el timeout por defecto del pipeline.
        Assert.Equal(expectedSeconds < 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(expectedSeconds), step.Timeout);
        Assert.Equal(TimeSpan.FromHours(1), AnalysisPipelineFactory.CreateLanguageEnricherStep(null).Timeout);
    }

    [Theory]
    [InlineData("1000", "900", true)]
    [InlineData("1200", "900", false)]
    [InlineData("3600", "0", true)]
    [InlineData("0", "0", false)]
    public async Task A_step_timeout_below_the_process_one_is_a_warning_and_the_step_goes_on(string step, string process, bool warns)
    {
        using var setup = await CreateAsync();
        var configuration = Config(("JavaAnalyzer:JdkPath", ""), ("JavaAnalyzer:StepTimeoutSeconds", step),
            ("JavaAnalyzer:TimeoutSeconds", process), ("StrutsJsp:Enabled", "false"));
        var reporter = new CapturingReporter();
        var context = EnrichmentStepContext.Create(configuration, reporter, setup.Repo, setup.Graph.DatabasePath);
        var input = new GraphStatusResult { DatabasePath = setup.Graph.DatabasePath };

        await new LanguageEnricherStep(JavaAnalyzerOptions.FromConfiguration(configuration)).ExecuteAsync(input, context, 1, null, CancellationToken.None);
        Dump(reporter);

        var warnings = reporter.Entries.Where(e => e.Level == AnalysisLogLevel.Warning
            && e.Message.StartsWith("⚠️ Analizador Java: el timeout del paso es menor que el del proceso Java", StringComparison.Ordinal)).ToList();
        Assert.Equal(warns ? 1 : 0, warnings.Count);
        if (warns && process == "900")
            Assert.Equal("⚠️ Analizador Java: el timeout del paso es menor que el del proceso Java: 1.000 s (JavaAnalyzer:StepTimeoutSeconds) "
                + "frente a 900 s (JavaAnalyzer:TimeoutSeconds) más 300 s para la escritura en el grafo. El paso puede cortarse antes que el proceso.",
                warnings[0].Message);
        // El aviso no para el paso: sigue hasta el JDK que falta.
        Assert.Contains(reporter.Entries, e => e.Message.StartsWith("⚠️ Analizador Java: no hay JDK configurado", StringComparison.Ordinal));
    }

    [JdkFact]
    public async Task With_a_jdk_the_step_enriches_the_graph_end_to_end_and_reports_the_missing_packages()
    {
        using var setup = await CreateAsync();
        var graph = setup.Graph;

        // ProvidedClasspath vacío: Servicio.web usa javax.servlet, que no está en el repositorio.
        var (result, reporter, context) = await RunAsync(setup, JdkConfig());
        Dump(reporter);

        var messages = reporter.Entries.Where(e => e.Source == LanguageEnricherStep.StepName).ToList();
        Assert.DoesNotContain(messages, e => e.Level == AnalysisLogLevel.Error);
        Assert.Equal(
            "🔎 Lenguajes detectados en disco: Java (7 ficheros .java); 1 struts-config y 4 JSP; raíces de test excluidas: test.",
            messages[0].Message);
        Assert.Contains(messages, e => e.Message == $"☕ Analizador Java: JDK {TestJdk.Info!.Version} en '{TestJdk.Info.JdkPath}'.");
        Assert.Contains(messages, e => e.Message.StartsWith("⏳ Analizador Java: ", StringComparison.Ordinal));
        Assert.Contains(messages, e => e.Level == AnalysisLogLevel.Success && e.Message.StartsWith("✅ Analizador Java: 7 ficheros en ", StringComparison.Ordinal));
        var missing = Assert.Single(messages, e => e.Level == AnalysisLogLevel.Warning && e.Message.Contains(" en el classpath: ", StringComparison.Ordinal));
        Assert.Contains("javax.servlet", missing.Message);
        Assert.EndsWith("Añade sus jars en JavaAnalyzer:ProvidedClasspath.", missing.Message);
        Assert.Contains(messages, e => e.Level == AnalysisLogLevel.Success && e.Message.StartsWith("✅ Struts/JSP: 1 acción, 4 JSP (1 sospechosa), ", StringComparison.Ordinal));
        Assert.Equal(DetectedLanguages.Java, context.Get<DetectedLanguages>(AnalysisContextKeys.DetectedLanguages));

        // CALLS resueltas: la interfaz en lugar de la implementación, las dos sobrecargas y la clase anidada.
        Assert.Equal(new[]
        {
            JavaGraphFixture.Qn(JavaTestRepo.BasePath, "Base.log"),
            JavaGraphFixture.DaoConsultar,
            JavaGraphFixture.DaoConsultar + "(Hashtable,long)",
            JavaGraphFixture.Qn(JavaTestRepo.DaoImplPath, "DaoImpl.Cache.put")
        }.OrderBy(q => q, StringComparer.Ordinal), await CallTargetsAsync(graph, JavaGraphFixture.Ejecutar));
        Assert.Equal(0, await graph.CountAsync($"SELECT COUNT(1) FROM edges WHERE type = 'CALLS' AND NOT {EnrichmentMarks.EnrichedByPredicate("'JavaAnalyzer'")}"));

        // Schema 2: el campo casa con el Field de cbm, su lectura da READS y los usos de tipo USES_TYPE (Q5-Q7); no queda
        // ninguna USAGE de cbm en los ficheros analizados.
        var id = await PropertiesAsync(graph, JavaGraphFixture.BeanId);
        Assert.Equal(("id", "long", "private"), (id["field_name"]!.GetValue<string>(), id["type_fqn"]!.GetValue<string>(), id["accessibility"]!.GetValue<string>()));
        Assert.Equal($"[{JavaTestRepo.LineOf(JavaTestRepo.BeanPath, "return id;")}]", JsonNode.Parse((await graph.TextAsync("""
            SELECT e.properties FROM edges e JOIN nodes s ON s.id = e.source_id JOIN nodes t ON t.id = e.target_id
            WHERE e.type = 'READS' AND s.qualified_name = @s AND t.qualified_name = @t
            """, ("@s", JavaGraphFixture.GetId), ("@t", JavaGraphFixture.BeanId)))!)!["lines"]!.ToJsonString());
        async Task<string> UsesTypeContextsAsync(string target) => JsonNode.Parse((await graph.TextAsync("""
            SELECT e.properties FROM edges e JOIN nodes s ON s.id = e.source_id JOIN nodes t ON t.id = e.target_id
            WHERE e.type = 'USES_TYPE' AND s.qualified_name = @s AND t.qualified_name = @t
            """, ("@s", JavaGraphFixture.Ejecutar), ("@t", target)))!)!["contexts"]!.ToJsonString();
        Assert.Contains("\"local\"", await UsesTypeContextsAsync(JavaGraphFixture.Qn(JavaTestRepo.DaoPath, "Dao")));
        Assert.Contains("\"new_implicit\"", await UsesTypeContextsAsync(JavaGraphFixture.Qn(JavaTestRepo.DaoImplPath, "DaoImpl")));
        Assert.Equal(0, await graph.CountAsync("SELECT COUNT(1) FROM edges WHERE type = 'USAGE'"));
        Assert.Contains(messages, e => e.Level == AnalysisLogLevel.Success && e.Message.Contains(" USES_TYPE, ", StringComparison.Ordinal)
            && e.Message.Contains("de las referencias a tipos y campos resueltas", StringComparison.Ordinal)
            && e.Message.Contains("4 de 4 campos casados con cbm (100,00 %)", StringComparison.Ordinal));

        // Sobrecarga creada con su overload_of.
        var overload = await PropertiesAsync(graph, JavaGraphFixture.DaoImplConsultar + "(Hashtable,long)");
        Assert.Equal(JavaGraphFixture.DaoImplConsultar, overload["overload_of"]!.GetValue<string>());

        // El código de test ya no está en la copia.
        Assert.Equal(0, await graph.CountAsync("SELECT COUNT(1) FROM nodes WHERE file_path = 'test' OR file_path LIKE 'test/%'"));

        // Javadoc del fuente ISO-8859-1 bien decodificado.
        Assert.Equal("Añade la acción.", (await PropertiesAsync(graph, JavaGraphFixture.Ejecutar))["docstring"]!.GetValue<string>());

        // Struts: la acción con su HANDLES desde la clase y la JSP con DAO y SQL, sospechosa.
        Assert.Equal(1, await graph.CountAsync("""
            SELECT COUNT(1) FROM edges e JOIN nodes s ON s.id = e.source_id JOIN nodes t ON t.id = e.target_id
            WHERE e.type = 'HANDLES' AND s.qualified_name = @s AND t.qualified_name = @t
            """, ("@s", JavaGraphFixture.Qn(JavaTestRepo.ConsultarActionPath, "ConsultarAction")), ("@t", RouteQn)));
        var consulta = await PropertiesAsync(graph,
            $"{JavaGraphFixture.Project}.{JavaTestRepo.ConsultaJspPath[..JavaTestRepo.ConsultaJspPath.LastIndexOf('.')].Replace('/', '.')}.__jsp__");
        Assert.True(consulta["suspicious"]!.GetValue<bool>());

        var runs = await RunsAsync(graph);
        Assert.Equal([(JavaGraphWriter.EnricherName, EnrichmentRuns.StatusOk), (StrutsJspWriter.EnricherName, EnrichmentRuns.StatusOk)],
            runs.Select(r => (r.Enricher, r.Status)));
        Assert.Equal(TestJdk.Info!.Version, runs[0].ToolVersion);

        // P4: petición y log junto a la BD; el resultado, ya aplicado, se borra (H-11, sin JavaAnalyzer:KeepIntermediate).
        var work = JavaAnalyzerRunner.WorkingDirectoryFor(graph.DatabasePath);
        Assert.Equal(Path.Combine(setup.Temp.Path, "db", "enrichment", "java"), work);
        Assert.True(File.Exists(Path.Combine(work, JavaAnalyzerRunner.RequestFileName)));
        Assert.False(File.Exists(Path.Combine(work, JavaAnalyzerRunner.ResultFileName)));
        Assert.True(File.Exists(Path.Combine(work, JavaAnalyzerRunner.LogFileName)));

        Assert.Equal(await graph.CountAsync("SELECT COUNT(1) FROM nodes"), result.TotalNodes);
        Assert.Equal(await graph.CountAsync("SELECT COUNT(1) FROM edges"), result.TotalEdges);
        Assert.True(result.IsEnriched);
    }

    [JdkFact]
    public async Task Keep_intermediate_leaves_the_analyzer_result_next_to_the_database()
    {
        using var setup = await CreateAsync();

        var (_, reporter, _) = await RunAsync(setup, JdkConfig(("JavaAnalyzer:KeepIntermediate", "true")));

        var result = Path.Combine(JavaAnalyzerRunner.WorkingDirectoryFor(setup.Graph.DatabasePath), JavaAnalyzerRunner.ResultFileName);
        Assert.True(File.Exists(result));
        Assert.Contains(reporter.Entries, e => e.Message == $"ℹ️ Analizador Java: se conserva '{result}' (JavaAnalyzer:KeepIntermediate).");
    }

    [JdkFact]
    public async Task A_second_run_skips_by_p3_and_a_new_java_pass_makes_struts_run_again()
    {
        using var setup = await CreateAsync();
        var graph = setup.Graph;
        await RunAsync(setup, JdkConfig());
        var afterFirst = await SnapshotAsync(graph);

        // Misma indexación y misma configuración: los dos se omiten y el grafo no cambia.
        var (_, second, _) = await RunAsync(setup, JdkConfig());
        Dump(second);
        Assert.Contains(second.Entries, e => e.Level == AnalysisLogLevel.Info && e.Message ==
            "ℹ️ Analizador Java: el grafo ya está enriquecido para esta indexación y esta configuración; se omite. "
            + "Se repite si cbm vuelve a indexar el repositorio (p. ej. con «Forzar reindexación») o si cambia la configuración.");
        Assert.Contains(second.Entries, e => e.Level == AnalysisLogLevel.Info && e.Message ==
            "ℹ️ Struts/JSP: el grafo ya tiene las acciones y las JSP de esta indexación y esta configuración; se omite.");
        Assert.DoesNotContain(second.Entries, e => e.Message.StartsWith("☕", StringComparison.Ordinal));
        Assert.Equal(afterFirst, await SnapshotAsync(graph));
        Assert.Equal(2, (await RunsAsync(graph)).Count);

        // Otra configuración de Java: el analizador se repite y, con él, el plugin Struts, aunque su configuración no cambie.
        var (_, third, _) = await RunAsync(setup, JdkConfig(("JavaAnalyzer:TestRoots:0", "qa")));
        Assert.Contains(third.Entries, e => e.Message.StartsWith("✅ Analizador Java:", StringComparison.Ordinal));
        Assert.Contains(third.Entries, e => e.Message.StartsWith("✅ Struts/JSP:", StringComparison.Ordinal));
        Assert.Equal(
            [JavaGraphWriter.EnricherName, StrutsJspWriter.EnricherName, JavaGraphWriter.EnricherName, StrutsJspWriter.EnricherName],
            (await RunsAsync(graph)).Select(r => r.Enricher));
        Assert.Equal(1, await graph.CountAsync("SELECT COUNT(1) FROM edges WHERE type = 'HANDLES'"));
    }
}

public class JavaAnalyzerRunnerTests
{
    [Fact]
    public void An_empty_or_missing_jdk_path_is_a_typed_jdk_error()
    {
        var empty = Assert.Throws<JavaAnalyzerException>(() => JavaAnalyzerRunner.ResolveJdk(""));
        Assert.Equal(JavaAnalyzerFailure.JdkNotConfigured, empty.Failure);
        Assert.True(empty.IsJdkProblem);

        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "no-existe");
        var notFound = Assert.Throws<JavaAnalyzerException>(() => JavaAnalyzerRunner.ResolveJdk(missing));
        Assert.Equal(JavaAnalyzerFailure.JdkNotFound, notFound.Failure);
        Assert.Contains(missing, notFound.Message);
        Assert.True(notFound.IsJdkProblem);
    }

    [Fact]
    public void The_release_file_decides_the_version()
    {
        using var temp = new TempDirectory();
        var jdk = Path.Combine(temp.Path, "jdk");
        Directory.CreateDirectory(Path.Combine(jdk, "bin"));

        var unknown = Assert.Throws<JavaAnalyzerException>(() => JavaAnalyzerRunner.ResolveJdk(jdk));
        Assert.Equal(JavaAnalyzerFailure.JdkVersionUnknown, unknown.Failure);

        File.WriteAllText(Path.Combine(jdk, "release"), "JAVA_VERSION=\"1.8.0_391\"\n");
        var old = Assert.Throws<JavaAnalyzerException>(() => JavaAnalyzerRunner.ResolveJdk(jdk));
        Assert.Equal(JavaAnalyzerFailure.JdkTooOld, old.Failure);
        Assert.Contains("1.8.0_391", old.Message);

        File.WriteAllText(Path.Combine(jdk, "release"), "JAVA_VERSION=\"11.0.2\"\n");
        Assert.Equal(JavaAnalyzerFailure.JdkTooOld, Assert.Throws<JavaAnalyzerException>(() => JavaAnalyzerRunner.ResolveJdk(jdk)).Failure);

        // Versión válida pero sin bin/java: no es un JDK.
        File.WriteAllText(Path.Combine(jdk, "release"), "JAVA_VERSION=\"17.0.2\"\n");
        Assert.Equal(JavaAnalyzerFailure.JdkNotFound, Assert.Throws<JavaAnalyzerException>(() => JavaAnalyzerRunner.ResolveJdk(jdk)).Failure);

        File.WriteAllBytes(Path.Combine(jdk, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"), []);
        var info = JavaAnalyzerRunner.ResolveJdk(jdk);
        Assert.Equal(("17.0.2", 17, Path.GetFullPath(jdk)), (info.Version, info.FeatureVersion, info.JdkPath));
    }

    [Theory]
    [InlineData("17.0.20.1", 17)]
    [InlineData("21", 21)]
    [InlineData("1.8.0_391", 8)]
    [InlineData("25-ea", 25)]
    [InlineData("x", null)]
    public void Feature_version_follows_both_numbering_schemes(string version, int? expected) =>
        Assert.Equal(expected, JavaAnalyzerRunner.FeatureVersion(version));

    [Fact]
    public void Classpath_takes_the_repository_jars_and_expands_provided_folders()
    {
        using var temp = new TempDirectory();
        var repo = Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "WebContent", "WEB-INF", "lib"));
        File.WriteAllBytes(Path.Combine(repo, "WebContent", "WEB-INF", "lib", "struts.jar"), []);
        // Los jars provided viven fuera del repositorio; «../provided» es relativa al repositorio.
        var provided = Path.Combine(temp.Path, "provided");
        Directory.CreateDirectory(Path.Combine(provided, "sub"));
        File.WriteAllBytes(Path.Combine(provided, "servlet-api.jar"), []);
        File.WriteAllBytes(Path.Combine(provided, "jsp-api.jar"), []);
        File.WriteAllBytes(Path.Combine(provided, "leeme.txt"), []);
        File.WriteAllBytes(Path.Combine(provided, "sub", "otro.jar"), []);
        var outside = Path.Combine(temp.Path, "extra.jar");
        File.WriteAllBytes(outside, []);
        var scan = RepositoryScan.Scan(repo, []);

        var classpath = JavaAnalyzerRunner.ComposeClasspath(scan, ["../provided", outside, "falta.jar", outside]);

        Assert.Equal(
        [
            Path.Combine(repo, "WebContent", "WEB-INF", "lib", "struts.jar"),
            Path.Combine(provided, "jsp-api.jar"),
            Path.Combine(provided, "servlet-api.jar"),
            outside
        ], classpath.Entries);
        Assert.Equal(["falta.jar"], classpath.Missing);
    }

    [Fact]
    public void The_request_carries_the_contract_keys_and_the_batch_options()
    {
        var scan = new RepositoryScanResult { Root = @"C:\repo", JavaFiles = ["src/a/B.java", "src/a/C.java"] };
        var text = JavaAnalyzerRunner.BuildRequest(new JavaAnalyzerRunRequest
        {
            Scan = scan,
            Options = new JavaAnalyzerOptions { BatchSize = 2000, MaxParallelBatches = 2 },
            Jdk = new JdkInfo(@"C:\jdk", @"C:\jdk\bin\java.exe", "17.0.2", 17),
            Classpath = new JavaClasspath([@"C:\libs\a.jar"], []),
            DefaultEncoding = "iso-8859-1",
            WorkingDirectory = @"C:\db\enrichment\java"
        });

        Assert.Equal(
            "root=C:\\repo\nout=C:\\db\\enrichment\\java\ndefaultEncoding=iso-8859-1\nbatchSize=2000\nmaxParallelBatches=2\n"
            + "classpath=C:\\libs\\a.jar\nfile=src/a/B.java\nfile=src/a/C.java\n",
            text);
    }

    [Fact]
    public void The_analyzer_source_travels_to_the_output_and_the_working_folder_sits_by_the_database()
    {
        Assert.True(File.Exists(JavaAnalyzerRunner.DefaultAnalyzerSourcePath), JavaAnalyzerRunner.DefaultAnalyzerSourcePath);
        Assert.Equal(Path.Combine(@"C:\repo\.codebase", "enrichment", "java"), JavaAnalyzerRunner.WorkingDirectoryFor(@"C:\repo\.codebase\codebase.db"));
    }
}

/// <summary>P2: con Java y sin C#, <see cref="BdpEnricherStep"/> no llama al CLI ni al fallback del LLM.</summary>
public class BdpEnricherLanguageTests
{
    private sealed class CountingStep : IStep<GraphStatusResult, GraphStatusResult>
    {
        public int Calls { get; private set; }
        public string Name => "Fallback";
        public IResume? Resume => null;
        public Type InputType => typeof(GraphStatusResult);
        public Type OutputType => typeof(GraphStatusResult);

        public Task<GraphStatusResult> ExecuteAsync(GraphStatusResult input, PipelineContext context, int attempt,
            GraphStatusResult? lastStepResult, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(input);
        }
    }

    private static async Task<(GraphStatusResult Input, GraphStatusResult Result, CapturingReporter Reporter, CountingStep Fallback)> RunAsync(
        DetectedLanguages? languages)
    {
        using var temp = new TempDirectory();
        var reporter = new CapturingReporter();
        // Con BdpEnricher desactivado el camino de siempre va directo al fallback: así se ve si se toma o no.
        var context = EnrichmentStepContext.Create(TestConfig.FromPairs(("BdpEnricher:Enabled", "false")), reporter, temp.Path,
            Path.Combine(temp.Path, "codebase.db"));
        if (languages is { } detected)
            context.Set(AnalysisContextKeys.DetectedLanguages, detected);
        var fallback = new CountingStep();
        var input = new GraphStatusResult { DatabasePath = Path.Combine(temp.Path, "codebase.db") };
        var result = await new BdpEnricherStep(fallback).ExecuteAsync(input, context, 1, null, CancellationToken.None);
        return (input, result, reporter, fallback);
    }

    [Fact]
    public async Task Java_without_csharp_skips_the_cli_and_the_llm_fallback()
    {
        var (input, result, reporter, fallback) = await RunAsync(DetectedLanguages.Java);

        Assert.Same(input, result);
        Assert.Equal(0, fallback.Calls);
        var entry = Assert.Single(reporter.Entries);
        Assert.Equal(BdpEnricherStep.StepName, entry.Source);
        Assert.Equal("ℹ️ Repositorio Java sin C#: el enriquecimiento es el del analizador Java; se omiten BdpGraphEnricher y el fallback del LLM.",
            entry.Message);
    }

    [Theory]
    [InlineData(DetectedLanguages.Java | DetectedLanguages.CSharp)]
    [InlineData(DetectedLanguages.CSharp)]
    [InlineData(DetectedLanguages.None)]
    [InlineData(null)]
    public async Task With_csharp_or_without_either_language_the_path_does_not_change(DetectedLanguages? languages)
    {
        var (_, _, _, fallback) = await RunAsync(languages);

        Assert.Equal(1, fallback.Calls);
    }
}
