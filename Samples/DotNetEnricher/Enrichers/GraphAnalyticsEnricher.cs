namespace DotNetEnricher.Enrichers;

/// <summary>
/// Analítica estructural (Fase 4), sin Roslyn — solo SQL sobre el grafo. Order 16: tras Project(8)
/// (necesita CsProject + PROJECT_DEPENDS_ON) y Reachability(15).
///
/// Produce:
///   1. Acoplamiento por <b>clase</b>: <c>afferent_coupling</c>/<c>efferent_coupling</c>/<c>instability</c>
///      (mismo criterio que ProjectEnricher pero agregando a nivel Class/Interface — el owner de cada
///      método se resuelve por DEFINES_METHOD).
///   2. <b>Ciclos</b> (Tarjan SCC) sobre el grafo clase↔clase y sobre PROJECT_DEPENDS_ON → marcas
///      <c>in_cycle</c>/<c>cycle_group</c>/<c>cycle_size</c>/<c>cycle_members</c> en los miembros.
///   3. <b>Violaciones de capas</b> sobre PROJECT_DEPENDS_ON usando el <c>role</c> de CsProject: una
///      dependencia de una capa base hacia una capa superior (core→host/adapter/frontend, etc.) se marca
///      con <c>layer_violation</c>=true + motivo en la arista.
///   4. <b>Comunidades</b> funcionales: Louvain (local-moving determinista) sobre un grafo clase↔clase
///      ponderado (IMPLEMENTS/INHERITS=4, CALLS=3, USAGE=2, same-folder+1); se prefiere a label
///      propagation porque en grafos densos éste colapsa en una comunidad gigante. Marcas <c>community_id</c>
///      + <c>community_name</c> (TF-IDF de los nombres de los miembros, vía NameTokenizer + IDFs de cbm).
///
/// Nota de diseño: los ciclos se materializan como <b>propiedades de los nodos miembro</b> (no como nodos
/// <c>Cycle</c> separados) para preservar la idempotencia — crear nodos nuevos no es limpiable de forma
/// sencilla (FTS5 contentless), mientras que las props se resetean al inicio como en ReachabilityEnricher.
/// </summary>
public class GraphAnalyticsEnricher : IGraphEnricher
{
    public string Name  => "GraphAnalyticsEnricher";
    public int    Order => 16;

    // Jerarquía de capas: una dependencia es violación si va de una capa base a una superior.
    private static readonly Dictionary<string, int> LayerLevel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["core"] = 0, ["utility"] = 0, ["adapter"] = 1, ["host"] = 2, ["frontend"] = 3
    };

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        var graph   = ctx.Repository;
        var project = ctx.Project;

        var db      = graph;

        // ── 0. Reset de marcas propias (idempotencia) ─────────────────────────────────────────
        await db.ExecuteAsync("""
            UPDATE nodes SET properties = json_remove(properties,
                '$.afferent_coupling','$.efferent_coupling','$.instability',
                '$.in_cycle','$.cycle_group','$.cycle_size','$.cycle_members',
                '$.community_id','$.community_name')
            WHERE project=@project AND label IN ('Class','Interface')
            """, new { project });
        await db.ExecuteAsync("""
            UPDATE nodes SET properties = json_remove(properties,
                '$.in_cycle','$.cycle_group','$.cycle_size','$.cycle_members','$.layer_violations')
            WHERE project=@project AND label='CsProject'
            """, new { project });
        await db.ExecuteAsync(
            "UPDATE edges SET properties = json_remove(properties,'$.layer_violation','$.layer_violation_reason') WHERE project=@project AND type='PROJECT_DEPENDS_ON'",
            new { project });

        // ── 1. Grafo clase↔clase ───────────────────────────────────────────────────────────────
        // Nodos "class-like" (Class/Interface) y mapa método→clase-owner (DEFINES_METHOD).
        var classLike = new HashSet<long>((await db.QueryAsync<long>(
            "SELECT id FROM nodes WHERE project=@project AND label IN ('Class','Interface')", new { project })));

        var ownerOf = new Dictionary<long, long>();
        foreach (var e in await db.QueryAsync(
            "SELECT source_id owner, target_id member FROM edges WHERE project=@project AND type='DEFINES_METHOD'",
            new { project }))
            ownerOf[(long)e.member] = (long)e.owner;

        // El owner debe ser también class-like: DEFINES_METHOD también cubre struct/record (label 'Type'),
        // que no entran en el análisis de acoplamiento de clase ni en el reset (paso 0) → filtrarlos aquí
        // evita que se cuelen en el grafo de ciclos y queden con marcas in_cycle sin limpiar (idempotencia).
        long? ResolveClass(long nodeId) =>
            classLike.Contains(nodeId) ? nodeId :
            ownerOf.TryGetValue(nodeId, out var c) && classLike.Contains(c) ? c : null;

        // Dependencias clase→clase (deduplicadas) a partir de aristas estructurales.
        var deps    = new Dictionary<long, HashSet<long>>();   // Ce: from → {to}
        var revDeps = new Dictionary<long, HashSet<long>>();   // Ca: to → {from}
        foreach (var e in await db.QueryAsync(
            "SELECT source_id s, target_id t FROM edges WHERE project=@project AND type IN ('CALLS','USAGE','IMPLEMENTS','INHERITS')",
            new { project }))
        {
            var cs = ResolveClass((long)e.s);
            var ct = ResolveClass((long)e.t);
            if (cs is null || ct is null || cs == ct) continue;

            (deps.TryGetValue(cs.Value, out var ds) ? ds : deps[cs.Value] = new()).Add(ct.Value);
            (revDeps.TryGetValue(ct.Value, out var rs) ? rs : revDeps[ct.Value] = new()).Add(cs.Value);
        }

        // ── 2. Ca/Ce/I por clase ───────────────────────────────────────────────────────────────
        int coupledClasses = 0;
        foreach (var id in classLike)
        {
            int ce = deps.TryGetValue(id, out var ds) ? ds.Count : 0;
            int ca = revDeps.TryGetValue(id, out var rs) ? rs.Count : 0;
            double instability = (ca + ce) == 0 ? 0.0 : Math.Round((double)ce / (ca + ce), 2);

            await graph.PatchNodePropertiesByIdAsync(id, new Dictionary<string, object?>
            {
                ["afferent_coupling"] = ca,
                ["efferent_coupling"] = ce,
                ["instability"]       = instability
            });
            if (ca + ce > 0) coupledClasses++;
        }
        progress?.Report($"Analytics: coupling (Ca/Ce/I) on {classLike.Count} classes/interfaces ({coupledClasses} coupled).");

        // ── 3. Ciclos clase↔clase (Tarjan SCC) ──────────────────────────────────────────────────
        var classNames = (await db.QueryAsync(
            "SELECT id, name FROM nodes WHERE project=@project AND label IN ('Class','Interface')", new { project }))
            .ToDictionary(r => (long)r.id, r => (string)r.name);

        var classDepsList = deps.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        int classCycleMembers = await MarkCyclesAsync(graph, classDepsList, classNames.Keys, classNames);
        progress?.Report($"Analytics: {classCycleMembers} classes in dependency cycles.");

        // ── 4. Ciclos de proyecto (Tarjan sobre PROJECT_DEPENDS_ON) ──────────────────────────────
        var projDeps  = new Dictionary<long, HashSet<long>>();
        foreach (var e in await db.QueryAsync(
            "SELECT source_id s, target_id t FROM edges WHERE project=@project AND type='PROJECT_DEPENDS_ON'",
            new { project }))
            (projDeps.TryGetValue((long)e.s, out var ds) ? ds : projDeps[(long)e.s] = new()).Add((long)e.t);

        var projNames = (await db.QueryAsync(
            "SELECT id, name FROM nodes WHERE project=@project AND label='CsProject'", new { project }))
            .ToDictionary(r => (long)r.id, r => (string)r.name);

        var projDepsList = projDeps.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        int projCycleMembers = await MarkCyclesAsync(graph, projDepsList, projNames.Keys, projNames);
        progress?.Report($"Analytics: {projCycleMembers} projects in dependency cycles.");

        // ── 5. Violaciones de capas sobre PROJECT_DEPENDS_ON ─────────────────────────────────────
        var roleById = (await db.QueryAsync(
            "SELECT id, json_extract(properties,'$.role') role FROM nodes WHERE project=@project AND label='CsProject'",
            new { project }))
            .Where(r => r.role is not null)
            .ToDictionary(r => (long)r.id, r => (string)r.role);

        var violationsByProj = new Dictionary<long, int>();
        int layerViolations = 0;
        foreach (var e in await db.QueryAsync(
            "SELECT id, source_id s, target_id t FROM edges WHERE project=@project AND type='PROJECT_DEPENDS_ON'",
            new { project }))
        {
            long edgeId = (long)e.id, from = (long)e.s, to = (long)e.t;
            if (!roleById.TryGetValue(from, out var rf) || !roleById.TryGetValue(to, out var rt)) continue;
            if (!LayerLevel.TryGetValue(rf, out var lf) || !LayerLevel.TryGetValue(rt, out var lt)) continue;

            if (lf < lt)  // capa base depende de capa superior → violación
            {
                var reason = $"{rf} (nivel {lf}) depende de {rt} (nivel {lt})";
                await db.ExecuteAsync(
                    "UPDATE edges SET properties = json_set(json_set(properties,'$.layer_violation',json('true')),'$.layer_violation_reason',@reason) WHERE id=@edgeId",
                    new { edgeId, reason });
                violationsByProj[from] = violationsByProj.GetValueOrDefault(from) + 1;
                layerViolations++;
            }
        }

        foreach (var (projId, count) in violationsByProj)
            await graph.PatchNodePropertiesByIdAsync(projId, new Dictionary<string, object?> { ["layer_violations"] = count });

        progress?.Report($"Analytics: {layerViolations} layer violation(s) on PROJECT_DEPENDS_ON.");

        // ── 6. Comunidades funcionales (label propagation determinista) ──────────────────────────
        // Grafo ponderado NO dirigido entre clases: IMPLEMENTS/INHERITS=4, CALLS=3, USAGE=2, same-folder+1.
        var weight = new Dictionary<long, Dictionary<long, double>>();
        void AddW(long a, long b, double w)
        {
            if (a == b) return;
            if (!weight.TryGetValue(a, out var ma)) weight[a] = ma = new();
            if (!weight.TryGetValue(b, out var mb)) weight[b] = mb = new();
            ma[b] = ma.GetValueOrDefault(b) + w;
            mb[a] = mb.GetValueOrDefault(a) + w;
        }

        foreach (var e in await db.QueryAsync(
            "SELECT source_id s, target_id t, type FROM edges WHERE project=@project AND type IN ('CALLS','USAGE','IMPLEMENTS','INHERITS')",
            new { project }))
        {
            var cs = ResolveClass((long)e.s);
            var ct = ResolveClass((long)e.t);
            if (cs is null || ct is null || cs == ct) continue;
            double w = (string)e.type switch { "IMPLEMENTS" or "INHERITS" => 4, "CALLS" => 3, _ => 2 };
            AddW(cs.Value, ct.Value, w);
        }

        // same-folder +1 (boost sobre pares ya conectados)
        var folderOf = (await db.QueryAsync(
            "SELECT id, file_path FROM nodes WHERE project=@project AND label IN ('Class','Interface')", new { project }))
            .ToDictionary(r => (long)r.id, r => Folder(r.file_path as string));
        foreach (var (a, nbrs) in weight)
            foreach (var b in nbrs.Keys.ToList())
                if (a < b && folderOf.GetValueOrDefault(a) is { Length: > 0 } fa && fa == folderOf.GetValueOrDefault(b))
                {
                    weight[a][b] += 1; weight[b][a] += 1;
                }

        // Louvain (una fase de local-moving, determinista) — maximiza modularidad. Se prefiere a label
        // propagation porque en grafos densos (mucho acoplamiento) éste colapsa en una
        // "comunidad monstruo"; la modularidad penaliza esa fusión. Orden por id, empate por comunidad menor.
        var labels = LouvainLocalMoving(weight, classLike);

        // community_id secuencial por tamaño desc (estable), naming por TF-IDF.
        var groups = labels.GroupBy(kv => kv.Value)
                           .Select(g => g.Select(kv => kv.Key).ToList())
                           .OrderByDescending(g => g.Count).ThenBy(g => g.Min())
                           .ToList();

        var idfs = await graph.LoadTokenIdfsAsync(project);
        int cid = 0, named = 0;
        foreach (var members in groups)
        {
            cid++;
            string? name = members.Count >= 2 ? NameCommunity(members, classNames, idfs) : null;
            if (name is not null) named++;
            foreach (var id in members)
            {
                var patch = new Dictionary<string, object?> { ["community_id"] = cid };
                if (name is not null) patch["community_name"] = name;
                await graph.PatchNodePropertiesByIdAsync(id, patch);
            }
        }

        int multiMember = groups.Count(g => g.Count > 1);
        progress?.Report(
            $"Analytics: {groups.Count} communities ({multiMember} multi-clase, {named} nombradas); " +
            $"largest={(groups.Count > 0 ? groups[0].Count : 0)} clases.");
    }

    /// <summary>
    /// Louvain — una fase de local-moving determinista maximizando modularidad sobre un grafo no dirigido
    /// ponderado. Devuelve id-de-nodo → id-de-comunidad. Determinista: nodos en orden ascendente, comunidades
    /// candidatas en orden ascendente, y un movimiento requiere ganancia &gt; eps (evita oscilar por ruido).
    /// </summary>
    private static Dictionary<long, long> LouvainLocalMoving(
        Dictionary<long, Dictionary<long, double>> weight, HashSet<long> allNodes)
    {
        var k = new Dictionary<long, double>();
        double twoM = 0;
        foreach (var id in allNodes)
        {
            double deg = weight.TryGetValue(id, out var nb) ? nb.Values.Sum() : 0;
            k[id] = deg;
            twoM += deg;
        }

        var comm = allNodes.ToDictionary(id => id, id => id);
        if (twoM <= 0) return comm;   // sin aristas → cada nodo su propia comunidad

        var sigmaTot = new Dictionary<long, double>();
        foreach (var id in allNodes)
            sigmaTot[comm[id]] = sigmaTot.GetValueOrDefault(comm[id]) + k[id];

        var ordered = allNodes.OrderBy(x => x).ToList();
        const double eps = 1e-12;

        for (int iter = 0; iter < 50; iter++)
        {
            bool moved = false;
            foreach (var i in ordered)
            {
                if (!weight.TryGetValue(i, out var nbrs) || nbrs.Count == 0) continue;

                long ci = comm[i];
                var kin = new Dictionary<long, double>();   // peso de i hacia cada comunidad vecina
                foreach (var (j, w) in nbrs)
                    if (j != i) { var cj = comm[j]; kin[cj] = kin.GetValueOrDefault(cj) + w; }

                sigmaTot[ci] -= k[i];   // quitar i de su comunidad

                // ΔQ (proporcional) de mover i a c: kin[c] - Σtot[c]·k_i/(2m)
                long best = ci;
                double bestGain = kin.GetValueOrDefault(ci) - sigmaTot.GetValueOrDefault(ci) * k[i] / twoM;
                foreach (var c in kin.Keys.OrderBy(x => x))
                {
                    double gain = kin[c] - sigmaTot.GetValueOrDefault(c) * k[i] / twoM;
                    if (gain > bestGain + eps) { bestGain = gain; best = c; }
                }

                sigmaTot[best] = sigmaTot.GetValueOrDefault(best) + k[i];
                if (best != ci) { comm[i] = best; moved = true; }
            }
            if (!moved) break;
        }

        return comm;
    }

    /// <summary>Nombre de comunidad por TF-IDF de los tokens de los nombres de sus miembros (top-2).</summary>
    private static string? NameCommunity(
        List<long> members, IReadOnlyDictionary<long, string> names, IReadOnlyDictionary<string, float> idfs)
    {
        var freq = new Dictionary<string, int>();
        foreach (var id in members)
            if (names.TryGetValue(id, out var nm))
                foreach (var tok in NameTokenizer.Tokenize(nm))
                    if (tok.Length >= 3)
                        freq[tok] = freq.GetValueOrDefault(tok) + 1;

        if (freq.Count == 0) return null;

        var score = new Dictionary<string, double>();
        foreach (var (tok, f) in freq)
        {
            double idf = idfs.TryGetValue(tok, out var v) ? Math.Log(1 + v / 1000f) : 1.0;
            score[tok] = f * idf;
        }

        var top = score.OrderByDescending(k => k.Value).ThenBy(k => k.Key).Take(2).Select(k => k.Key).ToList();
        return top.Count > 0 ? string.Join("-", top) : null;
    }

    private static string Folder(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return "";
        var norm = filePath.Replace('\\', '/');
        var i = norm.LastIndexOf('/');
        return i < 0 ? "" : norm[..i];
    }

    /// <summary>
    /// Marca los miembros de cada SCC de tamaño &gt;1 (ciclo) con in_cycle/cycle_group/cycle_size/cycle_members.
    /// Devuelve el número total de nodos marcados.
    /// </summary>
    private static async Task<int> MarkCyclesAsync(
        GraphRepository graph, Dictionary<long, List<long>> graphAdj,
        IEnumerable<long> allNodes, IReadOnlyDictionary<long, string> names)
    {
        var sccs = Tarjan(graphAdj, allNodes);
        int marked = 0, group = 0;
        foreach (var scc in sccs.Where(s => s.Count > 1))
        {
            group++;
            var members = scc.Select(id => names.TryGetValue(id, out var n) ? n : id.ToString())
                             .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            foreach (var id in scc)
            {
                await graph.PatchNodePropertiesByIdAsync(id, new Dictionary<string, object?>
                {
                    ["in_cycle"]      = true,
                    ["cycle_group"]   = group,
                    ["cycle_size"]    = scc.Count,
                    ["cycle_members"] = members
                });
                marked++;
            }
        }
        return marked;
    }

    /// <summary>Tarjan iterativo: devuelve las componentes fuertemente conexas del grafo dirigido.</summary>
    private static List<List<long>> Tarjan(Dictionary<long, List<long>> adj, IEnumerable<long> nodes)
    {
        var index    = new Dictionary<long, int>();
        var low      = new Dictionary<long, int>();
        var onStack  = new HashSet<long>();
        var stack    = new Stack<long>();
        var sccs     = new List<List<long>>();
        int counter  = 0;

        List<long> Neighbors(long v) => adj.TryGetValue(v, out var l) ? l : new List<long>();

        foreach (var start in nodes)
        {
            if (index.ContainsKey(start)) continue;

            // DFS iterativa: pila de (nodo, índice del siguiente vecino a explorar)
            var work = new Stack<(long v, int i)>();
            work.Push((start, 0));

            while (work.Count > 0)
            {
                var (v, i) = work.Pop();

                if (i == 0)
                {
                    index[v] = low[v] = counter++;
                    stack.Push(v); onStack.Add(v);
                }

                var neigh = Neighbors(v);
                bool recursed = false;
                for (int j = i; j < neigh.Count; j++)
                {
                    var w = neigh[j];
                    if (!index.ContainsKey(w))
                    {
                        work.Push((v, j + 1));   // reanudar v tras procesar w
                        work.Push((w, 0));
                        recursed = true;
                        break;
                    }
                    else if (onStack.Contains(w))
                    {
                        low[v] = Math.Min(low[v], index[w]);
                    }
                }
                if (recursed) continue;

                // Todos los vecinos procesados: propagar low al padre y cerrar SCC si es raíz.
                if (low[v] == index[v])
                {
                    var scc = new List<long>();
                    long w;
                    do { w = stack.Pop(); onStack.Remove(w); scc.Add(w); } while (w != v);
                    sccs.Add(scc);
                }
                if (work.Count > 0)
                {
                    var parent = work.Peek().v;
                    low[parent] = Math.Min(low[parent], low[v]);
                }
            }
        }

        return sccs;
    }
}
