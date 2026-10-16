using AITaskAgent.Configuration;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.Observability;
using CodebaseAnalysis.Core.Documents;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Knowledge.Graph;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysis.Core.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// V2-D (plan v2 §8.1.4): tras reemplazar la BD por una nueva (simulación de reindexación) y ejecutar el pipeline sin
/// red, las fuentes marcadas vuelven al grafo: documentos e informes propios en la detección y los del catálogo tras la
/// síntesis, ya con el contenido nuevo. Los documentos subidos no se ingestan como Markdown del repositorio.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class ReincorporationPipelineTests
{
    private const string CannedReport = "# Informe\n\n## Resumen\n\nContenido del servicio falso.\n\n<!-- analysis-meta: health=Green -->\n";

    private static async Task CreateEnrichedGraphAsync(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        await GraphSchema.EnsureAsync(dbPath, CancellationToken.None);
        await using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO nodes (project, label, name, qualified_name, file_path, start_line, end_line, properties)
            VALUES ('Codebase', 'Class', 'Sample', 'Demo.Sample', 'src/Sample.cs', 1, 10, '{"cyclomatic":3}');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> UserDocQualifiedNamesAsync(string db)
    {
        await using var conn = new SqliteConnection($"Data Source={db};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT qualified_name FROM nodes WHERE label = 'UserDocument' ORDER BY 1";
        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(reader.GetString(0));
        return result;
    }

    [Fact]
    public async Task Marked_sources_come_back_after_the_database_is_replaced()
    {
        using var repo = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(repo.Path, "src"));
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "src", "Sample.cs"), "namespace Demo; public class Sample { }");

        IConfiguration configuration = TestConfig.FromPairs(FakeLlmConfig.Pairs("canned"));
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddSingleton<ILlmService>(new CannedLlmService(CannedReport));
        services.AddAITaskAgent();
        await using var serviceProvider = services.BuildServiceProvider();
        serviceProvider.UseAITaskAgent();

        var roles = LlmRoleFactory.BuildRoles(serviceProvider, _ => null, _ => { });
        var input = AnalysisRunOptions.BuildInput(configuration, repo.Path, _ => { });
        await CreateEnrichedGraphAsync(input.DatabasePath);
        var paths = new KnowledgePaths(input.RepositoryPath, input.OutputReportsPath);

        var doc = await new DocumentStore(paths).UploadAsync(new MemoryStream(Encoding.UTF8.GetBytes("# Manual\n\nLa clase `Sample`.\n")), "manual.md", CancellationToken.None);
        var custom = new ReportWriter(paths).Create("Estudio", "# Estudio\n\nSobre `Sample`.", null, ReportVersionOrigin.Ui);
        Directory.CreateDirectory(paths.ReportsPath);
        File.WriteAllText(Path.Combine(paths.ReportsPath, "05_errores_y_bugs.md"), "# Viejo\n\n<!-- analysis-meta: health=Red -->\n");
        foreach (var source in new[] { SourceRef.Document(doc.Id), custom.Source, SourceRef.Report("05_errores_y_bugs.md") })
            await KnowledgeGraphIngestor.AddAsync(input.DatabasePath, paths, source, CancellationToken.None);

        // Reindexación simulada: una BD nueva sin nada de lo añadido.
        SqliteConnection.ClearAllPools();
        File.Delete(input.DatabasePath);
        await CreateEnrichedGraphAsync(input.DatabasePath);
        Assert.Empty(await UserDocQualifiedNamesAsync(input.DatabasePath));

        AnalysisToolRegistration.Register(input);
        var reporter = new CapturingReporter();
        var result = await AnalysisRunner.RunAsync(input, roles, serviceProvider, new EventChannel(), reporter, CancellationToken.None);
        Assert.False(result.HasError, result.Error?.Message);

        Assert.Equal(
            [$"userdoc.custom:{custom.FileName}", $"userdoc.doc:{doc.Id}", "userdoc.report:05_errores_y_bugs.md"],
            await UserDocQualifiedNamesAsync(input.DatabasePath));
        Assert.Contains(reporter.Entries, e => e.Message == "🧩 Reincorporados al grafo 2 documentos aportados e informes propios.");
        Assert.Contains(reporter.Entries, e => e.Message == "🧩 Reincorporados al grafo 1 informes del análisis.");

        // El informe del catálogo volvió con el contenido regenerado.
        var state = await KnowledgeGraphIngestor.GetStateAsync(input.DatabasePath, paths, SourceRef.Report("05_errores_y_bugs.md"), CancellationToken.None);
        Assert.Equal(GraphKnowledgeStates.Present, state.State);

        // Los documentos subidos no se ingestan como Markdown ni como ficheros del repositorio.
        await using var conn = new SqliteConnection($"Data Source={input.DatabasePath};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM nodes WHERE replace(file_path, '\\', '/') LIKE '%.codebase/%' OR replace(file_path, '\\', '/') LIKE 'reports/%'";
        Assert.Equal(0L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
    }
}
