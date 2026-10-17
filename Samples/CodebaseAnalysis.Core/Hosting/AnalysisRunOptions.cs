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

    /// <summary>Conmutador de la CLI que valida los informes que hay en disco sin ejecutar el pipeline.</summary>
    public const string ValidateReportsSwitch = "--validate-reports";

    /// <summary>
    /// Quita <paramref name="name"/> (sin valor, o con <c>=true</c>/<c>=false</c>) de los argumentos antes de pasarlos a la
    /// configuración: el proveedor de línea de comandos toma el argumento siguiente como valor de un <c>--clave</c> suelto, así
    /// que <c>--validate-reports --repo X</c> se comería el <c>--repo</c>.
    /// </summary>
    public static string[] ExtractSwitch(string[] args, string name, out bool present)
    {
        present = false;
        var rest = new List<string>(args.Length);
        foreach (var arg in args)
        {
            if (arg.Equals(name, StringComparison.OrdinalIgnoreCase) || arg.Equals(name + "=true", StringComparison.OrdinalIgnoreCase))
                present = true;
            else if (!arg.Equals(name + "=false", StringComparison.OrdinalIgnoreCase))
                rest.Add(arg);
        }
        return [.. rest];
    }

    /// <summary>
    /// Flags de la CLI que exigen valor. Cualquier otro <c>--Seccion:Clave</c> (con «:») lo exige también; <c>--trace</c> y
    /// <c>--debug</c> no.
    /// </summary>
    private static readonly string[] ValueFlags =
    [
        "--repo", "--db", "--output", "--force-reindex", "--reports", "--report", "--log-level", "--loglevel",
        "--verify", "--validation-output", "--expected"
    ];

    /// <summary>
    /// Los flags que exigen valor y llegan sin él: al final de la línea o seguidos de otro <c>--flag</c>. El proveedor de línea
    /// de comandos ignora en silencio un <c>--clave</c> final sin valor, así que <c>--verify</c> o <c>--repo</c> al final
    /// arrancaban un análisis completo de pago con otra intención. Se llama con los argumentos tras <see cref="ExtractSwitch"/>.
    /// La forma <c>--clave=valor</c> lleva su valor y no se comprueba.
    /// </summary>
    public static IReadOnlyList<string> FindFlagsMissingValue(IReadOnlyList<string> args)
    {
        var missing = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Contains('='))
                continue;
            var needsValue = ValueFlags.Contains(arg, StringComparer.OrdinalIgnoreCase) || arg.Contains(':');
            if (needsValue && (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal)))
                missing.Add(arg);
        }
        return missing;
    }

    /// <summary>
    /// <c>--validation-output</c>: carpeta donde se escribe <c>_validation.json</c>, relativa al repositorio o absoluta; por
    /// defecto, la de informes. Sirve para validar un repositorio sin escribir en él. No crea nada en disco.
    /// </summary>
    public static string ResolveValidationOutputPath(IConfiguration configuration, string repositoryPath, string reportsPath)
    {
        var raw = configuration["validation-output"];
        return string.IsNullOrWhiteSpace(raw) ? reportsPath : Path.IsPathRooted(raw) ? raw : Path.GetFullPath(raw, repositoryPath);
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
