using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>Una fila por argumento de la tabla del README §2.</summary>
public sealed class AnalysisRunOptionsTests
{
    private static AnalysisPipelineInput Build(string repo, params string[] args)
    {
        var configuration = TestConfig.FromArgs(args);
        return AnalysisRunOptions.BuildInput(configuration, repo, _ => { });
    }

    [Fact]
    public void Repo_is_relative_to_the_current_directory()
    {
        using var cwd = new TempDirectory();

        var repo = AnalysisRunOptions.ResolveRepositoryPath(TestConfig.FromArgs("--repo", "sub"), cwd.Path);

        Assert.Equal(Path.Combine(cwd.Path, "sub"), repo);
    }

    [Fact]
    public void Repo_falls_back_to_RepositoryPath_and_then_to_the_current_directory()
    {
        using var cwd = new TempDirectory();

        var fromConfig = AnalysisRunOptions.ResolveRepositoryPath(
            TestConfig.FromPairs(("CodebaseAnalysis:RepositoryPath", "configured")), cwd.Path);
        var fromNothing = AnalysisRunOptions.ResolveRepositoryPath(TestConfig.FromArgs(), cwd.Path);
        var cliWins = AnalysisRunOptions.ResolveRepositoryPath(
            new ConfigurationBuilder()
                .AddInMemoryCollection([new("CodebaseAnalysis:RepositoryPath", "configured")])
                .AddCommandLine(["--repo", "cli"])
                .Build(),
            cwd.Path);

        Assert.Equal(Path.Combine(cwd.Path, "configured"), fromConfig);
        Assert.Equal(Path.GetFullPath(cwd.Path), fromNothing);
        Assert.Equal(Path.Combine(cwd.Path, "cli"), cliWins);
    }

    [Fact]
    public void Db_and_output_are_relative_to_the_repository_and_their_folders_are_created()
    {
        using var repo = new TempDirectory();

        var input = Build(repo.Path, "--db", "data/graph.db", "--output", "out");

        Assert.Equal(Path.Combine(repo.Path, "data", "graph.db"), input.DatabasePath);
        Assert.Equal(Path.Combine(repo.Path, "out"), input.OutputReportsPath);
        Assert.True(Directory.Exists(Path.Combine(repo.Path, "data")));
        Assert.True(Directory.Exists(Path.Combine(repo.Path, "out")));
        Assert.True(Directory.Exists(Path.Combine(repo.Path, "logs")));
    }

    [Fact]
    public void Db_and_output_default_to_codebase_folder_and_reports()
    {
        using var repo = new TempDirectory();

        var input = Build(repo.Path);

        Assert.Equal(repo.Path, input.RepositoryPath);
        Assert.Equal(Path.Combine(repo.Path, ".codebase", "codebase.db"), input.DatabasePath);
        Assert.Equal(Path.Combine(repo.Path, "reports"), input.OutputReportsPath);
    }

    [Fact]
    public void Absolute_db_path_is_kept()
    {
        using var repo = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var absolute = Path.Combine(elsewhere.Path, "graph.db");

        var input = Build(repo.Path, "--db", absolute);

        Assert.Equal(absolute, input.DatabasePath);
    }

    [Theory]
    [InlineData(new[] { "--force-reindex", "true" }, true)]
    [InlineData(new[] { "--force-reindex", "false" }, false)]
    [InlineData(new string[0], false)]
    public void Force_reindex_comes_from_the_command_line(string[] args, bool expected)
    {
        using var repo = new TempDirectory();

        Assert.Equal(expected, Build(repo.Path, args).ForceReindex);
    }

    [Fact]
    public void Force_reindex_falls_back_to_configuration()
    {
        using var repo = new TempDirectory();

        var input = AnalysisRunOptions.BuildInput(
            TestConfig.FromPairs(("CodebaseAnalysis:ForceReindex", "true")), repo.Path, _ => { });

        Assert.True(input.ForceReindex);
    }

    [Theory]
    [InlineData("--reports", "smells, di")]
    [InlineData("--report", "code_smells,InyeccionDependenciasExtensiones")]
    public void Reports_accept_aliases_and_full_names(string key, string value)
    {
        using var repo = new TempDirectory();

        var input = Build(repo.Path, key, value);

        Assert.Equal([ReportType.CodeSmells, ReportType.InyeccionDependenciasExtensiones], input.RequestedReports);
    }

    [Fact]
    public void Unknown_report_is_warned_and_dropped()
    {
        var warnings = new List<string>();

        var reports = AnalysisRunOptions.ResolveRequestedReports(TestConfig.FromArgs("--reports", "nope,di"), warnings.Add);

        Assert.Equal([ReportType.InyeccionDependenciasExtensiones], reports);
        var warning = Assert.Single(warnings);
        Assert.StartsWith("⚠️ Tipo de informe desconocido: nope", warning);
    }

    [Fact]
    public void Without_cli_list_the_configured_list_is_used()
    {
        var reports = AnalysisRunOptions.ResolveRequestedReports(
            TestConfig.FromPairs(("CodebaseAnalysis:RequestedReports:0", "errores"), ("CodebaseAnalysis:RequestedReports:1", "deuda")),
            _ => { });

        Assert.Equal([ReportType.ErroresYBugs, ReportType.DeudaTecnicaHotspots], reports);
    }

    [Fact]
    public void Empty_lists_mean_all_reports()
    {
        var none = AnalysisRunOptions.ResolveRequestedReports(TestConfig.FromArgs(), _ => { });
        var onlyUnknown = AnalysisRunOptions.ResolveRequestedReports(TestConfig.FromArgs("--reports", "nope"), _ => { });

        Assert.Equal(Enum.GetValues<ReportType>(), none);
        Assert.Equal(Enum.GetValues<ReportType>(), onlyUnknown);
    }

    [Theory]
    [InlineData(new[] { "--log-level", "Warning" }, LogLevel.Warning)]
    [InlineData(new[] { "--loglevel", "error" }, LogLevel.Error)]
    [InlineData(new[] { "--log-level", "verbose" }, LogLevel.Trace)]
    [InlineData(new[] { "--trace" }, LogLevel.Trace)]
    [InlineData(new[] { "--debug" }, LogLevel.Debug)]
    [InlineData(new[] { "--debug", "true" }, LogLevel.Debug)]
    [InlineData(new[] { "--log-level", "error", "--trace" }, LogLevel.Error)]
    [InlineData(new string[0], LogLevel.Information)]
    public void Log_level_follows_the_documented_precedence(string[] args, LogLevel expected)
    {
        Assert.Equal(expected, AnalysisRunOptions.ResolveLogLevel(TestConfig.FromArgs(args), args));
    }

    [Fact]
    public void Log_level_falls_back_to_Logging_section()
    {
        var level = AnalysisRunOptions.ResolveLogLevel(TestConfig.FromPairs(("Logging:LogLevel:Default", "Warning")), []);

        Assert.Equal(LogLevel.Warning, level);
    }

    [Theory]
    [InlineData(null, 0, false)]
    [InlineData("4", 4, false)]
    [InlineData("0", 0, false)]
    [InlineData("-1", 0, true)]
    [InlineData("muchos", 0, true)]
    public void Max_parallel_auditors_is_read_and_defaults_to_no_limit(string? raw, int expected, bool warned)
    {
        using var repo = new TempDirectory();
        var warnings = new List<string>();

        var input = AnalysisRunOptions.BuildInput(TestConfig.FromPairs(("CodebaseAnalysis:Reports:MaxParallelAuditors", raw)), repo.Path, warnings.Add);

        Assert.Equal(expected, input.MaxParallelAuditors);
        Assert.Equal(warned, warnings.Any(w => w.Contains("CodebaseAnalysis:Reports:MaxParallelAuditors", StringComparison.Ordinal)));
    }
}
