using Microsoft.CodeAnalysis;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// Aristas <c>CALLS</c> exactas desde las referencias de Roslyn (<see cref="ReferenceIndex"/>), en lugar de las de cbm, que
/// resuelve por nombre (<c>strategy</c> <c>unique_name</c>, <c>suffix_match</c>, <c>field_type_hint</c>…, con
/// <c>confidence</c> 0.75): una por (origen, método llamado), con estas propiedades:
///
///   - <c>exact</c> = true, <c>origin</c> = <c>compiler</c>, <c>callee</c> (el método llamado, con su firma),
///     <c>line</c> (la primera), <c>lines</c>, <c>count</c> y <c>kinds</c>: <c>invocation</c> (<c>Foo()</c>),
///     <c>creation</c> (<c>new X()</c>, al constructor), <c>constructor-initializer</c> (<c>: base(..)</c>,
///     <c>: this(..)</c>) y <c>delegate</c> (el método nombrado como delegado: <c>list.Select(Map)</c>).
///   - <c>dispatch</c>: <c>interface</c> o <c>virtual</c> si el destino es un miembro de interfaz o virtual/abstracto/override.
///     La arista va al símbolo declarado (lo que el compilador resuelve), no a las implementaciones; las une
///     <c>IMPLEMENTS</c>/<c>OVERRIDES</c>.
///   - <c>context</c> si el origen no es un método con nodo: <c>property</c>, <c>field-initializer</c>, <c>event</c>,
///     <c>member</c> (la llamada cuelga del tipo) o <c>top-level</c> (del nodo Module, o File, de un <c>Program.cs</c> sin
///     clase, como cbm).
///
/// Las de cbm que salen de nodos de los ficheros C# analizados se borran antes (<c>cbm_edges_replaced</c>), salvo en los
/// ficheros con símbolos sin resolver (<see cref="EnrichmentContext.FilesWithUnresolvedSymbols"/>), donde se conservan las
/// que no tienen una exacta desde el mismo origen a un método del mismo nombre (<c>cbm_kept_unresolved</c>); las de otros
/// lenguajes no se tocan. No cuentan como llamada los atributos, <c>nameof</c> ni las llamadas a métodos sin nodo (de
/// metadatos, accessors, funciones locales). Una resolución que falla por los tipos de los argumentos (un paquete NuGet sin
/// restaurar) cuenta si el método queda claro a nivel de nodo: un único candidato, o varias sobrecargas que comparten el
/// nodo de cbm (que es por nombre); la arista lleva entonces <c>resolution</c> = <c>candidate</c> si ningún sitio lo resolvió
/// del todo. Varios candidatos en nodos distintos no dan arista.
/// </summary>
public sealed class CallEnricher : IGraphEnricher
{
    public string Name  => "CallEnricher";
    public int    Order => 3;

    private sealed class CallAggregate
    {
        public required string Callee { get; init; }

        /// <summary>Nombre del nodo del método llamado, como lo da cbm (el de la clase para un constructor).</summary>
        public required string TargetName { get; init; }
        public string? Dispatch { get; init; }
        public string? Context { get; init; }
        public SortedSet<int> Lines { get; } = [];
        public SortedSet<string> Kinds { get; } = new(StringComparer.Ordinal);
        public HashSet<(string File, int Line, string Kind)> Sites { get; } = [];

        /// <summary>Algún sitio lo resolvió el compilador del todo (no por ser el único candidato).</summary>
        public bool Resolved { get; set; }
    }

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        var symbols = await ctx.GetGraphSymbolsAsync();
        var filter  = new CbmExclusionFilter(ctx.RepoRoot);
        var fileNodes = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        var calls = new Dictionary<(long Source, long Target), CallAggregate>();
        int withoutSource = 0;

        // Candidatos de una resolución ambigua (sobrecargas con un argumento sin tipo, por un paquete sin restaurar): como cbm
        // da un nodo por nombre, si todos los candidatos caen en el mismo nodo la arista es exacta a ese nivel.
        var ambiguousSites = ctx.References.All
            .Where(r => (r.Flags & ReferenceFlags.Candidate) != 0 && (r.Flags & ReferenceFlags.SingleCandidate) == 0)
            .GroupBy(r => (r.File, r.Position))
            .ToDictionary(g => g.Key, g => g.Select(r => r.TargetSymbol is IMethodSymbol m ? symbols.NodeOfMethod(m) : null).Distinct().Count() == 1
                                           && g.First().TargetSymbol is IMethodSymbol first && symbols.NodeOfMethod(first) is not null);

        foreach (var reference in ctx.References.All)
        {
            if (Kind(reference.Flags) is not { } kind) continue;
            if ((reference.Flags & ReferenceFlags.Candidate) != 0 && (reference.Flags & ReferenceFlags.SingleCandidate) == 0
                && !ambiguousSites.GetValueOrDefault((reference.File, reference.Position)))
                continue;
            if (reference.TargetSymbol is not IMethodSymbol target || symbols.NodeOfMethod(target) is not { } targetId) continue;
            if (filter.ShouldExclude(reference.File)) continue;

            long? sourceId;
            string? context = null;
            if (reference.Container is null)
            {
                // Código de nivel superior: del nodo Module del fichero, como cbm ({proyecto}.{ruta}), o del File si no hay.
                if (!fileNodes.TryGetValue(reference.File, out sourceId))
                {
                    var fileQn = SymbolQn.ForFile(reference.File, ctx.Project, ctx.RepoRoot);
                    var moduleQn = fileQn?[..^".__file__".Length];
                    sourceId = moduleQn is null ? null : await ctx.Repository.ExecuteScalarAsync<long?>(
                        "SELECT id FROM nodes WHERE project = @project AND qualified_name = @qn AND label = 'Module'",
                        new { project = ctx.Project, qn = moduleQn });
                    if (sourceId is null && fileQn is not null) sourceId = await ctx.Repository.GetNodeIdAsync(ctx.Project, fileQn);
                    fileNodes[reference.File] = sourceId;
                }
                context = "top-level";
            }
            else if (reference.Container is IMethodSymbol method && symbols.NodeOfMethod(method) is { } methodId)
                sourceId = methodId;
            else
            {
                sourceId = symbols.NodeOfContainer(reference.Container);
                context = reference.Container switch
                {
                    IPropertySymbol => "property",
                    IFieldSymbol => "field-initializer",
                    IEventSymbol => "event",
                    _ => "member"
                };
            }
            if (sourceId is not { } source)
            {
                withoutSource++;
                continue;
            }

            if (!calls.TryGetValue((source, targetId), out var aggregate))
                calls[(source, targetId)] = aggregate = new CallAggregate
                {
                    Callee = target.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    TargetName = SymbolQn.GraphName(target),
                    Dispatch = Dispatch(target),
                    Context = context
                };
            // Un fichero enlazado en dos proyectos sale en las dos compilaciones: cada sitio cuenta una vez.
            if ((reference.Flags & ReferenceFlags.Candidate) == 0) aggregate.Resolved = true;
            if (aggregate.Sites.Add((reference.File, reference.Line, kind)))
            {
                aggregate.Lines.Add(reference.Line);
                aggregate.Kinds.Add(kind);
            }
        }

        // Las de cbm que salen de los ficheros analizados se sustituyen; antes de borrarlas se cuenta cuántas tienen
        // equivalente exacto (mismo origen y destino) para el registro de la pasada.
        //
        // En un fichero con símbolos sin resolver (un paquete NuGet sin restaurar) una llamada sobre un receptor de tipo
        // desconocido no da referencia, y borrar su arista de cbm la perdería sin ganar la exacta. Ahí solo se sustituye la
        // de cbm si el origen tiene una exacta a un método del mismo nombre (cbm resuelve por nombre: es la misma llamada,
        // p. ej. a la implementación en lugar de a la interfaz); las demás se conservan (cbm_kept_unresolved).
        var unresolvedFiles = ctx.FilesWithUnresolvedSymbols;
        var replaceable = unresolvedFiles.Count == 0 ? ctx.AnalyzedFiles
            : ctx.AnalyzedFiles.Where(f => !unresolvedFiles.Contains(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var replaced = await ctx.Repository.DeleteCbmEdgesFromFilesAsync(ctx.Project, "CALLS", replaceable);
        int kept = 0;
        if (unresolvedFiles.Count > 0)
        {
            var exactNames = calls.Select(c => (c.Key.Source, c.Value.TargetName)).ToHashSet();
            var doomed = new List<long>();
            foreach (var (id, source, target, targetName) in await ctx.Repository.ListCbmEdgesFromFilesAsync(ctx.Project, "CALLS", unresolvedFiles))
            {
                if (calls.ContainsKey((source, target)) || exactNames.Contains((source, targetName)))
                {
                    doomed.Add(id);
                    replaced.Add((source, target));
                }
                else kept++;
            }
            await ctx.Repository.DeleteEdgesAsync(doomed);
        }
        var withEquivalent = replaced.Count(pair => calls.ContainsKey(pair));

        var enrichedAt = DateTime.UtcNow.ToString("O");
        foreach (var ((source, target), call) in calls.OrderBy(c => c.Key.Source).ThenBy(c => c.Key.Target))
        {
            var properties = new Dictionary<string, object?>
            {
                ["exact"]       = true,
                ["origin"]      = "compiler",
                ["callee"]      = call.Callee,
                ["line"]        = call.Lines.Min,
                ["lines"]       = call.Lines.ToArray(),
                ["count"]       = call.Sites.Count,
                ["kinds"]       = call.Kinds.ToArray(),
                ["enriched_by"] = new[] { Name },
                ["enriched_at"] = enrichedAt
            };
            if (call.Dispatch is not null) properties["dispatch"] = call.Dispatch;
            if (call.Context is not null)  properties["context"]  = call.Context;
            if (!call.Resolved)            properties["resolution"] = "candidate";
            await ctx.Repository.UpsertEdgeAsync(ctx.Project, source, target, "CALLS", properties);
        }

        ctx.CbmEdgesReplaced["CALLS"] = ctx.CbmEdgesReplaced.GetValueOrDefault("CALLS") + replaced.Count;
        ctx.EnricherDetails["calls"] = new
        {
            exact = calls.Count,
            cbm_replaced = replaced.Count,
            cbm_with_equivalent = withEquivalent,
            cbm_without_equivalent = replaced.Count - withEquivalent,
            cbm_kept_unresolved = kept,
            files_with_unresolved_symbols = unresolvedFiles.Count,
            references_without_source = withoutSource
        };
        progress?.Report($"Calls: {calls.Count} exact CALLS edges; {replaced.Count} cbm CALLS replaced ({withEquivalent} with an exact equivalent)"
            + (unresolvedFiles.Count > 0 ? $"; {kept} cbm CALLS kept in {unresolvedFiles.Count} file(s) with unresolved symbols." : "."));
    }

    /// <summary>Qué clase de llamada es la referencia; null si no es una llamada (atributo, nameof, candidato, tipo, campo…).</summary>
    private static string? Kind(ReferenceFlags flags)
    {
        if ((flags & (ReferenceFlags.Attribute | ReferenceFlags.NameOf)) != 0) return null;
        if ((flags & ReferenceFlags.Invocation) != 0) return "invocation";
        if ((flags & ReferenceFlags.Creation) != 0) return "creation";
        if ((flags & ReferenceFlags.ConstructorInitializer) != 0) return "constructor-initializer";
        // Un nombre de método que no se invoca: grupo de métodos convertido en delegado.
        return "delegate";
    }

    private static string? Dispatch(IMethodSymbol target) =>
        target.ContainingType?.TypeKind == TypeKind.Interface ? "interface"
        : target.IsVirtual || target.IsAbstract || target.IsOverride ? "virtual"
        : null;
}
