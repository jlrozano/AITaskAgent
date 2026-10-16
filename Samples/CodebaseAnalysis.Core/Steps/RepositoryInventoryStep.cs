using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Enrichment.Java;
using CodebaseAnalysis.Core.Enrichment.Web;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using Microsoft.Extensions.Configuration;

namespace CodebaseAnalysis.Core.Steps;

/// <summary>
/// Paso 5 del pipeline de preparación (spec §4.1): inventario del repositorio sin LLM. Une el recorrido de ficheros en disco, la
/// clasificación por source set, el conteo de líneas de producción y lo que el grafo sabe (entradas, tablas, ficheros con nodos,
/// estado de los enriquecedores y deriva de cbm), escribe <c>reports/_repository_inventory.json</c>, deja el inventario en el
/// contexto (<see cref="AnalysisContextKeys.RepositoryInventory"/>) y su versión compacta en
/// <see cref="ReportFacetInput.RepositoryInventory"/>.
/// </summary>
/// <remarks>
/// Devuelve el mismo <see cref="ReportFacetInput"/> que recibe, con los campos nuevos rellenos. No falla duro: si algo se
/// rompe por dentro es un aviso y el input sale intacto (sin inventario en el contexto), de modo que los pasos siguientes
/// trabajan sin él; solo la cancelación se propaga. Con <c>CodebaseAnalysis:Preparation:Mode = Off</c> no hace nada (la
/// factoría tampoco lo inserta; esto es la red de seguridad). Determinista: mismo grafo y mismo disco, mismo JSON salvo
/// <c>generated_at</c>.
/// </remarks>
public sealed class RepositoryInventoryStep : IStep<ReportFacetInput, ReportFacetInput>
{
    public const string StepName = "RepositoryInventoryStep";

    /// <summary>Puntos de entrada de producción que se guardan en el artefacto (el total se cuenta aparte).</summary>
    public const int MaxEntryPoints = 200;

    public const int MaxTables = 100;

    /// <summary>
    /// Tablas del grafo que se piden al lector: todas las razonables, porque se funden con las del SQL de las cadenas y el recorte a
    /// <see cref="MaxTables"/> se aplica después, sobre la unión.
    /// </summary>
    private const int TablesReadLimit = 50_000;
    public const int MaxConfigurationFiles = 200;

    /// <summary>Ficheros sin nodos en el grafo que se listan como muestra.</summary>
    public const int NotInGraphSampleSize = 20;

    /// <summary>
    /// Se pide al lector del grafo bastantes más entradas de las que se guardan: se filtran después por source set y el recorte
    /// se aplica sobre las de producción.
    /// </summary>
    private const int EntryPointsReadLimit = 50_000;

    /// <summary>Enriquecedor que cubre cada lenguaje; un lenguaje que no está aquí no tiene enriquecedor.</summary>
    private static readonly Dictionary<string, string> EnricherByLanguage = new(StringComparer.Ordinal)
    {
        ["java"] = JavaGraphWriter.EnricherName,
        ["jsp"] = StrutsJspWriter.EnricherName,
        ["csharp"] = DotNetEnricherIdentity.EnricherName
    };

    private static readonly Dictionary<string, string> ModuleKindByBuildFile = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pom.xml"] = "maven",
        ["build.gradle"] = "gradle",
        ["build.gradle.kts"] = "gradle",
        ["build.xml"] = "ant",
        ["package.json"] = "node",
        ["go.mod"] = "go",
        ["Cargo.toml"] = "cargo",
        ["pyproject.toml"] = "python"
    };

    private static readonly HashSet<string> ModuleProjectExtensions = new(StringComparer.OrdinalIgnoreCase) { ".csproj", ".fsproj", ".vbproj" };

    private static readonly HashSet<string> ConfigurationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".properties", ".xml", ".yml", ".yaml", ".config"
    };

    private readonly int _sqlScanMaxFiles;
    private readonly TimeSpan? _sqlScanTimeBudget;

    /// <param name="sqlScanMaxFiles">Ficheros que lee como mucho el escaneo del SQL de las cadenas (<see cref="SqlTableScanner.DefaultMaxFiles"/>).</param>
    /// <param name="sqlScanTimeBudget">Tiempo que le dedica como mucho (<see cref="SqlTableScanner.DefaultTimeBudget"/>).</param>
    public RepositoryInventoryStep(int sqlScanMaxFiles = SqlTableScanner.DefaultMaxFiles, TimeSpan? sqlScanTimeBudget = null)
    {
        _sqlScanMaxFiles = sqlScanMaxFiles;
        _sqlScanTimeBudget = sqlScanTimeBudget;
    }

    public string Name => StepName;
    public IResume? Resume => null;
    public int MaxRetries => 1;
    public int MiillisecondsBetweenRetries => 500;
    public TimeSpan? Timeout => TimeSpan.FromMinutes(10);
    public Type InputType => typeof(ReportFacetInput);
    public Type OutputType => typeof(ReportFacetInput);

    public async Task<ReportFacetInput> ExecuteAsync(
        ReportFacetInput input,
        PipelineContext context,
        int attempt,
        ReportFacetInput? lastStepResult,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var options = PreparationOptions.FromServices(context.ServiceProvider);
        foreach (var warning in options.Warnings)
            context.Report(StepName, AnalysisLogLevel.Warning, $"⚠️ {warning}");
        if (options.Mode == PreparationMode.Off)
            return input;

        context.Report(StepName, AnalysisLogLevel.Info, "🗂️ Inventariando el repositorio (módulos, source sets, entradas, lenguajes, líneas de producción)...");
        try
        {
            var inventory = await BuildAsync(input, context, options, cancellationToken);

            var reportsPath = ReportsPathOf(input, context);
            if (PreparationArtifacts.TryWrite(reportsPath, inventory) is { } writeWarning)
                context.Report(StepName, AnalysisLogLevel.Warning, writeWarning);

            context.Set(AnalysisContextKeys.RepositoryInventory, inventory);
            input.RepositoryInventory = RepositoryInventorySummary.ToJson(inventory);

            foreach (var warning in inventory.Warnings)
                context.Report(StepName, AnalysisLogLevel.Warning, warning);
            context.Report(StepName, AnalysisLogLevel.Info,
                $"🗂️ Inventario: {inventory.ProductionLoc.Files.Count} ficheros de producción, {inventory.ProductionLoc.Total} líneas, " +
                $"{inventory.Modules.Count} módulos, {inventory.EntryPointsTotal} puntos de entrada, {inventory.Persistence.TablesTotal} tablas, {inventory.Languages.Count} lenguajes.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Report(StepName, AnalysisLogLevel.Warning,
                $"⚠️ No se pudo construir el inventario del repositorio: {ex.Message}. Los pasos siguientes siguen sin él.");
        }

        return input;
    }

    private static string ReportsPathOf(ReportFacetInput input, PipelineContext context) =>
        input.OutputReportsPath is { Length: > 0 } fromInput
            ? fromInput
            : context.Get<AnalysisPipelineInput>(AnalysisContextKeys.PipelineInput)?.OutputReportsPath ?? "";

    private async Task<RepositoryInventory> BuildAsync(
        ReportFacetInput input, PipelineContext context, PreparationOptions options, CancellationToken ct)
    {
        var pipelineInput = context.Get<AnalysisPipelineInput>(AnalysisContextKeys.PipelineInput);
        var repository = input.RepositoryPath is { Length: > 0 } repo ? repo
            : pipelineInput?.RepositoryPath is { Length: > 0 } configured ? configured
            : Directory.GetCurrentDirectory();
        var root = Path.GetFullPath(repository);
        var databasePath = input.DatabasePath is { Length: > 0 } db ? db : pipelineInput?.DatabasePath ?? "";
        var reportsPath = ReportsPathOf(input, context);
        var warnings = new List<string>();

        var configuration = context.ServiceProvider.GetService(typeof(IConfiguration)) as IConfiguration;
        var javaOptions = configuration != null ? JavaAnalyzerOptions.FromConfiguration(configuration) : new JavaAnalyzerOptions();
        var testRoots = TestRoots.Resolve(root, javaOptions.TestRoots);

        // Disco: un único recorrido que comparten el conteo de líneas, los módulos y los ficheros de configuración.
        var files = RepositoryFileWalker.Walk(root, string.IsNullOrWhiteSpace(reportsPath) ? null : reportsPath);
        ct.ThrowIfCancellationRequested();
        var counted = ProductionLineCounter.Count(root, files, options, testRoots);
        warnings.AddRange(counted.Warnings);

        // Grafo.
        var graph = await InventoryGraphReader.ReadAsync(databasePath, EntryPointsReadLimit, TablesReadLimit, ct);
        warnings.AddRange(graph.Warnings);

        var production = counted.Production;

        // Código que no es producción ni test ni nada que se pueda afirmar: no entra en las líneas ni en el mapa de dominios,
        // así que el informe no debe dar «los ficheros» por completos sin decirlo.
        var unclassified = counted.Files.Where(f => f.SourceSet == SourceSets.Unknown).ToList();
        if (unclassified.Count > 0)
        {
            warnings.Add(
                $"⚠️ {unclassified.Count} fichero(s) de código no se pudieron clasificar (ni producción, ni test, ni otro conjunto) y no cuentan como producción: " +
                $"{string.Join(", ", unclassified.Take(NotInGraphSampleSize).Select(f => f.Path))}{(unclassified.Count > NotInGraphSampleSize ? ", ..." : "")}. " +
                "Revisa si son parte de la aplicación antes de dar por completo el total de ficheros.");
        }

        // Las tablas que el grafo no ve: el SQL escrito en las cadenas de los .java y .cs de producción. Un fallo del escaneo es un aviso.
        var sqlScan = ScanSql(root, production, warnings, ct);
        context.Set<IReadOnlyList<SqlTableReference>>(AnalysisContextKeys.InventorySqlTables, sqlScan.References);
        await WriteSqlTablesToGraphAsync(databasePath, sqlScan, context, warnings, ct);
        var (tables, tablesTotal) = MergeTables(graph.Tables, sqlScan);
        var byLanguage = ProductionLineCounter.ByLanguage(production);
        var entryPoints = graph.EntryPoints
            .Where(e => e.File.Length == 0 || SourceSetClassifier.Classify(e.File, options.CountedExtensions, testRoots) is SourceSets.Production or SourceSets.Unknown)
            .ToList();

        // El artefacto guarda solo MaxEntryPoints; el mapa de dominios necesita todas para medir cuántas entradas tiene cada subsistema.
        context.Set<IReadOnlyList<InventoryEntryPoint>>(AnalysisContextKeys.InventoryEntryPoints, entryPoints);

        var configurationFiles = files
            .Where(f => IsConfigurationFile(f) && SourceSetClassifier.Classify(f, options.CountedExtensions, testRoots) is SourceSets.Production or SourceSets.Unknown)
            .ToList();

        var fingerprint = context.Get<GraphProvenance>(AnalysisContextKeys.GraphProvenance)?.Fingerprint ?? graph.IndexFingerprint;

        return new RepositoryInventory
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            GraphFingerprint = fingerprint,
            Modules = DetectModules(files),
            SourceSets = SummarizeSourceSets(counted.Files),
            EntryPoints = [.. entryPoints.Take(MaxEntryPoints)],
            EntryPointsTotal = entryPoints.Count,
            Persistence = new InventoryPersistence
            {
                Tables = tables,
                TablesTotal = tablesTotal,
                ConfigurationFiles = [.. configurationFiles.Take(MaxConfigurationFiles)],
                ConfigurationFilesTotal = configurationFiles.Count
            },
            Languages = [.. byLanguage.Select(l => LanguageStatusOf(l, graph))],
            GraphCoverage = CoverageOf(production, graph, warnings),
            ProductionLoc = new ProductionLines
            {
                Total = production.Sum(f => (long)f.Lines),
                ByLanguage = byLanguage,
                Files = [.. production.Select(f => new FileLines { Path = f.Path, Language = f.Language, Lines = f.Lines })]
            },
            Warnings = warnings
        };
    }

    private SqlTableScan ScanSql(string root, IReadOnlyList<CountedFile> production, List<string> warnings, CancellationToken ct)
    {
        try
        {
            var scan = SqlTableScanner.Scan(root, production.Select(f => f.Path), ct, _sqlScanMaxFiles, _sqlScanTimeBudget);
            if (scan.StoppedBecause != null)
            {
                warnings.Add(
                    $"⚠️ El escaneo del SQL escrito en cadenas se detuvo {(scan.StoppedBecause == SqlTableScan.StoppedMaxFiles ? "al llegar al máximo de ficheros" : "al agotar su tiempo")} " +
                    $"tras {scan.FilesScanned} fichero(s): las tablas del inventario pueden estar incompletas.");
            }
            return scan;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"⚠️ No se pudo escanear el SQL escrito en cadenas ({ex.Message}): las tablas del inventario son solo las del grafo.");
            return new SqlTableScan();
        }
    }

    /// <summary>
    /// E2: las tablas del SQL de las cadenas, como nodos <c>Table</c> con aristas <c>QUERIES</c>/<c>MODIFIES</c> desde el método o la clase
    /// que contiene cada literal (<see cref="Enrichment.AnalysisGraph.SqlTableGraphWriter"/>). Va aquí porque es donde están las referencias
    /// con su fichero y su línea (no hay que volver a leer el disco) y el grafo ya está indexado y enriquecido; el inventario de esta
    /// ejecución ya leyó el grafo, y los lectores de tablas no cuentan las de esta pasada
    /// (<see cref="Enrichment.AnalysisGraph.AnalysisGraphLabels.NotDerivedTablePredicate"/>), así que ni el inventario ni el mapa de
    /// dominios cambian por ella. Un escaneo detenido escribe lo que encontró; un fallo es un aviso.
    /// </summary>
    private static async Task WriteSqlTablesToGraphAsync(
        string databasePath, SqlTableScan scan, PipelineContext context, List<string> warnings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            return;
        try
        {
            var stats = await Enrichment.AnalysisGraph.SqlTableGraphWriter.WriteAsync(databasePath, scan.References, ct);
            context.Report(StepName, AnalysisLogLevel.Info,
                $"🗄️ Tablas del SQL en el grafo: {stats.TablesCreated + stats.TablesReused} tabla(s), {stats.QueriesEdges} QUERIES y {stats.ModifiesEdges} MODIFIES; " +
                $"{stats.ReferencesUnresolved} referencia(s) de ficheros sin nodos en el grafo.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"⚠️ No se pudieron escribir en el grafo las tablas del SQL de las cadenas ({ex.Message}): el chat no tendrá la vista 'tables'.");
        }
    }

    /// <summary>
    /// Las tablas del grafo y, además, las que solo se nombran en SQL de cadenas (sin distinguir mayúsculas: la del grafo manda), de más a
    /// menos referenciadas y por nombre, recortadas a <see cref="MaxTables"/>; el total es el de la unión.
    /// </summary>
    internal static (IReadOnlyList<InventoryTable> Tables, int Total) MergeTables(IReadOnlyList<InventoryTable> graphTables, SqlTableScan scan)
    {
        var known = new HashSet<string>(graphTables.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var merged = graphTables
            .Concat(scan.Tables.Where(t => !known.Contains(t.Name)).Select(t => new InventoryTable
            {
                Name = t.Name, Schema = t.Schema, References = t.References, Source = InventoryTableSources.SqlLiteral
            }))
            .ToList();
        merged.Sort((a, b) => a.References != b.References ? b.References.CompareTo(a.References) : string.CompareOrdinal(a.Name, b.Name));
        return ([.. merged.Take(MaxTables)], merged.Count);
    }

    /// <summary>Módulos por ficheros de build (<c>pom.xml</c>, <c>build.gradle</c>, <c>.csproj</c>...); sin ninguno, un único módulo raíz.</summary>
    internal static IReadOnlyList<InventoryModule> DetectModules(IReadOnlyList<string> relativeFiles)
    {
        var modules = new List<InventoryModule>();
        foreach (var file in relativeFiles)
        {
            var name = Path.GetFileName(file);
            string? kind = ModuleKindByBuildFile.GetValueOrDefault(name);
            if (kind == null && ModuleProjectExtensions.Contains(Path.GetExtension(name)))
                kind = "dotnet";
            if (kind == null)
                continue;

            var slash = file.LastIndexOf('/');
            modules.Add(new InventoryModule { Path = slash < 0 ? "" : file[..slash], Kind = kind, BuildFile = name });
        }

        if (modules.Count == 0)
            return [new InventoryModule { Path = "", Kind = "root" }];

        modules.Sort((a, b) =>
        {
            var byPath = string.CompareOrdinal(a.Path, b.Path);
            return byPath != 0 ? byPath : string.CompareOrdinal(a.BuildFile, b.BuildFile);
        });
        return modules;
    }

    private static IReadOnlyList<SourceSetSummary> SummarizeSourceSets(IReadOnlyList<CountedFile> counted) =>
    [
        .. SourceSets.All
            .Select(set => (Set: set, Files: counted.Where(f => f.SourceSet == set).ToList()))
            .Where(g => g.Files.Count > 0)
            .Select(g => new SourceSetSummary { SourceSet = g.Set, Files = g.Files.Count, Lines = g.Files.Sum(f => (long)f.Lines) })
    ];

    private static bool IsConfigurationFile(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        if (RepositoryFileWalker.BuildFiles.Contains(name))
            return false;
        var extension = Path.GetExtension(name);
        return ConfigurationExtensions.Contains(extension)
            || (extension.Equals(".json", StringComparison.OrdinalIgnoreCase) && name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase));
    }

    private static LanguageStatus LanguageStatusOf(LanguageLines language, InventoryGraphData graph)
    {
        if (!EnricherByLanguage.TryGetValue(language.Language, out var enricher))
        {
            return new LanguageStatus
            {
                Language = language.Language, Files = language.Files, Status = LanguageStatuses.NotEnriched,
                Detail = "no hay enriquecedor para este lenguaje: el grafo es el de cbm, sin referencias resueltas"
            };
        }

        var run = graph.EnricherRuns.FirstOrDefault(r => r.Enricher == enricher);
        var (status, detail) = run switch
        {
            null => (LanguageStatuses.NotEnriched, $"{enricher} no dejó ninguna pasada registrada"),
            { Status: EnrichmentRuns.StatusOk } => (LanguageStatuses.Enriched, $"pasada #{run.RunId} de {enricher} correcta"),
            { Status: EnrichmentRuns.StatusFailed } => (LanguageStatuses.Failed, $"la última pasada de {enricher} falló: {run.Message}".TrimEnd(' ', ':')),
            { Status: EnrichmentRuns.StatusSkipped } => (LanguageStatuses.NotEnriched, $"{enricher} se saltó: {run.Message}".TrimEnd(' ', ':')),
            _ => (LanguageStatuses.NotEnriched, $"solo hay pasadas de {enricher} sobre otra indexación del grafo")
        };

        return new LanguageStatus
        {
            Language = language.Language, Files = language.Files, Status = status, Enricher = enricher,
            RunId = status == LanguageStatuses.Enriched ? run?.RunId : null, Detail = detail
        };
    }

    private static GraphCoverage CoverageOf(IReadOnlyList<CountedFile> production, InventoryGraphData graph, List<string> warnings)
    {
        var missing = production.Where(f => !graph.GraphFiles.Contains(f.Path)).Select(f => f.Path).ToList();
        var inGraph = production.Count - missing.Count;

        if (graph.GraphFiles.Count > 0 && production.Count > 0 && inGraph == 0)
            warnings.Add("⚠️ Ninguna ruta del grafo casa con un fichero de producción del disco: ¿se indexó otra carpeta o con otras rutas? La cobertura del grafo no es fiable.");

        return new GraphCoverage
        {
            FilesOnDisk = production.Count,
            FilesInGraph = inGraph,
            FilesNotInGraph = missing.Count,
            NotInGraphSample = [.. missing.Take(NotInGraphSampleSize)],
            CbmMatch = graph.CbmMatch
        };
    }
}
