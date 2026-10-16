using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using Microsoft.Data.Sqlite;

namespace CodebaseAnalysis.Core.Tests.Enrichment;

/// <summary>
/// <see cref="DotNetEnricherStep"/>: dónde encuentra el CLI, cuándo se salta la pasada y que un enriquecedor que no está
/// o falla deja un aviso y pasa al fallback del LLM en lugar de parar el análisis. La pasada real la prueban
/// DotNetEnricher.Tests y la verificación con csharp-simple.
/// </summary>
public class DotNetEnricherStepTests
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

    private static async Task<string> CreateGraphAsync(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE projects (name TEXT PRIMARY KEY, indexed_at TEXT NOT NULL, root_path TEXT NOT NULL);
            CREATE TABLE nodes (id INTEGER PRIMARY KEY AUTOINCREMENT, project TEXT NOT NULL, label TEXT NOT NULL, name TEXT NOT NULL,
                qualified_name TEXT NOT NULL, file_path TEXT DEFAULT '', start_line INTEGER DEFAULT 0, end_line INTEGER DEFAULT 0,
                properties TEXT DEFAULT '{}', UNIQUE(project, qualified_name));
            CREATE TABLE edges (id INTEGER PRIMARY KEY AUTOINCREMENT, project TEXT NOT NULL, source_id INTEGER NOT NULL,
                target_id INTEGER NOT NULL, type TEXT NOT NULL, properties TEXT DEFAULT '{}', UNIQUE(source_id, target_id, type));
            INSERT INTO projects VALUES ('p', '2026-09-30T10:00:00Z', 'C:/repo');
            INSERT INTO nodes(project, label, name, qualified_name, file_path) VALUES ('p', 'Class', 'A', 'p.src.A.A', 'src/A.cs');
            """;
        await cmd.ExecuteNonQueryAsync();
        return path;
    }

    private static (PipelineContext Context, CapturingReporter Reporter) Context(TempDirectory temp, string database, params (string, string?)[] config)
    {
        var reporter = new CapturingReporter();
        var context = EnrichmentStepContext.Create(TestConfig.FromPairs(config), reporter, temp.Path, database);
        context.Set(AnalysisContextKeys.DetectedLanguages, DetectedLanguages.CSharp);
        return (context, reporter);
    }

    [Fact]
    public void The_cli_is_found_in_the_bundle_folder_or_next_to_the_app()
    {
        using var temp = new TempDirectory();
        var name = DotNetEnricherIdentity.ExecutableName;
        var (missing, error) = DotNetEnricherStep.LocateCli(new DotNetEnricherOptions(), temp.Path);
        Assert.Null(missing);
        Assert.Contains(DotNetEnricherIdentity.BundleFolder, error, StringComparison.Ordinal);

        // Con un OutDir común el build lo deja en la carpeta de la app.
        File.WriteAllText(Path.Combine(temp.Path, name + ".dll"), "");
        Assert.Equal(Path.Combine(temp.Path, name + ".dll"), DotNetEnricherStep.LocateCli(new DotNetEnricherOptions(), temp.Path).CliPath);

        // Con salidas separadas va a dotnet-enricher/, que tiene prioridad.
        var bundle = Directory.CreateDirectory(Path.Combine(temp.Path, DotNetEnricherIdentity.BundleFolder)).FullName;
        File.WriteAllText(Path.Combine(bundle, name + ".dll"), "");
        Assert.Equal(Path.Combine(bundle, name + ".dll"), DotNetEnricherStep.LocateCli(new DotNetEnricherOptions(), temp.Path).CliPath);

        var configured = DotNetEnricherStep.LocateCli(new DotNetEnricherOptions { CliPath = Path.Combine(temp.Path, "no.exe") }, temp.Path);
        Assert.Null(configured.CliPath);
        Assert.Contains("DotNetEnricher:CliPath", configured.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_cli_the_step_warns_and_falls_back_to_the_llm()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "app.sln"), "");
        var db = await CreateGraphAsync(Path.Combine(temp.Path, "codebase.db"));
        var (context, reporter) = Context(temp, db, ("DotNetEnricher:CliPath", Path.Combine(temp.Path, "missing.exe")));
        var fallback = new CountingStep();

        await new DotNetEnricherStep(fallback).ExecuteAsync(new GraphStatusResult { DatabasePath = db }, context, 1, null, CancellationToken.None);

        Assert.Equal(1, fallback.Calls);
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Source == DotNetEnricherStep.StepName
                                               && e.Message.Contains("Se usa el enriquecimiento semántico por LLM.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_required_a_missing_cli_stops_the_analysis()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "app.sln"), "");
        var db = await CreateGraphAsync(Path.Combine(temp.Path, "codebase.db"));
        var (context, _) = Context(temp, db,
            ("DotNetEnricher:CliPath", Path.Combine(temp.Path, "missing.exe")), ("DotNetEnricher:Required", "true"));
        var fallback = new CountingStep();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DotNetEnricherStep(fallback).ExecuteAsync(new GraphStatusResult { DatabasePath = db }, context, 1, null, CancellationToken.None));
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task A_successful_run_on_the_same_index_and_configuration_is_not_repeated()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "app.sln"), "");
        var db = await CreateGraphAsync(Path.Combine(temp.Path, "codebase.db"));
        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            await conn.OpenAsync();
            await EnrichmentRuns.InsertAsync(conn, new EnrichmentRun
            {
                Enricher = DotNetEnricherIdentity.EnricherName,
                EnricherVersion = DotNetEnricherIdentity.EnricherVersion,
                IndexFingerprint = (await EnrichmentRuns.IndexFingerprintAsync(conn, CancellationToken.None))!,
                ConfigFingerprint = EnrichmentRuns.ConfigFingerprint(["app.sln", ""]),
                StartedAt = DateTimeOffset.UtcNow,
                Status = EnrichmentRuns.StatusOk
            }, CancellationToken.None);
        }
        // Sin CLI a propósito: si no se saltara la pasada, avisaría de que falta y llamaría al fallback.
        var (context, reporter) = Context(temp, db, ("DotNetEnricher:CliPath", Path.Combine(temp.Path, "missing.exe")));
        var fallback = new CountingStep();
        var input = new GraphStatusResult { DatabasePath = db, IsEnriched = true };

        var result = await new DotNetEnricherStep(fallback).ExecuteAsync(input, context, 1, null, CancellationToken.None);

        Assert.Same(input, result);
        Assert.Equal(0, fallback.Calls);
        var entry = Assert.Single(reporter.Entries);
        Assert.StartsWith("ℹ️ DotNetEnricher: el grafo ya está enriquecido para esta indexación y esta configuración", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_solution_the_step_falls_back_to_the_llm()
    {
        using var temp = new TempDirectory();
        var db = await CreateGraphAsync(Path.Combine(temp.Path, "codebase.db"));
        var (context, reporter) = Context(temp, db);
        var fallback = new CountingStep();

        await new DotNetEnricherStep(fallback).ExecuteAsync(new GraphStatusResult { DatabasePath = db }, context, 1, null, CancellationToken.None);

        Assert.Equal(1, fallback.Calls);
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Message.Contains(".sln ni .slnx", StringComparison.Ordinal));
    }

    // ─── Restauración de paquetes ───────────────────────────────────────────────

    [Theory]
    [InlineData(null, DotNetRestoreMode.Auto)]
    [InlineData("", DotNetRestoreMode.Auto)]
    [InlineData("auto", DotNetRestoreMode.Auto)]
    [InlineData("Always", DotNetRestoreMode.Always)]
    [InlineData("siempre", DotNetRestoreMode.Always)]
    [InlineData("NEVER", DotNetRestoreMode.Never)]
    [InlineData("nunca", DotNetRestoreMode.Never)]
    public void Restore_mode_accepts_english_and_spanish_values_and_defaults_to_auto(string? raw, DotNetRestoreMode expected)
    {
        var options = DotNetEnricherOptions.FromConfiguration(TestConfig.FromPairs(("DotNetEnricher:Restore", raw)));

        Assert.Equal(expected, options.Restore);
        Assert.Null(options.InvalidRestoreValue);
        Assert.Equal(300, options.RestoreTimeoutSeconds);
    }

    [Fact]
    public void An_unknown_restore_value_falls_back_to_auto_and_is_reported()
    {
        var options = DotNetEnricherOptions.FromConfiguration(TestConfig.FromPairs(
            ("DotNetEnricher:Restore", "quizá"), ("DotNetEnricher:RestoreTimeoutSeconds", "45")));

        Assert.Equal(DotNetRestoreMode.Auto, options.Restore);
        Assert.Equal("quizá", options.InvalidRestoreValue);
        Assert.Equal(45, options.RestoreTimeoutSeconds);
    }

    [Fact]
    public void The_cli_receives_the_restore_mode_its_timeout_and_a_cache_next_to_the_database_never_inside_the_repository_sources()
    {
        using var temp = new TempDirectory();
        var repo = Path.Combine(temp.Path, "repo");
        var db = Path.Combine(repo, ".codebase", "codebase.db");
        var options = new DotNetEnricherOptions { Restore = DotNetRestoreMode.Always, RestoreTimeoutSeconds = 120 };

        var args = DotNetEnricherStep.BuildCliArguments(Path.Combine(repo, "app.sln"), db, repo, "fp", options);

        string ValueOf(string flag) => args[args.IndexOf(flag) + 1];
        Assert.Equal("always", ValueOf("--restore"));
        Assert.Equal("120", ValueOf("--restore-timeout"));
        Assert.Equal(Path.Combine(repo, ".codebase", "enrichment", "dotnet-restore"), ValueOf("--cache-dir"));
    }
}
