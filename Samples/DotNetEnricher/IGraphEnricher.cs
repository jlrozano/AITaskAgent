using Microsoft.CodeAnalysis;

namespace DotNetEnricher;

public interface IGraphEnricher
{
    string Name  { get; }
    int    Order { get; }
    Task   EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null);
}

/// <summary>
/// Proyecto C# de la solución: nombre (el del .csproj), carpeta relativa a la raíz del repositorio con <c>/</c>
/// (vacía si el .csproj está en la raíz), si genera un ejecutable y si es de test. Sustituye a la inferencia del
/// proyecto por el segundo segmento del qualified_name, que solo acertaba con los .csproj en la raíz del repositorio y
/// un nombre de proyecto de cbm sin puntos. <see cref="ProjectReferences"/> y <see cref="Packages"/> son lo que declara el
/// .csproj (nombres de los proyectos referenciados y de los paquetes NuGet), no lo que el código usa de verdad.
/// </summary>
public sealed record CsProjectInfo(
    string Name, string RelativeDirectory, bool IsExecutable = false, bool IsTest = false,
    IReadOnlyList<string>? ProjectReferences = null, IReadOnlyList<string>? Packages = null);

/// <summary>Un registro de DI tal como lo vio <see cref="Enrichers.DiEnricher"/>: los símbolos de servicio e implementación, el tiempo de vida y el sitio.</summary>
internal sealed record DiRegistrationFact(ITypeSymbol ServiceType, ITypeSymbol ImplementationType, string Lifetime, string FilePath, int Line);

public class EnrichmentContext
{
    public required GraphRepository Repository  { get; init; }
    public required string           Project    { get; init; }
    public required string           RepoRoot   { get; init; }
    public IReadOnlyList<Compilation> Compilations { get; init; } = [];

    /// <summary>Proyectos C# de la solución, para <see cref="CsProjectOf"/>.</summary>
    public IReadOnlyList<CsProjectInfo> CsProjects { get; init; } = [];

    /// <summary>
    /// Colector de estadísticas de patch por enriquecedor (aciertos/fallos de QN).
    /// </summary>
    public EnrichmentStats Stats { get; } = new();

    private ReferenceIndex? _references;
    private Task<GraphSymbols>? _graphSymbols;

    /// <summary>
    /// Referencias exactas entre los símbolos del código fuente. Se construye la primera vez que alguien lo pide (una
    /// pasada sobre todos los árboles) y lo comparten los enriquecedores que lo usan.
    /// </summary>
    internal ReferenceIndex References => _references ??= BuildReferences();

    /// <summary>Milisegundos que tardó el índice de referencias; 0 si nadie lo ha pedido todavía.</summary>
    internal long ReferenceIndexMs { get; private set; }

    private ReferenceIndex BuildReferences()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var index = ReferenceIndex.Build(Compilations);
        ReferenceIndexMs = watch.ElapsedMilliseconds;
        return index;
    }

    /// <summary>Correspondencia símbolo-nodo del grafo, tal como esté en el momento de la primera petición.</summary>
    internal Task<GraphSymbols> GetGraphSymbolsAsync() => _graphSymbols ??= GraphSymbols.LoadAsync(this);

    /// <summary>Registros de DI que ha encontrado <see cref="Enrichers.DiEnricher"/>, para los que cruzan servicios (captive dependency).</summary>
    internal List<DiRegistrationFact> DiRegistrations { get; } = [];

    /// <summary>
    /// Aristas heurísticas de cbm borradas para sustituirlas por las exactas, por tipo (<c>CALLS</c>, <c>THROWS</c>). La
    /// pasada suma el total en <c>cbm_edges_replaced</c> de <c>enrichment_runs</c> y deja el desglose en <c>details</c>.
    /// </summary>
    internal SortedDictionary<string, int> CbmEdgesReplaced { get; } = new(StringComparer.Ordinal);

    /// <summary>Detalle propio de un enriquecedor para <c>details</c> de <c>enrichment_runs</c>, por clave (<c>calls</c>, <c>throws</c>).</summary>
    internal SortedDictionary<string, object> EnricherDetails { get; } = new(StringComparer.Ordinal);

    /// <summary>Ficheros C# que analiza la pasada (los que no excluye el filtro de cbm), relativos a la raíz como el <c>file_path</c> de cbm.</summary>
    internal HashSet<string> AnalyzedFiles => _analyzedFiles ??= LoadAnalyzedFiles();

    private HashSet<string>? _analyzedFiles;

    private readonly Dictionary<Compilation, System.Collections.Immutable.ImmutableArray<Diagnostic>> _diagnostics = [];
    private HashSet<string>? _filesWithUnresolvedSymbols;

    /// <summary>
    /// Diagnósticos de una compilación (<c>Compilation.GetDiagnostics()</c>, que liga todos los cuerpos y es caro), calculados
    /// una vez y compartidos por los enriquecedores que los leen.
    /// </summary>
    internal System.Collections.Immutable.ImmutableArray<Diagnostic> DiagnosticsOf(Compilation compilation) =>
        _diagnostics.TryGetValue(compilation, out var cached) ? cached : _diagnostics[compilation] = compilation.GetDiagnostics();

    /// <summary>
    /// Errores de un símbolo o un tipo que el compilador no encuentra: tipo o espacio de nombres (CS0246, CS0234), ensamblado
    /// sin referenciar (CS0012), nombre (CS0103), miembro (CS1061, típico de un método de extensión de un paquete que falta) o
    /// tipo predefinido (CS0518). Es lo que deja un paquete NuGet sin restaurar.
    /// </summary>
    internal static readonly HashSet<string> UnresolvedSymbolDiagnostics = ["CS0246", "CS0234", "CS0012", "CS0103", "CS1061", "CS0518"];

    /// <summary>
    /// Ficheros analizados (como <see cref="AnalyzedFiles"/>) con algún error de <see cref="UnresolvedSymbolDiagnostics"/>:
    /// en ellos el índice de referencias puede no ver llamadas (un receptor de tipo desconocido no da símbolo ni candidatos).
    /// </summary>
    internal HashSet<string> FilesWithUnresolvedSymbols => _filesWithUnresolvedSymbols ??= LoadFilesWithUnresolvedSymbols();

    private HashSet<string> LoadFilesWithUnresolvedSymbols()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var compilation in Compilations)
            foreach (var diagnostic in DiagnosticsOf(compilation))
                if (diagnostic.Severity == DiagnosticSeverity.Error && UnresolvedSymbolDiagnostics.Contains(diagnostic.Id)
                    && diagnostic.Location.SourceTree?.FilePath is { Length: > 0 } path
                    && AnalyzedFiles.Contains(SymbolQn.Relativize(path, RepoRoot)))
                    files.Add(SymbolQn.Relativize(path, RepoRoot));
        return files;
    }

    private HashSet<string> LoadAnalyzedFiles()
    {
        var filter = new CbmExclusionFilter(RepoRoot);
        return Compilations
            .SelectMany(c => c.SyntaxTrees)
            .Select(t => t.FilePath)
            .Where(p => !filter.ShouldExclude(p))
            .Select(p => SymbolQn.Relativize(p, RepoRoot))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Proyecto C# al que pertenece un fichero (ruta relativa a la raíz del repositorio, como el <c>file_path</c> de cbm):
    /// el de la carpeta más larga que lo contiene. Null si no cae en ninguno.
    /// </summary>
    public CsProjectInfo? CsProjectOf(string? relativeFilePath)
    {
        if (string.IsNullOrEmpty(relativeFilePath)) return null;
        var path = relativeFilePath.Replace('\\', '/');

        CsProjectInfo? best = null;
        foreach (var p in CsProjects)
        {
            var dir = p.RelativeDirectory;
            var contains = dir.Length == 0
                || path.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase);
            if (contains && (best is null || dir.Length > best.RelativeDirectory.Length))
                best = p;
        }
        return best;
    }

    /// <summary>
    /// Registra que un <see cref="GraphRepository.PatchNodePropertiesAsync"/> no encontró el nodo
    /// (retorno 0 = QN inexistente en el grafo base).
    /// </summary>
    public void RecordPatchMiss(string enricher, string qualifiedName) =>
        Stats.RecordPatchMiss(enricher, qualifiedName);

    /// <summary>Registra un patch aplicado con éxito (nodo localizado).</summary>
    public void RecordPatchHit(string enricher) => Stats.RecordPatchHit(enricher);
}

/// <summary>
/// Acumula aciertos y fallos de patch por enriquecedor para hacer observables los QN no resueltos.
/// Thread-safe (los enrichers pueden patchear en paralelo).
/// </summary>
public class EnrichmentStats
{
    private readonly object _lock = new();
    private readonly Dictionary<string, EnricherPatchStats> _byEnricher = new(StringComparer.Ordinal);

    public void RecordPatchHit(string enricher)
    {
        lock (_lock)
            GetOrAdd(enricher).Patched++;
    }

    public void RecordPatchMiss(string enricher, string qualifiedName)
    {
        lock (_lock)
        {
            var s = GetOrAdd(enricher);
            s.Missed++;
            if (s.SampleMisses.Count < 5)
                s.SampleMisses.Add(qualifiedName);
        }
    }

    /// <summary>Snapshot inmutable de las stats por enriquecedor.</summary>
    public IReadOnlyDictionary<string, EnricherPatchStats> ByEnricher
    {
        get
        {
            lock (_lock)
                return _byEnricher.ToDictionary(
                    kv => kv.Key,
                    kv => new EnricherPatchStats
                    {
                        Patched      = kv.Value.Patched,
                        Missed       = kv.Value.Missed,
                        SampleMisses = [.. kv.Value.SampleMisses]
                    },
                    StringComparer.Ordinal);
        }
    }

    private EnricherPatchStats GetOrAdd(string enricher)
    {
        if (!_byEnricher.TryGetValue(enricher, out var s))
            _byEnricher[enricher] = s = new EnricherPatchStats();
        return s;
    }
}

public class EnricherPatchStats
{
    public int Patched { get; set; }
    public int Missed  { get; set; }
    public int Attempts => Patched + Missed;
    public List<string> SampleMisses { get; set; } = [];
}
