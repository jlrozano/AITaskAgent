using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using System.Diagnostics;

namespace CodebaseAnalysis.Core.Steps;

public class BdpEnricherStep : IStep<GraphStatusResult, GraphStatusResult>
{
    private readonly IStep<GraphStatusResult, GraphStatusResult>? _fallbackStep;

    public const string StepName = "BdpEnricherStep";

    public string Name => StepName;
    public IResume? Resume => null;
    public int MaxRetries => 1;
    public int MiillisecondsBetweenRetries => 1000;
    /// <summary><c>CodebaseAnalysis:Timeouts:EnrichMinutes</c>; lo fija la factoría.</summary>
    public TimeSpan? Timeout { get; init; } = TimeSpan.FromMinutes(20);
    public Type InputType => typeof(GraphStatusResult);
    public Type OutputType => typeof(GraphStatusResult);

    public BdpEnricherStep(IStep<GraphStatusResult, GraphStatusResult>? fallbackStep = null)
    {
        _fallbackStep = fallbackStep;
    }

    public async Task<GraphStatusResult> ExecuteAsync(
        GraphStatusResult input, 
        PipelineContext context, 
        int attempt, 
        GraphStatusResult? lastStepResult, 
        CancellationToken cancellationToken)
    {
        // P2: un repositorio Java sin C# ya lo ha enriquecido LanguageEnricherStep; BdpGraphEnricher solo abre soluciones
        // .NET y el fallback del LLM no aportaría nada sobre el grafo del analizador Java.
        if (context.Metadata.TryGetValue(AnalysisContextKeys.DetectedLanguages, out var detected)
            && detected is Enrichment.DetectedLanguages languages
            && languages.HasFlag(Enrichment.DetectedLanguages.Java)
            && !languages.HasFlag(Enrichment.DetectedLanguages.CSharp))
        {
            context.Report(StepName, AnalysisLogLevel.Info,
                "ℹ️ Repositorio Java sin C#: el enriquecimiento es el del analizador Java; se omiten BdpGraphEnricher y el fallback del LLM.");
            return input;
        }

        var config = context.ServiceProvider.GetService<IConfiguration>();
        var bdpSection = config?.GetSection("BdpEnricher");

        bool enabled = bdpSection?.GetValue<bool>("Enabled", true) ?? true;
        if (!enabled)
        {
            context.Report(StepName, AnalysisLogLevel.Info, "ℹ️ BdpEnricher deshabilitado en configuración.");
            if (_fallbackStep != null)
            {
                return await _fallbackStep.ExecuteAsync(input, context, attempt, lastStepResult, cancellationToken);
            }
            return input;
        }

        if (input.IsEnriched)
        {
            context.Report(StepName, AnalysisLogLevel.Info, "ℹ️ El grafo ya contiene enriquecimiento estructural y semántico previo.");
            return input;
        }

        var (cliPath, error) = LocateBdpEnricherCli(bdpSection);
        if (cliPath == null)
        {
            bool required = bdpSection?.GetValue<bool>("Required", false) ?? false;
            if (required)
            {
                throw new InvalidOperationException($"[BdpEnricherStep] Fail-fast: {error}");
            }

            context.Report(StepName, AnalysisLogLevel.Warning, $"⚠️ {error}. Intentando fallback a enriquecimiento semántico.");
            if (_fallbackStep != null)
            {
                return await _fallbackStep.ExecuteAsync(input, context, attempt, lastStepResult, cancellationToken);
            }
            return input;
        }

        var pipelineInput = context.Get<AnalysisPipelineInput>(AnalysisContextKeys.PipelineInput);
        var repoPath = pipelineInput?.RepositoryPath ?? Directory.GetCurrentDirectory();
        // Una SolutionPath relativa es relativa al repositorio analizado, como las demás rutas.
        var solutionPath = bdpSection?["SolutionPath"];
        solutionPath = string.IsNullOrWhiteSpace(solutionPath)
            ? FindSolutionFile(repoPath)
            : Path.GetFullPath(solutionPath, repoPath);

        if (string.IsNullOrWhiteSpace(solutionPath) || !File.Exists(solutionPath))
        {
            context.Report(StepName, AnalysisLogLevel.Warning, $"⚠️ No se encontró archivo .sln ni .slnx para enriquecimiento Roslyn en '{pipelineInput?.RepositoryPath}'. Usando fallback semántico.");
            if (_fallbackStep != null)
            {
                return await _fallbackStep.ExecuteAsync(input, context, attempt, lastStepResult, cancellationToken);
            }
            return input;
        }

        var projectName = bdpSection?["ProjectName"];
        if (string.IsNullOrWhiteSpace(projectName))
        {
            projectName = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(solutionPath))) 
                ?? Path.GetFileNameWithoutExtension(solutionPath);
        }

        context.Report(StepName, AnalysisLogLevel.Info, $"🚀 Ejecutando enriquecimiento semántico Roslyn (BdpGraphEnricher)...");
        context.Report(StepName, AnalysisLogLevel.Info, $"   CLI: {cliPath}");
        context.Report(StepName, AnalysisLogLevel.Info, $"   Solución: {solutionPath}");
        context.Report(StepName, AnalysisLogLevel.Info, $"   DB: {input.DatabasePath}");
        context.Report(StepName, AnalysisLogLevel.Info, $"   Proyecto: {projectName}");

        await RunEnricherCliAsync(context, cliPath, solutionPath, input.DatabasePath, projectName, cancellationToken);

        // Recalcular conteos
        var (nodes, edges) = await GetGraphCountsAsync(input.DatabasePath, cancellationToken);

        return new GraphStatusResult
        {
            ExistedBefore = input.ExistedBefore,
            IsEnriched = true,
            TotalNodes = nodes,
            TotalEdges = edges,
            DatabasePath = input.DatabasePath
        };
    }

    private static (string? CliPath, string? Error) LocateBdpEnricherCli(IConfigurationSection? bdpSection)
    {
        var configured = bdpSection?["CliPath"];
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return (Path.GetFullPath(configured), null);
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(userProfile, "source", "BdpGraphEnricher", "src", "BdpGraphEnricher.CLI", "bin", "Release", "net10.0", "BdpGraphEnricher.CLI.exe"),
            Path.Combine(userProfile, "source", "BdpGraphEnricher", "src", "BdpGraphEnricher.CLI", "bin", "Debug", "net10.0", "BdpGraphEnricher.CLI.exe"),
            Path.Combine(userProfile, "source", "BdpGraphEnricher", "src", "BdpGraphEnricher.CLI", "bin", "Release", "net9.0", "BdpGraphEnricher.CLI.exe"),
            Path.Combine(userProfile, "source", "BdpGraphEnricher", "src", "BdpGraphEnricher.CLI", "bin", "Debug", "net9.0", "BdpGraphEnricher.CLI.exe"),
            Path.Combine(userProfile, "source", "BdpGraphEnricher", "src", "BdpGraphEnricher.CLI", "bin", "Release", "net8.0", "BdpGraphEnricher.CLI.exe"),
            Path.Combine(userProfile, "source", "BdpGraphEnricher", "src", "BdpGraphEnricher.CLI", "bin", "Debug", "net8.0", "BdpGraphEnricher.CLI.exe")
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                return (Path.GetFullPath(c), null);
            }
        }

        return (null, "No se encontró el ejecutable 'BdpGraphEnricher.CLI.exe'.");
    }

    /// <summary>
    /// Busca la solución en la raíz del repositorio y, si no está, en sus subcarpetas inmediatas
    /// (<c>src/</c> y similares). Nunca fuera: antes miraba también en la carpeta padre, que no es
    /// parte del repositorio analizado y podía contener una solución ajena.
    /// </summary>
    private static string? FindSolutionFile(string dir)
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

    private static async Task RunEnricherCliAsync(PipelineContext context, string cliPath, string solutionPath, string dbPath, string projectName, CancellationToken ct)
    {
        var isDll = cliPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var fileName = isDll ? "dotnet" : cliPath;
        var arguments = isDll
            ? $"\"{cliPath}\" enrich --solution \"{solutionPath}\" --db \"{dbPath}\" --project \"{projectName}\""
            : $"enrich --solution \"{solutionPath}\" --db \"{dbPath}\" --project \"{projectName}\"";

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"No se pudo iniciar el proceso de enriquecimiento: {fileName}");

        var stdOutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = proc.StandardError.ReadToEndAsync(ct);

        await ProcessCancellation.WaitForExitOrKillAsync(proc, ct);

        var stdout = await stdOutTask;
        var stderr = await stdErrTask;

        // Exit codes: 0 = OK, 2 = Warnings/Misses > 5% (no fatal), > 0 = Error
        if (proc.ExitCode == 2)
        {
            context.Report(StepName, AnalysisLogLevel.Warning, $"⚠️ Enriquecimiento completado con advertencias (código 2).\n{stderr}");
        }
        else if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"BdpGraphEnricher.CLI falló con código {proc.ExitCode}.\nError: {stderr}\nSalida: {stdout}");
        }
        else
        {
            context.Report(StepName, AnalysisLogLevel.Success, $"✅ Enriquecimiento semántico Roslyn finalizado con éxito.");
        }
    }

    private static async Task<(int TotalNodes, int TotalEdges)> GetGraphCountsAsync(string dbPath, CancellationToken ct)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Cache=Shared");
            await conn.OpenAsync(ct);

            using var cmd1 = conn.CreateCommand();
            cmd1.CommandText = "SELECT COUNT(1) FROM nodes;";
            var nodes = Convert.ToInt32(await cmd1.ExecuteScalarAsync(ct));

            using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = "SELECT COUNT(1) FROM edges;";
            var edges = Convert.ToInt32(await cmd2.ExecuteScalarAsync(ct));

            return (nodes, edges);
        }
        catch
        {
            return (0, 0);
        }
    }

    public Task FinalizeAsync(IStepResult input, PipelineContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<(bool IsValid, string? Error)> ValidateAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
        => Task.FromResult((result is GraphStatusResult, (string?)null));
}
