using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace DotNetEnricher;

/// <summary>Códigos de salida del CLI (ver la cabecera de Program.cs).</summary>
public static class ExitCodes
{
    public const int Ok               = 0;
    public const int InvalidInput     = 1;
    public const int Warnings         = 2;
    public const int LoadFailed       = 3;
    public const int EnrichmentFailed = 4;
}

public sealed record EnrichOptions
{
    public required FileInfo Solution { get; init; }
    public required FileInfo Database { get; init; }
    public DirectoryInfo? RepoRoot { get; init; }
    public string? Project { get; init; }
    public string? ConfigFingerprint { get; init; }
    public bool Verbose { get; init; }
    public string? MsBuildVersion { get; init; }
    public string? MsBuildPath { get; init; }

    /// <summary><c>auto</c>, <c>always</c>/<c>siempre</c> o <c>never</c>/<c>nunca</c> (<see cref="CodebaseAnalysis.Core.Enrichment.DotNetRestoreModes"/>).</summary>
    public string Restore { get; init; } = "auto";
    public int RestoreTimeoutSeconds { get; init; } = 300;

    /// <summary>Fuera del repositorio; null = <see cref="NuGetRestore.DefaultCacheRoot"/>.</summary>
    public DirectoryInfo? RestoreCacheDirectory { get; init; }
}

/// <summary>El comando <c>enrich</c>: valida las entradas, carga la solución y ejecuta <see cref="EnrichmentPipeline"/>.</summary>
public static class EnrichCommand
{
    // NoInlining: el JIT no debe ver los tipos de MSBuildWorkspace antes de que Program registre MSBuildLocator.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<int> RunAsync(EnrichOptions options, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var solution = options.Solution;
        var db = options.Database;

        output.WriteLine($"[DotNetEnricher] Solución: {solution.FullName}");
        output.WriteLine($"[DotNetEnricher] DB:       {db.FullName}");

        if (!solution.Exists) { error.WriteLine($"ERROR: no existe la solución '{solution.FullName}'."); return ExitCodes.InvalidInput; }
        if (!db.Exists)       { error.WriteLine($"ERROR: no existe la BD '{db.FullName}'.");            return ExitCodes.InvalidInput; }

        var repoRoot = options.RepoRoot?.FullName ?? FindRepositoryRoot(solution.Directory!);
        repoRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoRoot));
        if (!Directory.Exists(repoRoot)) { error.WriteLine($"ERROR: no existe la raíz del repositorio '{repoRoot}'."); return ExitCodes.InvalidInput; }

        var solutionRelative = Path.GetRelativePath(repoRoot, solution.FullName);
        if (solutionRelative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(solutionRelative))
        {
            error.WriteLine($"ERROR: la solución '{solution.FullName}' no está dentro de la raíz del repositorio '{repoRoot}'.");
            return ExitCodes.InvalidInput;
        }
        output.WriteLine($"[DotNetEnricher] Raíz:     {repoRoot}");
        output.WriteLine($"[DotNetEnricher] Proyecto: {(string.IsNullOrWhiteSpace(options.Project) ? "(el mayoritario en el grafo)" : options.Project)}");
        output.WriteLine();

        // ─── 0. Restaurar los paquetes NuGet fuera del repositorio ──────────────
        // Sin project.assets.json Roslyn no resuelve los paquetes y se pierden las reglas que dependen de sus tipos. Un fallo
        // aquí (sin acceso a los feeds, tiempo agotado) deja un aviso y la carga sigue sin paquetes.
        if (!CodebaseAnalysis.Core.Enrichment.DotNetRestoreModes.TryParse(options.Restore, out var restoreMode))
        {
            output.WriteLine($"[restore] ! --restore='{options.Restore}' no es válido (auto, always/siempre, never/nunca): se usa auto.");
            restoreMode = CodebaseAnalysis.Core.Enrichment.DotNetRestoreMode.Auto;
        }
        var cacheRoot = options.RestoreCacheDirectory?.FullName ?? NuGetRestore.DefaultCacheRoot(solution.FullName);
        RestoreOutcome restore;
        try
        {
            restore = await NuGetRestore.PrepareAsync(restoreMode, solution.FullName, repoRoot, cacheRoot, options.RestoreTimeoutSeconds,
                NuGetRestore.ResolveDotnetHost(options.MsBuildPath), output, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            restore = RestoreOutcome.Skipped($"la restauración de paquetes falló de forma inesperada ({ex.Message}); se sigue sin paquetes.");
        }
        output.WriteLine($"[restore] {restore.Message}");
        if (restore.Attempted && !restore.Succeeded)
            error.WriteLine($"AVISO: {restore.Message}");

        // ─── 1. Cargar solución con Roslyn ──────────────────────────────────────
        output.WriteLine("[carga] Cargando solución con Roslyn...");
        LoadedSolution loaded;
        try
        {
            loaded = await SolutionLoader.LoadAsync(solution.FullName, repoRoot, output, ct, restore.Properties);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            error.WriteLine($"ERROR: no se pudo cargar la solución: {ex.Message}");
            return ExitCodes.LoadFailed;
        }

        output.WriteLine($"  -> {loaded.Compilations.Count} proyecto(s) C# compilado(s), {loaded.Diagnostics.Count} aviso(s) de carga.");
        if (loaded.Compilations.Count == 0 || loaded.Compilations.All(c => !c.SyntaxTrees.Any()))
        {
            error.WriteLine("ERROR: la solución no tiene ningún proyecto C# con código cargado (ver los avisos de carga).");
            return ExitCodes.LoadFailed;
        }

        // ─── 2. Enriquecer en una sola transacción ──────────────────────────────
        var progress = options.Verbose ? new SyncProgress(msg => output.WriteLine($"  -> {msg}")) : null;
        EnrichmentResult result;
        try
        {
            result = await EnrichmentPipeline.RunAsync(new EnrichmentRequest
            {
                DatabasePath      = db.FullName,
                RepoRoot          = repoRoot,
                Project           = options.Project,
                Compilations      = loaded.Compilations,
                CsProjects        = loaded.Projects,
                ConfigFingerprint = options.ConfigFingerprint,
                SolutionPath      = solutionRelative,
                ToolVersion       = $"Roslyn {typeof(Compilation).Assembly.GetName().Version}; MSBuild {options.MsBuildVersion ?? "?"}",
                LoadDiagnostics   = loaded.Diagnostics
            }, output, progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (EnrichmentInputException ex)
        {
            error.WriteLine($"ERROR: {ex.Message}");
            return ExitCodes.InvalidInput;
        }
        catch (Exception ex)
        {
            error.WriteLine($"ERROR: falló el enriquecimiento; el grafo queda como estaba: {ex}");
            return ExitCodes.EnrichmentFailed;
        }

        output.WriteLine();
        var o = result.Outcome;
        output.WriteLine($"Enriquecimiento completado (enrichment_runs.id = {result.RunId}, proyecto {result.Project}): "
            + $"{o.FilesAnalyzed} ficheros ({o.FilesUnmatched} sin nodos en el grafo), {o.NodesCreated} nodos creados, "
            + $"{o.NodesUpdated} actualizados, {o.EdgesCreated} aristas escritas, {o.NodesRemoved} nodos de la pasada anterior retirados.");

        // ─── 3. Resumen de patches observables (F0.2) ───────────────────────────
        if (result.PatchStats.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("── Patches por enriquecedor (aciertos/fallos de QN) ──");
            foreach (var (enricher, s) in result.PatchStats.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var pct = s.Attempts == 0 ? 0.0 : (double)s.Missed / s.Attempts * 100.0;
                output.WriteLine($"  {enricher}: {s.Patched} patched / {s.Missed} missed ({pct:F1}% miss, {s.Attempts} intentos)");
                foreach (var qn in s.SampleMisses)
                    output.WriteLine($"      miss: {qn}");
            }
        }

        if (result.ExceedsMissThreshold)
        {
            error.WriteLine($"AVISO: al menos un enriquecedor supera el {EnrichmentPipeline.MissThreshold:P0} de patch-misses (exit 2).");
            return ExitCodes.Warnings;
        }
        return ExitCodes.Ok;
    }

    /// <summary>
    /// La carpeta con <c>.git</c> más cercana por encima de la solución, o la de la solución si no hay. La app pasa
    /// siempre <c>--repo-root</c>; esto es para el uso a mano.
    /// </summary>
    internal static string FindRepositoryRoot(DirectoryInfo solutionDirectory)
    {
        for (var dir = solutionDirectory; dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
        }
        return solutionDirectory.FullName;
    }

    /// <summary><see cref="Progress{T}"/> publica en el pool de hilos y desordena la salida; este escribe en el momento.</summary>
    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
