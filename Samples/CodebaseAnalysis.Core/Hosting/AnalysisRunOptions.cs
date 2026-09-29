using CodebaseAnalysis.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodebaseAnalysis.Core.Hosting;

/// <summary>
/// Resolución de <c>--repo</c>, <c>--db</c>, <c>--output</c>, <c>--force-reindex</c>, <c>--reports</c> y del
/// nivel de log contra la sección <c>CodebaseAnalysis</c>. Está partida en varios métodos porque la CLI
/// necesita el repositorio antes de configurar Serilog y el resto después de construir los roles.
/// </summary>
public static class AnalysisRunOptions
{
    public const string SectionName = "CodebaseAnalysis";

    /// <summary>
    /// <c>--repo</c> o <c>CodebaseAnalysis:RepositoryPath</c>, relativo al directorio actual, sin más
    /// heurísticas: la anterior, si encontraba una BD en la carpeta del sample, analizaba esa carpeta.
    /// </summary>
    public static string ResolveRepositoryPath(IConfiguration configuration, string? currentDirectory = null)
    {
        var rawRepo = configuration["repo"] ?? configuration.GetSection(SectionName)["RepositoryPath"];
        return Path.GetFullPath(string.IsNullOrWhiteSpace(rawRepo) ? "." : rawRepo, currentDirectory ?? Directory.GetCurrentDirectory());
    }

    /// <summary>
    /// <c>--log-level</c> (o <c>--loglevel</c>), después <c>--trace</c> y <c>--debug</c>, después
    /// <c>Logging:LogLevel:Default</c>; por defecto <see cref="LogLevel.Information"/>.
    /// </summary>
    /// <remarks>Recibe también los argumentos crudos: <c>--trace</c> y <c>--debug</c> sin valor no llegan a la configuración.</remarks>
    public static LogLevel ResolveLogLevel(IConfiguration configuration, string[] args)
    {
        var minLogLevelStr = configuration["log-level"]
            ?? configuration["loglevel"]
            ?? (configuration["trace"] != null || args.Any(a => a.Equals("--trace", StringComparison.OrdinalIgnoreCase)) ? "Trace" : null)
            ?? (configuration["debug"] != null || args.Any(a => a.Equals("--debug", StringComparison.OrdinalIgnoreCase)) ? "Debug" : null)
            ?? configuration["Logging:LogLevel:Default"]
            ?? "Information";

        return minLogLevelStr.ToLowerInvariant() switch
        {
            "trace" or "verbose" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "warning" or "warn" => LogLevel.Warning,
            "error" => LogLevel.Error,
            _ => LogLevel.Information
        };
    }

    /// <summary>
    /// <c>--reports</c> (o <c>--report</c>), separados por comas; sin lista, <c>CodebaseAnalysis:RequestedReports</c>;
    /// si queda vacía, todos. Un nombre desconocido se avisa por <paramref name="warn"/> y se descarta.
    /// </summary>
    public static List<ReportType> ResolveRequestedReports(IConfiguration configuration, Action<string> warn)
    {
        var cliReports = configuration["reports"] ?? configuration["report"];
        var reportNames = !string.IsNullOrWhiteSpace(cliReports)
            ? cliReports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : configuration.GetSection(SectionName).GetSection("RequestedReports").Get<List<string>>() ?? [];

        var requestedList = new List<ReportType>();
        foreach (var rName in reportNames)
        {
            try
            {
                requestedList.Add(AnalysisPipelineInput.ParseReportType(rName));
            }
            catch (Exception ex)
            {
                warn($"⚠️ {ex.Message}");
            }
        }

        return requestedList.Count > 0 ? requestedList : Enum.GetValues<ReportType>().ToList();
    }

    /// <summary>
    /// <c>--db</c> o <c>CodebaseAnalysis:DatabasePath</c> (por defecto <c>.codebase/codebase.db</c>), relativo al
    /// repositorio. No crea nada en disco.
    /// </summary>
    public static string ResolveDatabasePath(IConfiguration configuration, string repositoryPath)
    {
        var rawDb = configuration["db"] ?? configuration.GetSection(SectionName)["DatabasePath"] ?? Path.Combine(".codebase", "codebase.db");
        return Path.IsPathRooted(rawDb) ? rawDb : Path.GetFullPath(rawDb, repositoryPath);
    }

    /// <summary>
    /// <c>--output</c> o <c>CodebaseAnalysis:OutputReportsPath</c> (por defecto <c>reports</c>), relativo al
    /// repositorio. No crea nada en disco.
    /// </summary>
    public static string ResolveReportsPath(IConfiguration configuration, string repositoryPath)
    {
        var rawOutput = configuration["output"] ?? configuration.GetSection(SectionName)["OutputReportsPath"] ?? "reports";
        return Path.IsPathRooted(rawOutput) ? rawOutput : Path.GetFullPath(rawOutput, repositoryPath);
    }

    /// <summary>
    /// <c>CodebaseAnalysis:LogsPath</c> (por defecto <c>logs</c>), relativa al repositorio o absoluta: la carpeta del log de
    /// cada análisis. Con una absoluta la salida no cae dentro del repositorio analizado. No crea nada en disco.
    /// </summary>
    public static string ResolveLogsPath(IConfiguration configuration, string repositoryPath)
    {
        var rawLogs = configuration.GetSection(SectionName)["LogsPath"];
        rawLogs = string.IsNullOrWhiteSpace(rawLogs) ? "logs" : rawLogs;
        return Path.IsPathRooted(rawLogs) ? rawLogs : Path.GetFullPath(rawLogs, repositoryPath);
    }

    /// <summary>
    /// Aviso de <c>--debug</c> y <c>--trace</c>: el log guarda el cuerpo de cada petición al modelo, con código y fragmentos
    /// del repositorio. Null en los demás niveles.
    /// </summary>
    public static string? DebugLogWarning(LogLevel level, string logsPath) =>
        level <= LogLevel.Debug
            ? $"⚠️ Log en nivel {level}: '{logsPath}' guardará el cuerpo de cada petición al modelo, con código y fragmentos del repositorio. "
              + "No lo compartas ni lo subas al control de versiones; CodebaseAnalysis:LogsPath lo lleva fuera del repositorio."
            : null;

    /// <summary>
    /// Entrada del pipeline. <c>--db</c> y <c>--output</c> son relativos al repositorio. Crea las carpetas
    /// de la BD, de los informes y de los logs (<see cref="ResolveLogsPath"/>).
    /// </summary>
    public static AnalysisPipelineInput BuildInput(IConfiguration configuration, string repositoryPath, Action<string> warn)
    {
        var analysisSection = configuration.GetSection(SectionName);

        var dbPath = ResolveDatabasePath(configuration, repositoryPath);
        var outputReportsPath = ResolveReportsPath(configuration, repositoryPath);

        // Las carpetas se crean en la ubicación del código analizado.
        var dbDir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dbDir)) Directory.CreateDirectory(dbDir);
        Directory.CreateDirectory(outputReportsPath);
        Directory.CreateDirectory(ResolveLogsPath(configuration, repositoryPath));

        var forceReindex = configuration.GetValue<bool>("force-reindex",
            analysisSection.GetValue<bool>("ForceReindex", false));

        return new AnalysisPipelineInput
        {
            RepositoryPath = repositoryPath,
            DatabasePath = dbPath,
            OutputReportsPath = outputReportsPath,
            ForceReindex = forceReindex,
            RequestedReports = ResolveRequestedReports(configuration, warn),
            AuditorMaxToolIterations = ResolveAuditorMaxToolIterations(analysisSection.GetSection("Auditors"), warn),
            AuditorMaxToolIterationsByRole = ResolveAuditorMaxToolIterationsByRole(analysisSection.GetSection("Auditors"), warn),
            MaxParallelAuditors = ResolveMaxParallelAuditors(analysisSection.GetSection("Reports"), warn),
            SourceDefaultEncoding = configuration[$"{Enrichment.JavaAnalyzerOptions.SectionName}:{nameof(Enrichment.JavaAnalyzerOptions.DefaultEncoding)}"] ?? string.Empty,
            BackupsToKeep = ResolveBackupsToKeep(analysisSection.GetSection("Backups"), warn)
        };
    }

    /// <summary>
    /// <c>CodebaseAnalysis:Backups:Keep</c>; <see cref="AnalysisPipelineInput.DefaultBackupsToKeep"/> si falta, y también con
    /// aviso si no es un entero ≥ 0. 0 = sin límite.
    /// </summary>
    public static int ResolveBackupsToKeep(IConfigurationSection backups, Action<string> warn)
    {
        var raw = backups["Keep"];
        if (string.IsNullOrWhiteSpace(raw))
            return AnalysisPipelineInput.DefaultBackupsToKeep;
        if (int.TryParse(raw, out var keep) && keep >= 0)
            return keep;
        warn($"⚠️ CodebaseAnalysis:Backups:Keep='{raw}' no es un entero ≥ 0: se usa {AnalysisPipelineInput.DefaultBackupsToKeep}.");
        return AnalysisPipelineInput.DefaultBackupsToKeep;
    }

    /// <summary><c>CodebaseAnalysis:Reports:MaxParallelAuditors</c>; 0 si falta, y también con aviso si no es un entero ≥ 0.</summary>
    public static int ResolveMaxParallelAuditors(IConfigurationSection reports, Action<string> warn)
    {
        var raw = reports["MaxParallelAuditors"];
        if (string.IsNullOrWhiteSpace(raw))
            return 0;
        if (int.TryParse(raw, out var max) && max >= 0)
            return max;
        warn($"⚠️ CodebaseAnalysis:Reports:MaxParallelAuditors='{raw}' no es un entero ≥ 0: se usa 0 (sin tope propio).");
        return 0;
    }

    /// <summary><c>CodebaseAnalysis:Auditors:MaxToolIterations</c>; un valor menor que 1 se ignora con aviso.</summary>
    public static int ResolveAuditorMaxToolIterations(IConfigurationSection auditors, Action<string> warn)
    {
        var raw = auditors["MaxToolIterations"];
        if (string.IsNullOrWhiteSpace(raw))
            return AnalysisPipelineInput.DefaultAuditorMaxToolIterations;
        if (int.TryParse(raw, out var max) && max >= 1)
            return max;
        warn($"⚠️ CodebaseAnalysis:Auditors:MaxToolIterations='{raw}' no es un entero ≥ 1: se usa {AnalysisPipelineInput.DefaultAuditorMaxToolIterations}.");
        return AnalysisPipelineInput.DefaultAuditorMaxToolIterations;
    }

    /// <summary><c>CodebaseAnalysis:Auditors:MaxToolIterationsByRole</c>: rol → tope; los valores menores que 1 se ignoran con aviso.</summary>
    public static Dictionary<string, int> ResolveAuditorMaxToolIterationsByRole(IConfigurationSection auditors, Action<string> warn)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in auditors.GetSection("MaxToolIterationsByRole").GetChildren())
        {
            if (int.TryParse(entry.Value, out var max) && max >= 1)
                result[entry.Key] = max;
            else
                warn($"⚠️ CodebaseAnalysis:Auditors:MaxToolIterationsByRole:{entry.Key}='{entry.Value}' no es un entero ≥ 1: se ignora.");
        }
        return result;
    }
}
