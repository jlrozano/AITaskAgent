namespace DotNetEnricher.Enrichers;

/// <summary>
/// Métricas derivadas del grafo. <c>fan_in</c>/<c>fan_out</c> de los métodos C# salen del índice exacto de referencias de
/// Roslyn; el resto (percentiles, agregados, y el fan de nodos que Roslyn no resuelve, como las funciones de otros
/// lenguajes) se calcula sobre la SQLite.
/// Depende de que <see cref="CyclomaticEnricher"/> (Order 13) haya materializado <c>cyclomatic</c>
/// y de que exista <c>lines</c> (cbm). Como esas viven en el JSON <c>properties</c>, se leen con
/// <c>json_extract</c> en SQL directo (nunca por el proxy/Cypher, que no lee props de nodo).
///
/// Materializa:
///   - Method/Function: <c>fan_in</c>/<c>fan_out</c>, <c>hotspot</c> = round(cyclomatic·ln(1+fan_in),2).
///     Con Roslyn, <c>fan_in</c> son los miembros distintos que referencian al método (o a lo que implementa o sobrescribe:
///     una llamada por la interfaz cuenta para la implementación) y <c>fan_out</c> los símbolos distintos del código
///     fuente que su firma y su cuerpo nombran, sin contarse a sí mismo. Sin Roslyn, aristas CALLS+USAGE de cbm.
///   - Class: agregados de sus métodos hijos (DEFINES_METHOD): method_count, loc_total,
///     cyclomatic_total/max, fan_in_total.
///   - CsProject (_proj_.): percentiles cyclomatic_p50/p90, cyclomatic_max, loc_p90.
/// </summary>
public class MetricsEnricher : IGraphEnricher
{
    public string Name  => "MetricsEnricher";
    public int    Order => 14;

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        var graph   = ctx.Repository;
        var project = ctx.Project;

        var db      = graph;

        // ── 1. fan_in / fan_out / hotspot por Method y Function ────────────────────────────
        var methodRows = (await db.QueryAsync($"""
            SELECT
                n.id  AS id,
                n.name AS name,
                (SELECT COUNT(*) FROM edges e
                   WHERE e.project = @project AND e.target_id = n.id
                     AND e.type IN ('CALLS','USAGE'))        AS fan_in,
                (SELECT COUNT(*) FROM edges e
                   WHERE e.project = @project AND e.source_id = n.id
                     AND e.type IN ('CALLS','USAGE'))        AS fan_out,
                json_extract(n.properties, '$.cyclomatic')   AS cyclomatic
            FROM nodes n
            WHERE n.project = @project AND n.label IN ('Method','Function') AND {LanguageScope.NotJava("n")}
            """, new { project })).ToList();

        var exact = await ExactFanAsync(ctx);
        var hotspots = new List<(string Name, double Hotspot)>();
        int withMetrics = 0;

        foreach (var row in methodRows)
        {
            long   id      = (long)row.id;
            string name    = (string)row.name;
            long   fanIn   = Convert.ToInt64(row.fan_in);
            long   fanOut  = Convert.ToInt64(row.fan_out);
            int    cyc     = row.cyclomatic is null ? 0 : Convert.ToInt32(row.cyclomatic);

            // El método es de la solución y Roslyn lo resolvió: sus cifras sustituyen a las de las aristas por nombre.
            if (exact.TryGetValue(id, out var fan))
            {
                fanIn  = fan.In;
                fanOut = fan.Out;
            }

            double hotspot = Math.Round(cyc * Math.Log(1 + fanIn), 2);

            await graph.PatchNodePropertiesByIdAsync(id, new
            {
                fan_in  = fanIn,
                fan_out = fanOut,
                hotspot
            });

            withMetrics++;
            if (hotspot > 0) hotspots.Add((name, hotspot));
        }

        progress?.Report($"Metrics: {withMetrics} methods/functions with fan-in/out + hotspot ({exact.Count} exact from Roslyn references).");

        var top3 = hotspots.OrderByDescending(h => h.Hotspot).Take(3).ToList();
        if (top3.Count > 0)
            progress?.Report("Metrics: top-3 hotspots -> " +
                string.Join(", ", top3.Select(h => $"{h.Name}={h.Hotspot}")));

        // ── 2. Agregados por Class (vía DEFINES_METHOD) ────────────────────────────────────
        // fan_in de los métodos ya está persistido arriba, por lo que json_extract lo lee.
        var classRows = (await db.QueryAsync("""
            SELECT
                c.id AS id,
                COUNT(m.id)                                                   AS method_count,
                COALESCE(SUM(json_extract(m.properties, '$.lines')), 0)       AS loc_total,
                COALESCE(SUM(json_extract(m.properties, '$.cyclomatic')), 0)  AS cyclomatic_total,
                COALESCE(MAX(json_extract(m.properties, '$.cyclomatic')), 0)  AS cyclomatic_max,
                COALESCE(SUM(json_extract(m.properties, '$.fan_in')), 0)      AS fan_in_total
            FROM nodes c
            JOIN edges e ON e.source_id = c.id AND e.project = c.project AND e.type = 'DEFINES_METHOD'
            JOIN nodes m ON m.id = e.target_id AND m.project = c.project
            WHERE c.project = @project AND c.label = 'Class'
            GROUP BY c.id
            """, new { project })).ToList();

        foreach (var row in classRows)
        {
            await graph.PatchNodePropertiesByIdAsync((long)row.id, new
            {
                method_count     = Convert.ToInt64(row.method_count),
                loc_total        = Convert.ToInt64(row.loc_total),
                cyclomatic_total = Convert.ToInt64(row.cyclomatic_total),
                cyclomatic_max   = Convert.ToInt64(row.cyclomatic_max),
                fan_in_total     = Convert.ToInt64(row.fan_in_total)
            });
        }

        progress?.Report($"Metrics: {classRows.Count} classes with aggregate metrics.");

        // ── 3. Percentiles por CsProject ───────────────────────────────────────────────────
        // El proyecto de cada método es el de la carpeta de su fichero (mismo criterio que ProjectEnricher).
        var perMethod = (await db.QueryAsync("""
            SELECT
                n.file_path AS file_path,
                json_extract(n.properties, '$.cyclomatic') AS cyclomatic,
                json_extract(n.properties, '$.lines')      AS lines
            FROM nodes n
            WHERE n.project = @project AND n.label IN ('Method','Function')
            """, new { project })).ToList();

        var cycByProject = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var locByProject = new Dictionary<string, List<double>>(StringComparer.Ordinal);

        foreach (var row in perMethod)
        {
            var csProj = ctx.CsProjectOf((string?)row.file_path)?.Name;
            if (string.IsNullOrWhiteSpace(csProj)) continue;

            if (row.cyclomatic is not null)
            {
                if (!cycByProject.TryGetValue(csProj, out var cl)) cycByProject[csProj] = cl = new();
                cl.Add(Convert.ToDouble(row.cyclomatic));
            }
            if (row.lines is not null)
            {
                if (!locByProject.TryGetValue(csProj, out var ll)) locByProject[csProj] = ll = new();
                ll.Add(Convert.ToDouble(row.lines));
            }
        }

        var csProjects = (await db.QueryAsync(
            "SELECT id, name FROM nodes WHERE project = @project AND label = 'CsProject'",
            new { project })).ToList();

        int projPatched = 0;
        foreach (var row in csProjects)
        {
            long   id   = (long)row.id;
            string name = (string)row.name;

            cycByProject.TryGetValue(name, out var cyc);
            locByProject.TryGetValue(name, out var loc);
            cyc ??= new();
            loc ??= new();

            await graph.PatchNodePropertiesByIdAsync(id, new
            {
                cyclomatic_p50 = Percentile(cyc, 50),
                cyclomatic_p90 = Percentile(cyc, 90),
                cyclomatic_max = cyc.Count == 0 ? 0 : (int)cyc.Max(),
                loc_p90        = Percentile(loc, 90)
            });
            projPatched++;
        }

        progress?.Report($"Metrics: {projPatched} CsProjects with percentiles.");
    }

    /// <summary>Fan-in y fan-out exactos por nodo Method, del índice de referencias (las sobrecargas comparten nodo).</summary>
    private static async Task<Dictionary<long, (int In, int Out)>> ExactFanAsync(EnrichmentContext ctx)
    {
        var result = new Dictionary<long, (int, int)>();
        var symbols = await ctx.GetGraphSymbolsAsync();
        var index = ctx.References;
        foreach (var (nodeId, methods) in symbols.MethodsByNode)
        {
            var own = methods.Select(m => SymbolKey.Of(m)!.Value).ToHashSet();
            var callers = new HashSet<SymbolKey>();
            var targets = new HashSet<SymbolKey>();
            foreach (var method in methods)
            {
                foreach (var r in index.To(method).Concat(ReferenceIndex.ImplementedMembers(method).SelectMany(index.To)))
                    if (!own.Contains(r.Caller)) callers.Add(r.Caller);
                foreach (var r in index.From(method))
                    if (!own.Contains(r.Target)) targets.Add(r.Target);
            }
            result[nodeId] = (callers.Count, targets.Count);
        }
        return result;
    }

    /// <summary>Percentil por rango más cercano (nearest-rank) → devuelve un valor observado (entero).</summary>
    private static int Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int rank = (int)Math.Ceiling(p / 100.0 * sorted.Count);
        int idx  = Math.Clamp(rank - 1, 0, sorted.Count - 1);
        return (int)sorted[idx];
    }
}
