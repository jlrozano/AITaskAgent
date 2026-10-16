using System.Diagnostics;
using System.Text;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodebaseAnalysis.Core.Steps;

/// <summary>
/// Enriquecimiento Roslyn de las soluciones .NET, en la fase <c>enrich</c> tras <see cref="LanguageEnricherStep"/>. Lanza
/// el CLI del repositorio (<c>Samples/DotNetEnricher</c>) como proceso aparte, igual que el analizador Java: así el
/// timeout (<c>DotNetEnricher:TimeoutSeconds</c>) mata un MSBuild colgado y la solución compilada no se queda en la memoria
/// de la app. El CLI escribe en una sola transacción y registra su pasada en <c>enrichment_runs</c>; si no está, no hay
/// solución, falla o se pasa de tiempo, el análisis sigue con el fallback del LLM (<see cref="SemanticEnrichmentStep"/>) y
/// un aviso, salvo con <c>DotNetEnricher:Required</c>. Sustituye a <c>BdpEnricherStep</c>, que dependía del CLI externo
/// BdpGraphEnricher instalado aparte.
/// </summary>
public sealed class DotNetEnricherStep : IStep<GraphStatusResult, GraphStatusResult>
{
    public const string StepName = "DotNetEnricherStep";

    /// <summary>Líneas finales de la salida de error del CLI que se citan en el aviso de un fallo.</summary>
    public const int ErrorLinesShown = 15;

    private readonly IStep<GraphStatusResult, GraphStatusResult>? _fallbackStep;

    public DotNetEnricherStep(IStep<GraphStatusResult, GraphStatusResult>? fallbackStep = null)
    {
        _fallbackStep = fallbackStep;
    }

    public string Name => StepName;
    public IResume? Resume => null;
    public int MaxRetries => 1;
    public int MiillisecondsBetweenRetries => 1000;
    /// <summary><c>CodebaseAnalysis:Timeouts:EnrichMinutes</c>; lo fija la factoría. Cubre el CLI y el fallback del LLM.</summary>
    public TimeSpan? Timeout { get; init; } = TimeSpan.FromMinutes(20);
    public Type InputType => typeof(GraphStatusResult);
    public Type OutputType => typeof(GraphStatusResult);

    public async Task<GraphStatusResult> ExecuteAsync(
        GraphStatusResult input,
        PipelineContext context,
        int attempt,
        GraphStatusResult? lastStepResult,
        CancellationToken cancellationToken)
    {
        // P2: un repositorio Java sin C# ya lo ha enriquecido LanguageEnricherStep; DotNetEnricher solo abre soluciones
        // .NET y el fallback del LLM no aportaría nada sobre el grafo del analizador Java.
        if (context.Metadata.TryGetValue(AnalysisContextKeys.DetectedLanguages, out var detected)
            && detected is DetectedLanguages languages
            && languages.HasFlag(DetectedLanguages.Java)
            && !languages.HasFlag(DetectedLanguages.CSharp))
        {
            context.Report(StepName, AnalysisLogLevel.Info,
                "ℹ️ Repositorio Java sin C#: el enriquecimiento es el del analizador Java; se omiten DotNetEnricher y el fallback del LLM.");
            return input;
        }

        var config = context.ServiceProvider.GetService<IConfiguration>();
        var options = config != null ? DotNetEnricherOptions.FromConfiguration(config) : new DotNetEnricherOptions();

        if (!options.Enabled)
        {
            context.Report(StepName, AnalysisLogLevel.Info, "ℹ️ DotNetEnricher deshabilitado en configuración (DotNetEnricher:Enabled = false).");
            return await FallbackAsync(input, context, attempt, lastStepResult, cancellationToken);
        }

        var pipelineInput = context.Get<AnalysisPipelineInput>(AnalysisContextKeys.PipelineInput);
        var repoPath = Path.GetFullPath(pipelineInput?.RepositoryPath is { Length: > 0 } repo ? repo : Directory.GetCurrentDirectory());
        var databasePath = input.DatabasePath is { Length: > 0 } db ? db : pipelineInput?.DatabasePath ?? "";

        // Una SolutionPath relativa es relativa al repositorio analizado, como las demás rutas.
        var solutionPath = string.IsNullOrWhiteSpace(options.SolutionPath)
            ? FindSolutionFile(repoPath)
            : Path.GetFullPath(options.SolutionPath, repoPath);
        var solutionRelative = solutionPath is null ? null : Path.GetRelativePath(repoPath, solutionPath).Replace('\\', '/');
        // La restauración solo entra en la huella si no es la de por defecto: así las pasadas ya registradas siguen valiendo.
        var configFingerprint = EnrichmentRuns.ConfigFingerprint(options.Restore == DotNetRestoreMode.Auto
            ? [solutionRelative, options.ProjectName]
            : [solutionRelative, options.ProjectName, "restore=" + DotNetRestoreModes.ToArgument(options.Restore)]);
        if (options.InvalidRestoreValue != null)
            context.Report(StepName, AnalysisLogLevel.Warning,
                $"⚠️ DotNetEnricher:Restore='{options.InvalidRestoreValue}' no es válido (auto, always/siempre o never/nunca): se usa auto.");

        // Registro de pasadas (P3): con una pasada correcta sobre esta indexación y esta configuración no se repite. Un
        // grafo enriquecido sin ninguna pasada registrada es de antes del registro (BdpGraphEnricher) y se deja como está.
        var (indexFingerprint, hasRun, hasAnyRun) = await ReadRunsAsync(databasePath, configFingerprint, cancellationToken);
        if (hasRun)
        {
            context.Report(StepName, AnalysisLogLevel.Info,
                "ℹ️ DotNetEnricher: el grafo ya está enriquecido para esta indexación y esta configuración; se omite. "
                + "Se repite si cbm vuelve a indexar el repositorio (p. ej. con «Forzar reindexación») o si cambia la configuración.");
            return input;
        }
        if (input.IsEnriched && !hasAnyRun)
        {
            context.Report(StepName, AnalysisLogLevel.Info, "ℹ️ El grafo ya contiene enriquecimiento estructural y semántico previo.");
            return input;
        }

        if (solutionPath is null || !File.Exists(solutionPath))
        {
            context.Report(StepName, AnalysisLogLevel.Warning,
                $"⚠️ No se encontró archivo .sln ni .slnx para enriquecimiento Roslyn en '{repoPath}'. Usando fallback semántico.");
            return await FallbackAsync(input, context, attempt, lastStepResult, cancellationToken);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var (cliPath, locateError) = LocateCli(options);
        if (cliPath is null)
            return await FailAsync(locateError!, null);

        context.Report(StepName, AnalysisLogLevel.Info, "🚀 Ejecutando enriquecimiento semántico Roslyn (DotNetEnricher)...");
        context.Report(StepName, AnalysisLogLevel.Info, $"   CLI: {cliPath}");
        context.Report(StepName, AnalysisLogLevel.Info, $"   Solución: {solutionPath}");
        context.Report(StepName, AnalysisLogLevel.Info, $"   Raíz: {repoPath}");
        context.Report(StepName, AnalysisLogLevel.Info, $"   DB: {databasePath}");
        if (!string.IsNullOrWhiteSpace(options.ProjectName))
            context.Report(StepName, AnalysisLogLevel.Info, $"   Proyecto: {options.ProjectName}");

        var arguments = BuildCliArguments(solutionPath, databasePath, repoPath, configFingerprint, options);

        CliRun run;
        try
        {
            run = await RunCliAsync(context, cliPath, arguments, options.TimeoutSeconds, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return await FailAsync($"DotNetEnricher no se pudo ejecutar: {ex.Message}", ex.Message);
        }

        if (run.TimedOut)
            return await FailAsync(
                $"DotNetEnricher superó su límite de {options.TimeoutSeconds} s (DotNetEnricher:TimeoutSeconds) y se ha detenido; el grafo queda como estaba.",
                "timeout");

        switch (run.ExitCode)
        {
            case 0:
                context.Report(StepName, AnalysisLogLevel.Success, $"✅ Enriquecimiento semántico Roslyn finalizado con éxito en {stopwatch.Elapsed.TotalSeconds:0.0} s.");
                break;
            case 2:
                context.Report(StepName, AnalysisLogLevel.Warning,
                    $"⚠️ Enriquecimiento Roslyn completado con avisos (código 2): algún enriquecedor no encontró en el grafo más del 5 % de los nodos que buscaba.{ErrorTail(run.StandardError)}");
                break;
            default:
                return await FailAsync(
                    $"DotNetEnricher falló con código {run.ExitCode} ({DescribeExitCode(run.ExitCode)}); el grafo queda como estaba.{ErrorTail(run.StandardError)}",
                    $"exit {run.ExitCode}: {Tail(run.StandardError, 5)}");
        }

        var (nodes, edges) = await GetGraphCountsAsync(databasePath, cancellationToken);
        return new GraphStatusResult
        {
            ExistedBefore = input.ExistedBefore,
            IsEnriched = true,
            TotalNodes = nodes,
            TotalEdges = edges,
            DatabasePath = input.DatabasePath,
            Facets = input.Facets
        };

        // Fallo del enriquecedor: con Required se para el análisis; si no, aviso, fila failed y fallback del LLM.
        async Task<GraphStatusResult> FailAsync(string message, string? runMessage)
        {
            if (options.Required)
                throw new InvalidOperationException($"[{StepName}] DotNetEnricher:Required: {message}");

            context.Report(StepName, AnalysisLogLevel.Warning, $"⚠️ {message} Se usa el enriquecimiento semántico por LLM.");
            if (runMessage != null)
                await RecordFailureAsync(databasePath, indexFingerprint, configFingerprint, startedAt, stopwatch, runMessage, cancellationToken);
            return await FallbackAsync(input, context, attempt, lastStepResult, cancellationToken);
        }
    }

    /// <summary>
    /// Argumentos del CLI. La restauración de paquetes la hace el CLI (ya corre fuera de proceso, con su timeout y el MSBuild
    /// localizado) y su caché va junto a la BD del análisis (<c>.codebase/enrichment/dotnet-restore</c>), nunca en el repositorio.
    /// </summary>
    internal static List<string> BuildCliArguments(
        string solutionPath, string databasePath, string repoPath, string configFingerprint, DotNetEnricherOptions options)
    {
        var arguments = new List<string>
        {
            "enrich", "--solution", solutionPath, "--db", databasePath, "--repo-root", repoPath,
            "--config-fingerprint", configFingerprint, "--verbose",
            "--restore", DotNetRestoreModes.ToArgument(options.Restore),
            "--restore-timeout", options.RestoreTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(databaseDirectory))
            arguments.AddRange(["--cache-dir", Path.Combine(databaseDirectory, "enrichment", RestoreCacheFolder)]);
        if (!string.IsNullOrWhiteSpace(options.ProjectName))
            arguments.AddRange(["--project", options.ProjectName]);
        return arguments;
    }

    /// <summary>Carpeta (dentro de <c>enrichment/</c>, junto a la BD del análisis, como la del analizador Java) donde el CLI deja los <c>project.assets.json</c> de la restauración.</summary>
    public const string RestoreCacheFolder = "dotnet-restore";

    private Task<GraphStatusResult> FallbackAsync(
        GraphStatusResult input, PipelineContext context, int attempt, GraphStatusResult? lastStepResult, CancellationToken ct) =>
        _fallbackStep != null
            ? _fallbackStep.ExecuteAsync(input, context, attempt, lastStepResult, ct)
            : Task.FromResult(input);

    /// <summary>
    /// Ruta del CLI: <c>DotNetEnricher:CliPath</c> si está configurada; si no, la que deja el build junto a la app
    /// (<c>dotnet-enricher/</c>, o la propia carpeta de la app si todo se compiló en un <c>OutDir</c> común). En Windows se
    /// prefiere el <c>.exe</c>; el <c>.dll</c> se lanza con <c>dotnet</c>.
    /// </summary>
    internal static (string? CliPath, string? Error) LocateCli(DotNetEnricherOptions options, string? baseDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(options.CliPath))
        {
            return File.Exists(options.CliPath)
                ? (Path.GetFullPath(options.CliPath), null)
                : (null, $"No existe el CLI configurado en DotNetEnricher:CliPath ('{options.CliPath}').");
        }

        baseDirectory ??= AppContext.BaseDirectory;
        var name = DotNetEnricherIdentity.ExecutableName;
        foreach (var folder in new[] { Path.Combine(baseDirectory, DotNetEnricherIdentity.BundleFolder), baseDirectory })
        {
            var exe = Path.Combine(folder, name + ".exe");
            if (OperatingSystem.IsWindows() && File.Exists(exe))
                return (Path.GetFullPath(exe), null);
            var dll = Path.Combine(folder, name + ".dll");
            if (File.Exists(dll))
                return (Path.GetFullPath(dll), null);
        }

        return (null, $"No se encontró el CLI '{name}' junto a la app ('{Path.Combine(baseDirectory, DotNetEnricherIdentity.BundleFolder)}'). "
            + "Se compila con la app; si se ha movido, configura DotNetEnricher:CliPath.");
    }

    /// <summary>
    /// Busca la solución en la raíz del repositorio y, si no está, en sus subcarpetas inmediatas
    /// (<c>src/</c> y similares). Nunca fuera: antes miraba también en la carpeta padre, que no es
    /// parte del repositorio analizado y podía contener una solución ajena.
    /// </summary>
    internal static string? FindSolutionFile(string dir)
    {
        if (!Directory.Exists(dir)) return null;

        IEnumerable<string> folders = [dir, .. Directory.GetDirectories(dir).Order(StringComparer.OrdinalIgnoreCase)];
        foreach (var folder in folders)
        {
            var solution = Directory.GetFiles(folder, "*.slnx", SearchOption.TopDirectoryOnly).FirstOrDefault()
                ?? Directory.GetFiles(folder, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (solution != null) return solution;
        }

        return null;
    }

    internal sealed record CliRun(int ExitCode, bool TimedOut, string StandardError);

    /// <summary>
    /// Lanza el CLI y reenvía cada línea de su salida al log (canal del indexador). Con <paramref name="timeoutSeconds"/>
    /// &gt; 0, al agotarse mata el proceso con todo su árbol y devuelve <see cref="CliRun.TimedOut"/>; una cancelación del
    /// análisis también lo mata, pero se relanza.
    /// </summary>
    private static async Task<CliRun> RunCliAsync(
        PipelineContext context, string cliPath, IReadOnlyList<string> arguments, int timeoutSeconds, CancellationToken ct)
    {
        var isDll = cliPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo
        {
            FileName = isDll ? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet" : cliPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (isDll) psi.ArgumentList.Add(cliPath);
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                context.Report(StepName, AnalysisLogLevel.Info, $"   [dotnet] {e.Data}", AnalysisEventTypes.Channels.Indexer);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (stderr) stderr.AppendLine(e.Data);
            context.Report(StepName, AnalysisLogLevel.Info, $"   [dotnet-err] {e.Data}", AnalysisEventTypes.Channels.Indexer);
        };

        if (!process.Start())
            throw new InvalidOperationException($"No se pudo iniciar el proceso de enriquecimiento: {psi.FileName}");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutSeconds > 0)
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await ProcessCancellation.WaitForExitOrKillAsync(process, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CliRun(-1, TimedOut: true, Text());
        }

        // Tras el Exited quedan por llegar las últimas líneas redirigidas: esta espera sin límite las vacía.
        await process.WaitForExitAsync(CancellationToken.None);
        return new CliRun(process.ExitCode, TimedOut: false, Text());

        string Text() { lock (stderr) return stderr.ToString(); }
    }

    internal static string DescribeExitCode(int exitCode) => exitCode switch
    {
        1 => "argumentos o entradas no válidos",
        3 => "la solución no se pudo cargar",
        4 => "error durante el enriquecimiento",
        _ => "código inesperado"
    };

    private static string ErrorTail(string stderr) =>
        string.IsNullOrWhiteSpace(stderr) ? "" : $"\n{Tail(stderr, ErrorLinesShown)}";

    private static string Tail(string text, int lines) =>
        string.Join("\n", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(lines));

    /// <summary>Huella de la indexación y si hay una pasada correcta con esta configuración, o alguna del enriquecedor.</summary>
    private static async Task<(string? IndexFingerprint, bool HasRun, bool HasAnyRun)> ReadRunsAsync(
        string databasePath, string configFingerprint, CancellationToken ct)
    {
        if (!File.Exists(databasePath))
            return (null, false, false);
        try
        {
            using var conn = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            await conn.OpenAsync(ct);
            var index = await EnrichmentRuns.IndexFingerprintAsync(conn, ct);
            var hasRun = await EnrichmentRuns.HasSuccessfulRunAsync(conn, DotNetEnricherIdentity.EnricherName,
                DotNetEnricherIdentity.EnricherVersion, index, configFingerprint, ct);
            var hasAny = (await EnrichmentRuns.ListAsync(conn, ct, DotNetEnricherIdentity.EnricherName)).Count > 0;
            return (index, hasRun, hasAny);
        }
        catch (SqliteException)
        {
            return (null, false, false);
        }
    }

    /// <summary>Fila <c>failed</c> en <c>enrichment_runs</c>; si ni eso se puede escribir, el aviso ya emitido basta.</summary>
    private static async Task RecordFailureAsync(
        string databasePath, string? indexFingerprint, string configFingerprint, DateTimeOffset startedAt, Stopwatch stopwatch,
        string message, CancellationToken ct)
    {
        try
        {
            EnrichmentMarks.AssertWorkingCopy(databasePath);
            using var conn = new SqliteConnection($"Data Source={databasePath}");
            await conn.OpenAsync(ct);
            await EnrichmentRuns.InsertAsync(conn, new EnrichmentRun
            {
                Enricher = DotNetEnricherIdentity.EnricherName,
                EnricherVersion = DotNetEnricherIdentity.EnricherVersion,
                IndexFingerprint = indexFingerprint ?? "",
                ConfigFingerprint = configFingerprint,
                StartedAt = startedAt,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Status = EnrichmentRuns.StatusFailed,
                Message = message
            }, ct);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            // La BD es la del propio análisis: si no se puede escribir, lo dirá el paso siguiente.
        }
    }

    private static async Task<(int TotalNodes, int TotalEdges)> GetGraphCountsAsync(string dbPath, CancellationToken ct)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT (SELECT COUNT(1) FROM nodes), (SELECT COUNT(1) FROM edges)";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return (reader.GetInt32(0), reader.GetInt32(1));
        }
        catch (SqliteException)
        {
            return (0, 0);
        }
    }

    public Task FinalizeAsync(IStepResult input, PipelineContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<(bool IsValid, string? Error)> ValidateAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
        => Task.FromResult((result is GraphStatusResult, (string?)null));
}
