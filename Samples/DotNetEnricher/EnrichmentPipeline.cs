using System.Diagnostics;
using System.Text.Json;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Support;
using DotNetEnricher.Enrichers;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetEnricher;

/// <summary>Lo que necesita una pasada: la BD, la raíz del repositorio y las compilaciones ya cargadas.</summary>
public sealed record EnrichmentRequest
{
    /// <summary>La copia del grafo del análisis, nunca la BD de la caché de codebase-memory-mcp.</summary>
    public required string DatabasePath { get; init; }

    /// <summary>Raíz del repositorio analizado: la que usa cbm para el <c>file_path</c> y los qualified_names.</summary>
    public required string RepoRoot { get; init; }

    /// <summary>Proyecto de cbm; null para tomar el mayoritario en <c>nodes</c>.</summary>
    public string? Project { get; init; }

    public IReadOnlyList<Compilation> Compilations { get; init; } = [];
    public IReadOnlyList<CsProjectInfo> CsProjects { get; init; } = [];

    /// <summary>Huella de la configuración para <c>enrichment_runs</c>; null para calcularla con <see cref="EnrichmentPipeline.ConfigFingerprint"/>.</summary>
    public string? ConfigFingerprint { get; init; }

    /// <summary>Solución cargada, relativa a la raíz del repositorio (para el registro de la pasada).</summary>
    public string? SolutionPath { get; init; }

    /// <summary>Versiones de Roslyn y MSBuild, para <c>tool_version</c>.</summary>
    public string? ToolVersion { get; init; }

    /// <summary>Avisos de la carga de la solución (<c>WorkspaceFailed</c>).</summary>
    public IReadOnlyList<string> LoadDiagnostics { get; init; } = [];
}

public sealed record EnrichmentResult
{
    public required string Project { get; init; }
    public required long RunId { get; init; }
    public required EnrichmentOutcome Outcome { get; init; }
    public required IReadOnlyDictionary<string, EnricherPatchStats> PatchStats { get; init; }

    /// <summary>Algún enriquecedor supera el <see cref="EnrichmentPipeline.MissThreshold"/> de patches sin nodo.</summary>
    public bool ExceedsMissThreshold => PatchStats.Values.Any(s => s.Attempts > 0 && (double)s.Missed / s.Attempts > EnrichmentPipeline.MissThreshold);
}

/// <summary>El grafo o los argumentos no permiten enriquecer (sin nodos, sin proyecto…): código de salida 1.</summary>
public sealed class EnrichmentInputException(string message) : Exception(message);

/// <summary>
/// Una pasada completa sobre la copia del grafo, con el mismo patrón que el enriquecedor Java de la app
/// (<c>JavaGraphWriter</c>): conexión de escritura masiva, una sola transacción que primero borra la salida de la pasada
/// anterior (nodos creados y aristas con la marca de cada enriquecedor) y al final registra la pasada en
/// <c>enrichment_runs</c>. Si algo falla, el rollback deja el grafo como estaba y la app sigue con su fallback.
/// No carga la solución: recibe las compilaciones, así se prueba sin MSBuild.
/// </summary>
public static class EnrichmentPipeline
{
    /// <summary>Nombre de la pasada en <c>enrichment_runs</c>. Cada enriquecedor marca lo suyo con su propio nombre.</summary>
    public const string EnricherName = DotNetEnricherIdentity.EnricherName;

    /// <summary>Versión de lo que se escribe; la misma que consulta la app (fichero enlazado).</summary>
    public const string EnricherVersion = DotNetEnricherIdentity.EnricherVersion;

    /// <summary>Fracción de patches sin nodo por enriquecedor por encima de la cual la pasada acaba con código 2.</summary>
    public const double MissThreshold = 0.05;

    /// <summary>Enriquecedores en su orden de ejecución, con sus estrategias registradas en DI como en el original.</summary>
    public static IReadOnlyList<IGraphEnricher> CreateEnrichers()
    {
        var services = new ServiceCollection();
        services.AddGraphEnricher<SemanticMethodEnricher>();
        services.AddGraphEnricher<AttributeEnricher>();   // Order 2 (empate): tras Semantic (necesita las Interface creadas), antes que el resto
        services.AddGraphEnricher<CallEnricher>();        // Order 3 (empate, antes que DI): CALLS exactas en lugar de las de cbm, antes de Project/Analytics que las leen
        services.AddGraphEnricher<DiEnricher>();
        services.AddGraphEnricher<RouteEnricher>();
        services.AddGraphEnricher<FieldEnricher>();        // Order 5: campos con READS/WRITES exactos (Roslyn)
        services.AddGraphEnricher<ExtensionPointEnricher>();
        services.AddGraphEnricher<IssueEnricher>();        // Order 7: detected_issues por método (compilador + reglas IOperation)
        services.AddGraphEnricher<ThrowsEnricher>();       // Order 7 (empate): THROWS exactas, throws/rethrows/swallows_exceptions (reglas de catch de IssueEnricher)
        services.AddGraphEnricher<ProjectEnricher>();
        services.AddGraphEnricher<TypeRoleEnricher>();     // Order 8 (empate): tras Project, que crea los CsProject donde escribe la abstraccion
        services.AddGraphEnricher<CommentEnricher>();      // Order 9: docstring completo, TODO/FIXME/HACK, código comentado (trivia)
        services.AddGraphEnricher<ConstantEnricher>();     // Order 9: credenciales en claro y nombres de tipo de reflexión, resueltos como constantes
        services.AddGraphEnricher<SensitiveApiEnricher>(); // Order 9: sensitive_apis/sensitive_categories por símbolo (E/S, procesos, red, deserialización, reflexión…)
        services.AddGraphEnricher<ConcurrencyEnricher>();  // Order 9: concurrency_markers por método; is_static_mutable/is_thread_safe_type por campo (tras FieldEnricher)
        services.AddGraphEnricher<ConfigOptionsEnricher>();

        // FlowEnricher strategies — generic
        services.AddTransient<IFlowEntryProvider, BackgroundServiceEntryProvider>();
        services.AddTransient<IFlowEntryProvider, HandlesEntryProvider>();
        services.AddTransient<IStepResolver,      DefaultStepResolver>();
        services.AddGraphEnricher<FlowEnricher>();

        services.AddGraphEnricher<ContractEnricher>();     // Order 12: contratos (public_props/enum values) + doc_params/returns + doc_coverage (Roslyn)
        services.AddGraphEnricher<CyclomaticEnricher>();   // Order 13: McCabe real (Roslyn)
        services.AddGraphEnricher<HashEnricher>();         // Order 13 (empate): body_hash/contract_hash por método (tras ThrowsEnricher) y type_hash por tipo
        services.AddGraphEnricher<MetricsEnricher>();       // Order 14: fan-in/out (referencias exactas de Roslyn), agregados, percentiles (SQL)

        // DeadCode suppressors — generic
        services.AddTransient<IDeadCodeSuppressor, TestConventionSuppressor>();
        services.AddTransient<IDeadCodeSuppressor, ReflectionTargetSuppressor>();
        services.AddGraphEnricher<ReachabilityEnricher>(); // Order 15: dead code (BFS sobre las referencias exactas de Roslyn)
        services.AddGraphEnricher<GraphAnalyticsEnricher>(); // Order 16: acoplamiento-clase, ciclos (Tarjan), capas (SQL)

        using var serviceProvider = services.BuildServiceProvider();
        // OrderBy es estable: ante el empate de Order manda el orden de registro.
        return [.. serviceProvider.GetRequiredService<IEnumerable<IGraphEnricher>>().OrderBy(e => e.Order)];
    }

    /// <summary>Huella de lo que cambia el resultado además del código: la solución y el proyecto de cbm.</summary>
    public static string ConfigFingerprint(string? solutionRelativePath, string? project) =>
        EnrichmentRuns.ConfigFingerprint([solutionRelativePath?.Replace('\\', '/'), project]);

    public static Task<EnrichmentResult> RunAsync(
        EnrichmentRequest request, TextWriter log, IProgress<string>? progress = null, CancellationToken ct = default) =>
        RunAsync(request, CreateEnrichers(), log, progress, ct);

    /// <summary>Con otra lista de enriquecedores (los tests, para provocar un fallo a mitad de pasada).</summary>
    internal static async Task<EnrichmentResult> RunAsync(
        EnrichmentRequest request, IReadOnlyList<IGraphEnricher> enrichers, TextWriter log, IProgress<string>? progress, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        using var conn = await EnrichmentMarks.OpenForBulkWriteAsync(request.DatabasePath, ct);
        var project = request.Project;
        if (string.IsNullOrWhiteSpace(project))
        {
            project = await GraphProject.ResolveAsync(conn, "", ct);
            if (project.Length == 0)
                throw new EnrichmentInputException("El grafo no tiene nodos con proyecto: no hay nada que enriquecer (¿se indexó el repositorio?).");
        }
        var indexFingerprint = await EnrichmentRuns.IndexFingerprintAsync(conn, ct);
        var configFingerprint = request.ConfigFingerprint ?? ConfigFingerprint(request.SolutionPath, project);

        using var tx = conn.BeginTransaction();

        // cbm deja algunos nodos con properties que no es JSON; un solo json_extract/json_remove sin json_valid sobre ellos
        // aborta la consulta y revierte la pasada. La app los repara al detectar el grafo; aquí, también con el CLI suelto.
        var repairedNodes = await EnrichmentMarks.RepairInvalidPropertiesAsync(conn, tx, ct);
        if (repairedNodes > 0)
            log.WriteLine($"  -> Nodos con properties vacías o que no eran JSON válido, reparadas (el texto queda en cbm_raw_properties): {repairedNodes}.");

        int removedNodes = 0, removedEdges = 0;
        foreach (var name in enrichers.Select(e => e.Name).Distinct(StringComparer.Ordinal))
        {
            var (n, e) = await EnrichmentMarks.RemovePreviousOutputAsync(conn, tx, name, ct);
            removedNodes += n;
            removedEdges += e;
        }
        if (removedNodes + removedEdges > 0)
            log.WriteLine($"  -> Salida de la pasada anterior retirada: {removedNodes} nodos y {removedEdges} aristas.");

        // cbm indexa bin/, obj/ y los ficheros generados (los .json y .xml de bin/ daban miles de nodos): ningún
        // enriquecedor, métrica, ciclo ni comunidad debe verlos. La app ya los retira al detectar el grafo; aquí se
        // garantiza también al usar el CLI suelto.
        var (artifactNodes, artifactEdges) = await GeneratedArtifacts.RemoveFromGraphAsync(conn, tx, ct);
        if (artifactNodes > 0)
        {
            log.WriteLine($"  -> Salida de compilación y ficheros generados retirados del grafo: {artifactNodes} nodos y {artifactEdges} aristas.");
            removedNodes += artifactNodes;
            removedEdges += artifactEdges;
        }

        var repository = new GraphRepository(conn, tx) { Provenance = new ProvenanceStamp(EnricherVersion, startedAt) };
        var ctx = new EnrichmentContext
        {
            Repository   = repository,
            Project      = project,
            RepoRoot     = request.RepoRoot,
            Compilations = request.Compilations,
            CsProjects   = request.CsProjects
        };

        var timings = new Dictionary<string, long>(StringComparer.Ordinal);
        int step = 1;
        foreach (var enricher in enrichers)
        {
            ct.ThrowIfCancellationRequested();
            log.WriteLine($"[{step}/{enrichers.Count}] {enricher.Name}...");
            var enricherWatch = Stopwatch.StartNew();
            repository.Provenance.Enricher = enricher.Name;
            await enricher.EnrichAsync(ctx, progress);
            timings[enricher.Name] = enricherWatch.ElapsedMilliseconds;
            step++;
        }

        var (filesAnalyzed, filesUnmatched) = await CountFilesAsync(ctx, ct);
        var stats = ctx.Stats.ByEnricher;
        var outcome = new EnrichmentOutcome
        {
            FilesAnalyzed = filesAnalyzed,
            FilesUnmatched = filesUnmatched,
            NodesCreated = repository.NodesCreated,
            NodesUpdated = repository.NodesPatched,
            NodesRemoved = removedNodes,
            EdgesCreated = repository.EdgesWritten,
            CbmEdgesReplaced = ctx.CbmEdgesReplaced.Values.Sum(),
            Unresolved = stats.Values.Any(s => s.Missed > 0)
                ? JsonSerializer.Serialize(stats.Where(kv => kv.Value.Missed > 0).ToDictionary(
                    kv => kv.Key, kv => new { missed = kv.Value.Missed, attempts = kv.Value.Attempts, samples = kv.Value.SampleMisses }))
                : null,
            Diagnostics = request.LoadDiagnostics.Count > 0
                ? JsonSerializer.Serialize(new { workspace_failed = request.LoadDiagnostics.Count, samples = request.LoadDiagnostics.Take(20) })
                : null,
            Details = JsonSerializer.Serialize(new
            {
                solution = request.SolutionPath,
                projects = request.CsProjects.Select(p => new { p.Name, directory = p.RelativeDirectory, p.IsExecutable, p.IsTest }),
                enrichers = enrichers.Select(e => new
                {
                    name = e.Name,
                    ms = timings.GetValueOrDefault(e.Name),
                    patched = stats.GetValueOrDefault(e.Name)?.Patched ?? 0,
                    missed = stats.GetValueOrDefault(e.Name)?.Missed ?? 0
                }),
                // Aristas de cbm sustituidas por tipo y el detalle de los enriquecedores que las sustituyen (calls, throws).
                cbm_edges_replaced = ctx.CbmEdgesReplaced,
                enricher_details = ctx.EnricherDetails
            })
        };

        var runId = await EnrichmentRuns.InsertAsync(conn, new EnrichmentRun
        {
            Enricher = EnricherName,
            EnricherVersion = EnricherVersion,
            ToolVersion = request.ToolVersion,
            IndexFingerprint = indexFingerprint ?? "",
            ConfigFingerprint = configFingerprint,
            StartedAt = startedAt,
            DurationMs = stopwatch.ElapsedMilliseconds,
            Status = EnrichmentRuns.StatusOk,
            Outcome = outcome
        }, ct, tx);

        tx.Commit();
        return new EnrichmentResult { Project = project, RunId = runId, Outcome = outcome with { RunId = runId }, PatchStats = stats };
    }

    /// <summary>Ficheros C# analizados (los que no excluye el filtro de cbm) y cuántos no tienen ningún nodo en el grafo.</summary>
    private static async Task<(int Analyzed, int Unmatched)> CountFilesAsync(EnrichmentContext ctx, CancellationToken ct)
    {
        var filter = new CbmExclusionFilter(ctx.RepoRoot);
        var analyzed = ctx.Compilations
            .SelectMany(c => c.SyntaxTrees)
            .Select(t => t.FilePath)
            .Where(p => !filter.ShouldExclude(p))
            .Select(p => SymbolQn.Relativize(p, ctx.RepoRoot))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (analyzed.Count == 0) return (0, 0);

        ct.ThrowIfCancellationRequested();
        var indexed = (await ctx.Repository.QueryAsync<string>(
                "SELECT DISTINCT file_path FROM nodes WHERE project = @project AND file_path IS NOT NULL",
                new { project = ctx.Project }))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (analyzed.Count, analyzed.Count(f => !indexed.Contains(f)));
    }
}
