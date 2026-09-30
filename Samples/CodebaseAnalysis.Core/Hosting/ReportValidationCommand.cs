using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Reports;
using Microsoft.Extensions.Configuration;

namespace CodebaseAnalysis.Core.Hosting;

/// <summary>
/// <c>--validate-reports</c> de la CLI: valida los informes que hay en disco (<see cref="ReportValidator"/>) sin construir roles
/// LLM ni ejecutar el pipeline, y escribe <c>_validation.json</c>. Lee <c>--repo</c>, <c>--output</c> (la carpeta de informes),
/// <c>--db</c>, <c>--validation-output</c> y <c>--expected</c> (un <c>expected-findings.json</c>). No crea la carpeta de logs
/// ni nada dentro del repositorio si <c>--validation-output</c> apunta fuera.
/// </summary>
public static class ReportValidationCommand
{
    /// <summary>Problemas que se escriben en la consola; el resto queda en el JSON.</summary>
    public const int MaxIssuesShown = 40;

    /// <summary>Código de salida: 0 si la validación se hizo (tenga o no problemas), 1 si no se pudo hacer.</summary>
    public static int Run(IConfiguration configuration, string repositoryPath, IAnalysisReporter reporter)
    {
        var source = ReportValidationSummary.SourceName;
        var reportsPath = AnalysisRunOptions.ResolveReportsPath(configuration, repositoryPath);
        var dbPath = AnalysisRunOptions.ResolveDatabasePath(configuration, repositoryPath);
        var outputPath = AnalysisRunOptions.ResolveValidationOutputPath(configuration, repositoryPath, reportsPath);
        if (!Directory.Exists(reportsPath))
        {
            reporter.Report(AnalysisLogLevel.Error, source, $"❌ No existe la carpeta de informes '{reportsPath}'.");
            return 1;
        }

        reporter.Report(AnalysisLogLevel.Info, source,
            $"🧪 Validando los informes de '{reportsPath}' contra '{repositoryPath}'{(File.Exists(dbPath) ? $" y el grafo de '{dbPath}'" : " (sin grafo)")}.");
        var result = ReportValidator.Validate(new ReportValidationRequest(repositoryPath, reportsPath, File.Exists(dbPath) ? dbPath : null));

        if (configuration["expected"] is { Length: > 0 } rawExpected)
        {
            var expectedPath = Path.IsPathRooted(rawExpected) ? rawExpected : Path.GetFullPath(rawExpected);
            try
            {
                result.Expected = ExpectedFindingsComparer.Compare(result, ExpectedFindingsDocument.Load(expectedPath), expectedPath);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                reporter.Report(AnalysisLogLevel.Error, source, $"❌ No se pudo leer '{expectedPath}': {ex.Message}");
                return 1;
            }
        }

        var warning = ReportValidationFile.TryWrite(outputPath, result);
        foreach (var (level, message) in ReportValidationSummary.Describe(result, warning == null ? Path.Combine(outputPath, ReportValidationFile.FileName) : null))
            reporter.Report(level, source, message);
        foreach (var line in DescribeIssues(result, MaxIssuesShown))
            reporter.Report(AnalysisLogLevel.Info, source, line);
        if (warning != null)
        {
            reporter.Report(AnalysisLogLevel.Error, source, warning);
            return 1;
        }
        return 0;
    }

    /// <summary>Los problemas, primero los errores, hasta <paramref name="max"/>, con el informe y la línea.</summary>
    public static IReadOnlyList<string> DescribeIssues(ReportValidationResult result, int max)
    {
        var issues = result.Reports
            .SelectMany(r => r.Issues.Select(i => (r.FileName, Issue: i)))
            .OrderBy(x => x.Issue.Level == ReportValidationLevels.Error ? 0 : 1)
            .ToList();
        var lines = issues.Take(max)
            .Select(x => $"   {(x.Issue.Level == ReportValidationLevels.Error ? "✗" : "△")} {x.FileName}:{x.Issue.Line} [{x.Issue.Check}] {x.Issue.Message}")
            .ToList();
        if (issues.Count > max)
            lines.Add($"   … y {issues.Count - max} más en {ReportValidationFile.FileName}.");
        return lines;
    }
}
