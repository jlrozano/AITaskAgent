using System.Globalization;
using AITaskAgent.Core.Models;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// ForceReindex (auditoría, H-11): la copia de seguridad ya no mueve la BD ni los informes antes de indexar, así que un
/// indexador que falla deja la ejecución anterior en su sitio, y las copias rotan según <c>CodebaseAnalysis:Backups:Keep</c>.
/// </summary>
public sealed class ForceReindexBackupTests
{
    private const string ReportName = "05_errores_y_bugs.md";

    private sealed class Repo : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public string Path => _temp.Path;
        public string Database => System.IO.Path.Combine(Path, ".codebase", "codebase.db");
        public string Reports => System.IO.Path.Combine(Path, "reports");
        public string Backups => System.IO.Path.Combine(Path, ".codebase", "backups");

        public AnalysisPipelineInput Input(int keep = AnalysisPipelineInput.DefaultBackupsToKeep) => new()
        {
            RepositoryPath = Path,
            DatabasePath = Database,
            OutputReportsPath = Reports,
            ForceReindex = true,
            BackupsToKeep = keep
        };

        public void Dispose() => _temp.Dispose();
    }

    /// <summary>BD con un nodo de código, que <c>HasStructuralGraphAsync</c> reconoce como grafo, y un informe del pipeline.</summary>
    private static async Task<Repo> PreviousRunAsync()
    {
        var repo = new Repo();
        Directory.CreateDirectory(Path.GetDirectoryName(repo.Database)!);
        Directory.CreateDirectory(repo.Reports);
        await File.WriteAllTextAsync(Path.Combine(repo.Reports, ReportName), "informe anterior");

        await using var conn = new SqliteConnection($"Data Source={repo.Database};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE nodes(id INTEGER PRIMARY KEY, project TEXT, label TEXT, name TEXT, qualified_name TEXT, file_path TEXT,
                start_line INTEGER, end_line INTEGER, properties TEXT);
            INSERT INTO nodes(project, label, name, qualified_name, file_path, start_line, end_line, properties)
                VALUES ('p', 'Class', 'Anterior', 'p.src.Anterior', 'src/Anterior.java', 1, 9, '{}');
            """;
        await cmd.ExecuteNonQueryAsync();
        return repo;
    }

    private static async Task<string?> NodeNameAsync(string db)
    {
        await using var conn = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM nodes LIMIT 1";
        return await cmd.ExecuteScalarAsync() as string;
    }

    private static PipelineContext Context(CapturingReporter reporter, params (string, string?)[] configuration)
    {
        var services = new ServiceCollection().AddSingleton(TestConfig.FromPairs(configuration)).BuildServiceProvider();
        var context = new PipelineContext(null, services);
        context.Set(AnalysisContextKeys.Reporter, reporter);
        return context;
    }

    [Fact]
    public async Task A_failing_indexer_restores_the_previous_database_and_leaves_the_reports_in_place()
    {
        if (!OperatingSystem.IsWindows())
            return; // el indexador falso es un .cmd

        using var repo = await PreviousRunAsync();
        // Indexador falso: responde a --version y, al indexar, estropea la BD (como una indexación a medias) y sale con error.
        var fakeCbm = Path.Combine(repo.Path, "..", $"fake-cbm-{Guid.NewGuid():N}.cmd");
        await File.WriteAllTextAsync(fakeCbm, $"""
            @echo off
            if "%~1"=="--version" (echo codebase-memory-mcp 9.9.9& exit /b 0)
            echo roto> "{repo.Database}"
            exit /b 3
            """.Replace("\n", "\r\n", StringComparison.Ordinal));
        try
        {
            var reporter = new CapturingReporter();
            var context = Context(reporter, ("CodebaseMemory:CliPath", fakeCbm));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new DetectAndIndexGraphStep().ExecuteAsync(repo.Input(), context, 1, null, CancellationToken.None));

            Assert.Contains("código de error 3", ex.Message);
            Assert.Equal("Anterior", await NodeNameAsync(repo.Database));
            Assert.Equal("informe anterior", await File.ReadAllTextAsync(Path.Combine(repo.Reports, ReportName)));
            var backup = Assert.Single(Directory.GetDirectories(repo.Backups));
            Assert.True(File.Exists(Path.Combine(backup, ".codebase", "codebase.db")));
            Assert.True(File.Exists(Path.Combine(backup, "reports", ReportName)));
            Assert.Contains(reporter.Entries, e => e.Message.Contains("(versión 9.9.9)", StringComparison.Ordinal));
            Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning
                && e.Message.StartsWith("↩️ ForceReindex: la indexación no terminó; se ha restaurado la ejecución anterior", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(fakeCbm);
        }
    }

    [Fact]
    public async Task The_backup_copies_and_only_a_finished_indexing_takes_the_previous_reports_away()
    {
        using var repo = await PreviousRunAsync();
        var reporter = new CapturingReporter();
        var context = Context(reporter);

        var backup = DetectAndIndexGraphStep.BackupPreviousRun(repo.Input(), context);

        // Mientras se indexa, la BD y el informe siguen en su sitio, y la copia ya existe.
        Assert.Equal("Anterior", await NodeNameAsync(repo.Database));
        Assert.True(File.Exists(Path.Combine(repo.Reports, ReportName)));
        Assert.Equal("Anterior", await NodeNameAsync(Path.Combine(backup.Directory, ".codebase", "codebase.db")));
        Assert.Equal("informe anterior", await File.ReadAllTextAsync(Path.Combine(backup.Directory, "reports", ReportName)));

        backup.DiscardSuperseded(context);

        Assert.False(File.Exists(Path.Combine(repo.Reports, ReportName)));
        Assert.True(File.Exists(Path.Combine(backup.Directory, "reports", ReportName)));
    }

    [Fact]
    public async Task Backups_rotate_keeping_the_most_recent_ones()
    {
        using var repo = await PreviousRunAsync();
        var stamps = Enumerable.Range(1, 6)
            .Select(i => new DateTime(2026, 9, i, 10, 0, 0).ToString(DetectAndIndexGraphStep.BackupFolderFormat, CultureInfo.InvariantCulture))
            .ToList();
        foreach (var stamp in stamps)
            Directory.CreateDirectory(Path.Combine(repo.Backups, stamp));
        Directory.CreateDirectory(Path.Combine(repo.Backups, "manual"));

        // 0 no borra nada.
        Assert.Equal(0, DetectAndIndexGraphStep.PruneBackups(repo.Backups, 0));
        Assert.Equal(7, Directory.GetDirectories(repo.Backups).Length);

        Assert.Equal(3, DetectAndIndexGraphStep.PruneBackups(repo.Backups, 3));
        Assert.Equal([.. stamps.Skip(3), "manual"], Directory.GetDirectories(repo.Backups).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        // La copia nueva cuenta: con Keep 2 quedan ella y la más reciente de las anteriores.
        var reporter = new CapturingReporter();
        var backup = DetectAndIndexGraphStep.BackupPreviousRun(repo.Input(keep: 2), Context(reporter));
        Assert.Equal([stamps[^1], Path.GetFileName(backup.Directory), "manual"],
            Directory.GetDirectories(repo.Backups).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Contains(reporter.Entries, e => e.Message.StartsWith("🧹 ForceReindex: 2 copias antiguas borradas", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, AnalysisPipelineInput.DefaultBackupsToKeep, false)]
    [InlineData("0", 0, false)]
    [InlineData("12", 12, false)]
    [InlineData("-1", AnalysisPipelineInput.DefaultBackupsToKeep, true)]
    [InlineData("muchas", AnalysisPipelineInput.DefaultBackupsToKeep, true)]
    public void Backups_to_keep_comes_from_configuration(string? raw, int expected, bool warns)
    {
        using var repo = new Repo();
        var warnings = new List<string>();

        var input = AnalysisRunOptions.BuildInput(TestConfig.FromPairs(("CodebaseAnalysis:Backups:Keep", raw)), repo.Path, warnings.Add);

        Assert.Equal(expected, input.BackupsToKeep);
        Assert.Equal(warns, warnings.Any(w => w.Contains("CodebaseAnalysis:Backups:Keep", StringComparison.Ordinal)));
    }
}
