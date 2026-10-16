using Microsoft.CodeAnalysis;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// Análisis de alcanzabilidad (dead code) por BFS sobre el grafo. Order 15: corre tras Route(4)/Flow(11)/DI(3) para que
/// existan las raíces (HANDLES/FLOW_ENTRY/DI_RESOLVES).
///
/// Las aristas entre métodos y tipos C# salen de las <b>referencias exactas de Roslyn</b> (<see cref="ReferenceIndex"/>) y no
/// de las aristas CALLS/USAGE de cbm, que resuelve por nombre. Las aristas de cbm solo se usan desde nodos que Roslyn no
/// resolvió (funciones de otros lenguajes, métodos cuyo qualified_name no casa).
///
/// Raíces vivas: métodos <c>is_entry_point</c>/<c>is_test</c>, handlers HTTP (source de HANDLES), entradas de flujo
/// (target de FLOW_ENTRY), lo que nombra el código de nivel superior (<c>Program.cs</c>) o un miembro sin nodo, los
/// servicios que consume el propio framework (registro de un servicio externo, <c>AddHostedService</c>), más los nodos que
/// aporten los <see cref="IDeadCodeSuppressor"/> (p.ej. plugins cargados por reflexión). Un servicio registrado en DI que
/// nadie consume (<c>consumer_count = 0</c>) <b>no</b> es raíz: antes cualquier DI_RESOLVES lo mantenía vivo.
///
/// Propagación: una referencia de un miembro a otro (llamador→llamado, usuario→usado), la clase a sus miembros no
/// privados (DEFINES_METHOD), el miembro a su clase e IMPLEMENTS <b>inverso</b> (interfaz viva → sus implementaciones). Un
/// método <b>privado</b> (ordinario, sin atributos, que no sea explícito de interfaz, manejador de eventos ni <c>Main</c>)
/// solo está vivo si lo referencia código vivo: un método privado sin referencias dentro de una clase viva es candidato
/// de confianza alta. El resto se sigue decidiendo a nivel de <b>componente</b> (clase + miembros): una clase es muerta
/// solo si nada vivo la nombra, y sus miembros públicos con ella.
///
/// Marcas (Method/Function/Class): <c>is_reachable</c>; si no alcanzable, <c>dead_code_candidate</c> +
/// <c>dead_code_confidence</c> (high/medium/low) + <c>dead_code_reason</c>. Además <c>unused_registration</c>
/// (DiRegistration cuyo servicio no consume nadie, con el <c>consumer_count</c> de <see cref="DiEnricher"/>) y
/// <c>unused_config</c> (ConfigClass sin uso). Todo por SQL directo (las props se leen/escriben con json_extract; nunca por Cypher).
/// </summary>
public class ReachabilityEnricher : IGraphEnricher
{
    private readonly IReadOnlyList<IDeadCodeSuppressor> _suppressors;

    public ReachabilityEnricher(IEnumerable<IDeadCodeSuppressor> suppressors)
        => _suppressors = suppressors.ToList();

    public string Name  => "ReachabilityEnricher";
    public int    Order => 15;

    private sealed class NodeInfo
    {
        public required long   Id;
        public required string Label;
        public string? Accessibility;
        public bool    Polymorphic;   // virtual/override/abstract → invocable por contrato
        public string? Language;      // 'typescript' → tratamiento de dead code más conservador (F8)
        public bool    IsExported;    // TS: export explícito → probable API/entry aunque parezca inalcanzable
        public bool    PrivateCandidate; // método privado que solo vive si lo referencia código vivo
    }

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        var graph   = ctx.Repository;
        var project = ctx.Project;

        var db      = graph;

        // ── 0. Reset de marcas propias (idempotencia en re-ejecuciones sin reindex) ──────────
        // El pipeline puede re-correrse sobre la misma DB; como el patch es merge-only, hay que
        // limpiar las marcas de la corrida anterior antes de recomputar, o quedarían flags
        // contradictorios (p.ej. is_reachable=true junto a dead_code_candidate=true) al cambiar el estado.
        await db.ExecuteAsync($"""
            UPDATE nodes SET properties = json_remove(properties,
                '$.is_reachable','$.dead_code_candidate','$.dead_code_confidence',
                '$.dead_code_reason','$.dead_code_suppressed_by')
            WHERE project=@project AND label IN ('Method','Function','Class') AND {LanguageScope.NotJava()}
            """, new { project });
        await db.ExecuteAsync(
            "UPDATE nodes SET properties = json_remove(properties,'$.unused_registration') WHERE project=@project AND label='DiRegistration'",
            new { project });
        await db.ExecuteAsync(
            "UPDATE nodes SET properties = json_remove(properties,'$.unused_config') WHERE project=@project AND label='ConfigClass'",
            new { project });

        // ── 1. Nodos clasificables (Method/Function/Class) + Interface (solo para propagar) ──
        var nodes = new Dictionary<long, NodeInfo>();
        foreach (var r in await db.QueryAsync($"""
            SELECT id, label,
                   json_extract(properties,'$.accessibility') AS acc,
                   json_extract(properties,'$.is_virtual')    AS v,
                   json_extract(properties,'$.is_override')   AS o,
                   json_extract(properties,'$.is_abstract')   AS ab,
                   json_extract(properties,'$.is_explicit_interface_impl') AS ex,
                   json_extract(properties,'$.language')       AS lang,
                   json_extract(properties,'$.is_exported')    AS exp
            FROM nodes
            WHERE project = @project AND label IN ('Method','Function','Class','Interface') AND {LanguageScope.NotJava()}
            """, new { project }))
        {
            long id = (long)r.id;
            nodes[id] = new NodeInfo
            {
                Id            = id,
                Label         = (string)r.label,
                Accessibility = r.acc as string,
                Polymorphic   = ToBool(r.v) || ToBool(r.o) || ToBool(r.ab) || ToBool(r.ex),
                Language      = r.lang as string,
                IsExported    = ToBool(r.exp)
            };
        }

        // ── 2. Referencias exactas de Roslyn ─────────────────────────────────────────────────
        var symbols = await ctx.GetGraphSymbolsAsync();
        var index   = ctx.References;
        var mapped  = new HashSet<long>(symbols.MethodsByNode.Keys);
        mapped.UnionWith(symbols.TypesByNode.Keys);

        foreach (var (id, methods) in symbols.MethodsByNode)
            if (nodes.TryGetValue(id, out var info))
                info.PrivateCandidate = methods.All(IsPrivateCandidate);

        // ── 3. Adyacencia de propagación (source → target ya orientado hacia "lo alcanzado") ──
        var adj = new Dictionary<long, List<long>>();
        void AddEdge(long s, long t)
        {
            if (!adj.TryGetValue(s, out var l)) adj[s] = l = new List<long>();
            l.Add(t);
        }

        var roots = new HashSet<long>();
        int exactEdges = 0, rootRefs = 0;
        foreach (var r in index.All)
        {
            // Un registro de DI no usa el servicio: lo consumen los que lo piden, y eso ya es otra referencia.
            if ((r.Flags & ReferenceFlags.InDiRegistration) != 0) continue;
            if (symbols.NodeOfTarget(r.TargetSymbol) is not { } target || !nodes.ContainsKey(target)) continue;

            if (symbols.NodeOfContainer(r.Container) is { } source && nodes.ContainsKey(source))
            {
                AddEdge(source, target);
                exactEdges++;
            }
            else
            {
                // Código de nivel superior, atributos de ensamblado o un miembro sin nodo: vive por sí mismo.
                roots.Add(target);
                rootRefs++;
            }
        }

        // CALLS/USAGE de cbm, solo desde nodos que Roslyn no resolvió.
        foreach (var e in await db.QueryAsync(
            "SELECT source_id s, target_id t FROM edges WHERE project=@project AND type IN ('CALLS','USAGE')",
            new { project }))
            if (!mapped.Contains((long)e.s))
                AddEdge((long)e.s, (long)e.t);

        // DEFINES_METHOD: método vivo ⇒ clase viva, y clase viva ⇒ sus miembros no privados. Los métodos privados solo
        // viven si los referencia código vivo (ver la cabecera): antes una clase viva los daba todos por vivos.
        var owner = new Dictionary<long, long>();
        foreach (var e in await db.QueryAsync(
            "SELECT source_id s, target_id t FROM edges WHERE project=@project AND type='DEFINES_METHOD'",
            new { project }))
        {
            long type = (long)e.s, method = (long)e.t;
            owner[method] = type;
            AddEdge(method, type);
            if (!(nodes.TryGetValue(method, out var m) && m.PrivateCandidate))
                AddEdge(type, method);
        }

        // IMPLEMENTS inverso: interfaz → implementación
        foreach (var e in await db.QueryAsync(
            "SELECT target_id s, source_id t FROM edges WHERE project=@project AND type='IMPLEMENTS'",
            new { project }))
            AddEdge((long)e.s, (long)e.t);

        // ── 4. Raíces vivas ──────────────────────────────────────────────────────────────────
        async Task AddRoots(string sql) =>
            roots.UnionWith((await db.QueryAsync(sql, new { project }))
                .Select(x => (long)x.id).Where(nodes.ContainsKey));

        await AddRoots("""
            SELECT id FROM nodes WHERE project=@project AND label IN ('Method','Function')
              AND (json_extract(properties,'$.is_entry_point')=1 OR json_extract(properties,'$.is_test')=1)
            """);
        await AddRoots("SELECT DISTINCT source_id AS id FROM edges WHERE project=@project AND type='HANDLES'");
        await AddRoots("SELECT DISTINCT target_id AS id FROM edges WHERE project=@project AND type='FLOW_ENTRY'");
        // Un registro con consumer_count solo mantiene vivo lo que resuelve si alguien lo consume (y entonces la referencia
        // del consumidor ya lo alcanza). Sin consumer_count el servicio es externo (lo resuelve el framework: IHostedService,
        // validadores, handlers…) o lo registró una versión anterior: sigue siendo raíz.
        await AddRoots("""
            SELECT DISTINCT e.target_id AS id FROM edges e
            JOIN nodes r ON r.id = e.source_id
            WHERE e.project=@project AND e.type='DI_RESOLVES'
              AND json_extract(r.properties,'$.consumer_count') IS NULL
            """);

        var coreRoots = new HashSet<long>(roots);

        // Supresores (reflexión/convención) → raíces vivas adicionales. Solo se atribuye
        // dead_code_suppressed_by a nodos que NO eran ya raíz core (para no falsear la atribución).
        var suppressedBy = new Dictionary<long, string>();
        foreach (var sup in _suppressors)
        {
            var ids = await sup.GetLiveRootIdsAsync(ctx);
            foreach (var id in ids.Where(nodes.ContainsKey))
            {
                roots.Add(id);
                if (!coreRoots.Contains(id) && !suppressedBy.ContainsKey(id))
                    suppressedBy[id] = sup.Name;
            }
        }

        // ── 5. BFS ───────────────────────────────────────────────────────────────────────────
        var reachable = new HashSet<long>(roots);
        var queue     = new Queue<long>(roots);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!adj.TryGetValue(cur, out var next)) continue;
            foreach (var t in next)
                if (reachable.Add(t))
                    queue.Enqueue(t);
        }

        // ── 6. Marcado por nodo (solo Method/Function/Class) ──────────────────────────────────
        int deadHigh = 0, deadMed = 0, deadLow = 0, reachCount = 0;

        foreach (var n in nodes.Values)
        {
            if (n.Label == "Interface") continue;  // contratos: no son candidatos a dead code

            bool isReachable = reachable.Contains(n.Id);
            if (isReachable)
            {
                var patch = new Dictionary<string, object?> { ["is_reachable"] = true };
                if (suppressedBy.TryGetValue(n.Id, out var by)) patch["dead_code_suppressed_by"] = by;
                await graph.PatchNodePropertiesByIdAsync(n.Id, patch);
                reachCount++;
            }
            else if (n.Language == "typescript" && n.IsExported)
            {
                // F8: el grafo CALLS/IMPORTS de TS que extrae cbm es muy incompleto. Un símbolo TS
                // *exportado* que parece inalcanzable casi siempre es un falso positivo (se usa vía
                // framework/JSX/import dinámico sin arista). No lo marcamos como dead code.
                await graph.PatchNodePropertiesByIdAsync(n.Id, new Dictionary<string, object?> { ["is_reachable"] = false });
            }
            else if (n.Label == "Method" && owner.TryGetValue(n.Id, out var declaringType) && nodes.TryGetValue(declaringType, out var declaring) && declaring.Label == "Interface")
            {
                // Un método de interfaz es un contrato, como la interfaz (arriba): no es candidato a dead code.
                await graph.PatchNodePropertiesByIdAsync(n.Id, new Dictionary<string, object?> { ["is_reachable"] = false });
            }
            else
            {
                // F8: para TS solo emitimos dead code de alta confianza (símbolo no exportado y sin
                // referencias entrantes); el resto es demasiado ruidoso por el grafo TS incompleto.
                var (confidence, reason) = n.Language == "typescript"
                    ? ("high", "símbolo TypeScript no exportado y sin referencias entrantes (dead code TS solo se marca en alta confianza)")
                    : Classify(n, owner.TryGetValue(n.Id, out var ownerId) && reachable.Contains(ownerId));
                await graph.PatchNodePropertiesByIdAsync(n.Id, new Dictionary<string, object?>
                {
                    ["is_reachable"]         = false,
                    ["dead_code_candidate"]  = true,
                    ["dead_code_confidence"] = confidence,
                    ["dead_code_reason"]     = reason
                });
                switch (confidence) { case "high": deadHigh++; break; case "medium": deadMed++; break; default: deadLow++; break; }
            }
        }

        progress?.Report(
            $"Reachability: {reachCount} reachable, {deadHigh + deadMed + deadLow} dead-code candidates " +
            $"(high={deadHigh}, medium={deadMed}, low={deadLow}); roots={coreRoots.Count} core + {suppressedBy.Count} suppressed; " +
            $"{exactEdges} exact Roslyn edge(s), {rootRefs} root reference(s).");

        // ── 7. DI registrations sin inyectar / config sin uso ─────────────────────────────────
        // consumer_count lo calcula DiEnricher con las referencias de Roslyn al tipo del servicio, sin contar la propia llamada
        // de registro ni la lista de bases de la implementación. Sin el dato (servicio externo, hosted service) no se marca.
        int unusedReg = await db.ExecuteAsync("""
            UPDATE nodes
            SET properties = json_set(properties, '$.unused_registration', json('true'))
            WHERE project = @project AND label = 'DiRegistration'
              AND json_extract(properties, '$.consumer_count') = 0
            """, new { project });

        int unusedCfg = await db.ExecuteAsync("""
            UPDATE nodes
            SET properties = json_set(properties, '$.unused_config', json('true'))
            WHERE project = @project AND label = 'ConfigClass'
              AND NOT EXISTS (
                  SELECT 1 FROM edges e WHERE e.target_id = nodes.id
                    AND e.type IN ('USAGE','OPTIONS_CONFIGURES','CONFIGURES'))
            """, new { project });

        progress?.Report($"Reachability: {unusedReg} unused DI registration(s), {unusedCfg} unused config class(es).");
    }

    private static (string confidence, string reason) Classify(NodeInfo n, bool ownerReachable)
    {
        if (n.Label == "Class")
        {
            return IsNonPublic(n.Accessibility)
                ? ("high",   $"clase {n.Accessibility} sin conexión a ningún punto de entrada (ni usos, ni DI, ni flujos)")
                : ("medium", "clase sin conexión a puntos de entrada (posible API pública o instanciación por reflexión)");
        }

        // Method / Function
        if (n.PrivateCandidate && ownerReachable)
            return ("high", "método privado sin ninguna referencia desde código vivo de la solución (referencias exactas de Roslyn; solo la reflexión podría llamarlo)");

        if (n.Polymorphic)
            return ("low", "miembro polimórfico (virtual/override/abstract): puede invocarse por contrato base o interfaz sin arista directa");

        return IsNonPublic(n.Accessibility)
            ? ("high",   $"método {n.Accessibility ?? "no-público"} en un componente sin conexión a puntos de entrada")
            : ("medium", "método público sin conexión a puntos de entrada: posible API externa o invocación por reflexión");
    }

    /// <summary>
    /// Un método privado que puede darse por muerto sin más: ordinario, sin atributos (pueden registrarlo por reflexión
    /// o por convención), que no implemente explícitamente una interfaz, no sea un manejador de eventos (los engancha
    /// el diseñador o el marcado) ni el <c>Main</c>.
    /// </summary>
    private static bool IsPrivateCandidate(IMethodSymbol method) =>
        method.MethodKind == MethodKind.Ordinary
        && method.DeclaredAccessibility == Accessibility.Private
        && !method.IsOverride && !method.IsExtern && !method.IsPartialDefinition
        && method.PartialImplementationPart is null
        && method.ExplicitInterfaceImplementations.Length == 0
        && method.GetAttributes().Length == 0
        && method.Name != "Main"
        && !IsEventHandlerShaped(method);

    private static bool IsEventHandlerShaped(IMethodSymbol method) =>
        method.Parameters.Length == 2
        && method.Parameters[0].Type.SpecialType == SpecialType.System_Object
        && method.Parameters[1].Type.Name.EndsWith("EventArgs", StringComparison.Ordinal);

    private static bool IsNonPublic(string? accessibility) =>
        accessibility is "private" or "internal" or "protected" or "protected internal" or "private protected";

    private static bool ToBool(object? v) => v switch
    {
        null      => false,
        long l    => l != 0,
        int i     => i != 0,
        bool b    => b,
        string s  => s is "1" or "true" or "True",
        _         => false
    };
}
