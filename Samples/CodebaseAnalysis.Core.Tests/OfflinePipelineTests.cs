using AITaskAgent.Configuration;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// El pipeline completo sin red ni procesos externos. El grafo ya tiene un tipo (cuenta como
/// estructural, así que no se indexa) con <c>cyclomatic</c> (cuenta como enriquecido, así que no se
/// enriquece), el repositorio no tiene Office ni Markdown, y el LLM es un servicio falso.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class OfflinePipelineTests
{
    private const string CannedReport = """
        # Informe

        ## Resumen

        Contenido generado por el servicio falso de los tests, sin datos del repositorio.

        <!-- analysis-meta: health=Green -->
        """;

    private static async Task CreateEnrichedGraphAsync(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        await GraphSchema.EnsureAsync(dbPath, CancellationToken.None);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO nodes (project, label, name, qualified_name, file_path, start_line, end_line, properties)
            VALUES ('Codebase', 'Class', 'Sample', 'Demo.Sample', 'src/Sample.cs', 1, 10, '{"cyclomatic":3}');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Runs_all_auditors_and_the_synthesis_and_reports_progress()
    {
        using var repo = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(repo.Path, "src"));
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "src", "Sample.cs"), "namespace Demo; public class Sample { }");

        IConfiguration configuration = TestConfig.FromPairs(
            ("LlmConfiguration:DefaultProvider", "Fake"),
            ("LlmConfiguration:Providers:Fake:ServiceType", "fake"),
            ("LlmConfiguration:Providers:Fake:BaseUrl", "http://fake.invalid/"),
            ("LlmConfiguration:Providers:Fake:Model", "fake-model"));

        var llm = new CannedLlmService(CannedReport);
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddKeyedSingleton<ILlmService>("fake", llm);
        services.AddAITaskAgent();
        await using var serviceProvider = services.BuildServiceProvider();
        serviceProvider.UseAITaskAgent();

        var roles = LlmRoleFactory.BuildRoles(configuration, serviceProvider, _ => null, _ => { });
        var input = AnalysisRunOptions.BuildInput(configuration, repo.Path, _ => { });
        await CreateEnrichedGraphAsync(input.DatabasePath);
        AnalysisToolRegistration.Register(input);

        var channel = new EventChannel();
        var received = new List<IProgressEvent>();
        channel.Subscribe((evt, _) =>
        {
            lock (received)
            {
                received.Add(evt);
            }
            return Task.CompletedTask;
        });
        var reporter = new CapturingReporter();

        // El pipeline cierra el canal al terminar, porque no es el singleton del contenedor: cuando
        // RunAsync devuelve el control, todos los eventos han pasado ya por el suscriptor.
        var result = await AnalysisRunner.RunAsync(input, roles, serviceProvider, channel, reporter, CancellationToken.None);

        var final = Assert.IsType<AnalysisPipelineFinalResult>(result);
        Assert.False(final.HasError, final.Error?.Message);
        Assert.True(final.Success);
        Assert.Empty(final.FailedReports);
        Assert.Equal(9, final.IndividualReports.Count);
        Assert.All(final.IndividualReports, r => Assert.Equal("Green", r.QualityHealth));

        var reportFiles = Directory.GetFiles(input.OutputReportsPath, "*.md").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(
            ReportCatalog.All.Select(d => d.FileName).Append(ReportCatalog.ExecutiveReportFileName).Order(),
            reportFiles);

        List<IProgressEvent> events;
        lock (received)
        {
            events = [.. received];
        }

        Assert.Equal(9, events.OfType<BranchStartedEvent>().Count());

        var written = events.OfType<StepProgressEvent>().Where(e => e.EventType == AnalysisEventTypes.ReportWritten).ToList();
        Assert.Equal(10, written.Count);
        Assert.Equal(
            reportFiles,
            written.Select(e => (string)e.Metadata![AnalysisEventTypes.MetadataKeys.FileName]!).Order());
        Assert.All(written, e => Assert.Equal("Green", e.Metadata![AnalysisEventTypes.MetadataKeys.Health]));

        // Los mensajes van al reporter del contexto y, con el mismo texto, al canal.
        var logEvents = events.OfType<StepProgressEvent>().Where(e => e.EventType == AnalysisEventTypes.Log).ToList();
        Assert.Equal(reporter.Entries.Count, logEvents.Count);
        Assert.Contains(reporter.Entries, e =>
            e.Source == AnalysisRunner.SourceName && e.Message == "🚀 Ejecutando AnalysisPipeline completo con AITaskFramework...");
        Assert.Contains(reporter.Entries, e =>
            e.Source == Steps.DetectAndIndexGraphStep.StepName && e.Message.StartsWith("ℹ️ Grafo estructural detectado", StringComparison.Ordinal));
        Assert.Contains(reporter.Entries, e =>
            e.Source == Steps.BdpEnricherStep.StepName && e.Message == "ℹ️ El grafo ya contiene enriquecimiento estructural y semántico previo.");
        Assert.DoesNotContain(reporter.Entries, e => e.Level == AnalysisLogLevel.Error);
        // Los límites efectivos, una línea por grupo, justo después del arranque.
        var start = reporter.Entries.ToList().FindIndex(e => e.Message.StartsWith("🚀 Ejecutando AnalysisPipeline", StringComparison.Ordinal));
        string[] prefixes =
            ["⏱️ Límites de CodebaseAnalysis:Timeouts:", "⏱️ Límites de JavaAnalyzer:", "⏱️ Plazos LLM de los roles del análisis", "⏱️ Límites de AITaskAgent:Timeouts"];
        var limits = reporter.Entries.Skip(start + 1).Take(prefixes.Length).ToList();
        Assert.All(prefixes.Zip(limits), p => Assert.StartsWith(p.First, p.Second.Message, StringComparison.Ordinal));
        Assert.All(limits, e => Assert.Equal(AnalysisRunner.SourceName, e.Source));

        // 9 auditores y la síntesis; el enriquecedor no se invoca porque el grafo ya está enriquecido.
        Assert.Equal(10, llm.Calls);

        // Procedencia (recomendación 12): el pipeline completa la línea del modelo en cada informe y en el resumen.
        foreach (var file in reportFiles)
        {
            var meta = ReportMarkdown.GetLastMetaLine(await File.ReadAllTextAsync(Path.Combine(input.OutputReportsPath, file!)))!;
            Assert.Matches(
                @"^<!-- analysis-meta: health=Green; generated_at=\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ; model=fake-model; provider=canned; " +
                @"system_sha=[0-9a-f]{12}; prompt_sha=[0-9a-f]{12}; app=[^;]+ -->$", meta);
        }
        var smellsMeta = ReportMarkdown.GetLastMetaLine(await File.ReadAllTextAsync(Path.Combine(input.OutputReportsPath, "02_code_smells.md")))!;
        var deadMeta = ReportMarkdown.GetLastMetaLine(await File.ReadAllTextAsync(Path.Combine(input.OutputReportsPath, "03_codigo_muerto.md")))!;
        string Sha(string line, string key) => line.Split("; ").Single(p => p.StartsWith(key + "=", StringComparison.Ordinal));
        Assert.NotEqual(Sha(smellsMeta, "system_sha"), Sha(deadMeta, "system_sha"));

        // El consumo de las diez llamadas, del canal (LlmUsageTracker), por auditor y en total.
        var tracker = new LlmUsageTracker();
        events.ForEach(tracker.Observe);
        var usage = tracker.Snapshot();
        Assert.Equal((10, 50L, 50L), (usage.Total.Calls, usage.Total.PromptTokens, usage.Total.CompletionTokens));
        Assert.Equal(9, usage.BySource.Count(s => s.Report != null));
        Assert.Null(usage.Total.CostUsd);
    }
}
