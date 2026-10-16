namespace DotNetEnricher.Enrichers;

/// <summary>
/// Materializa los proyectos C# de la solución en el grafo SQLite (el indexador base solo produce
/// 1 nodo Project para todo el repositorio), calcula métricas de acoplamiento Ca/Ce/I y crea:
///   - Nodos <c>CsProject</c> con métricas y rol inferido.
///   - Aristas <c>PROJECT_DEPENDS_ON</c> (CsProject → CsProject) con peso y desglose por tipo.
///   - Aristas <c>BELONGS_TO_PROJECT</c> (Class/Interface/Method/… → CsProject) con
///     <c>presentational=false</c> (solo para consultas/filtrado).
///
/// Los proyectos salen de la solución cargada (<see cref="EnrichmentContext.CsProjects"/>) y cada nodo va al proyecto
/// de la carpeta que contiene su <c>file_path</c>. El original los infería del segundo segmento del qualified_name, que
/// solo acierta con los .csproj en la raíz del repositorio (con <c>src/</c> salía un proyecto «src») y con un nombre de
/// proyecto de cbm sin puntos (el de una ruta como <c>C:\Users\nombre.apellido\…</c> los tiene).
/// </summary>
public class ProjectEnricher : IGraphEnricher
{
    public string Name  => "ProjectEnricher";
    public int    Order => 8;

    /// <summary>Labels que pertenecen a un proyecto. Los QN que empiezan por '_' son nodos propios del enriquecedor.</summary>
    private const string MemberFilter = """
        label IN ('Class','Interface','Method','Enum','Type')
          AND substr(qualified_name, 1, 1) <> '_'
        """;

    private const string CrossFileEdgesSql = """
        SELECT n1.file_path AS from_file, n2.file_path AS to_file, e.type AS edge_type, COUNT(*) AS weight
        FROM edges e
        JOIN nodes n1 ON n1.id = e.source_id AND n1.project = @project
        JOIN nodes n2 ON n2.id = e.target_id AND n2.project = @project
        WHERE e.project = @project
          AND e.type IN ('CALLS','USAGE','IMPORTS','IMPLEMENTS','INHERITS')
          AND substr(n1.qualified_name, 1, 1) <> '_'
          AND substr(n2.qualified_name, 1, 1) <> '_'
          AND n1.file_path <> n2.file_path
        GROUP BY n1.file_path, n2.file_path, e.type
        """;

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        var graph      = ctx.Repository;
        var project    = ctx.Project;
        var enrichedAt = DateTime.UtcNow.ToString("O");

        var projects = ctx.CsProjects
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        progress?.Report($"ProjectEnricher: {projects.Count} C# projects in the solution.");
        if (projects.Count == 0) return;

        var depMap = new Dictionary<(string From, string To), (int TotalWeight, Dictionary<string, int> ByType)>();

        foreach (var row in await graph.QueryAsync(CrossFileEdgesSql, new { project }))
        {
            var from = ctx.CsProjectOf((string?)row.from_file)?.Name;
            var to   = ctx.CsProjectOf((string?)row.to_file)?.Name;
            if (from is null || to is null || from == to) continue;

            string type = (string)row.edge_type;
            int    w    = (int)(long)row.weight;

            var key = (from, to);
            if (!depMap.TryGetValue(key, out var entry))
            {
                entry = (0, new Dictionary<string, int>());
                depMap[key] = entry;
            }
            var updated = (entry.TotalWeight + w, entry.ByType);
            updated.ByType[type] = updated.ByType.GetValueOrDefault(type) + w;
            depMap[key] = updated;
        }

        var metrics = new Dictionary<string, (int Ca, int Ce)>();
        foreach (var p in projects)
            metrics[p.Name] = (0, 0);

        foreach (var ((from, to), _) in depMap)
        {
            if (metrics.ContainsKey(from))
                metrics[from] = (metrics[from].Ca, metrics[from].Ce + 1);
            if (metrics.ContainsKey(to))
                metrics[to] = (metrics[to].Ca + 1, metrics[to].Ce);
        }

        var projectNodeIds = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var csProj in projects)
        {
            var (ca, ce)  = metrics[csProj.Name];
            double instability = (ca + ce) == 0 ? 0.0 : Math.Round((double)ce / (ca + ce), 2);
            var role = InferProjectRole(csProj);

            var properties = new Dictionary<string, object?>
            {
                ["origin"]            = "enricher",
                ["enriched_by"]       = new[] { "ProjectEnricher" },
                ["enriched_at"]       = enrichedAt,
                ["presentational"]    = true,
                ["language"]          = "csharp",
                ["role"]              = role,
                ["directory"]         = csProj.RelativeDirectory,
                ["afferent_coupling"] = ca,
                ["efferent_coupling"] = ce,
                ["instability"]       = instability,
                ["is_host"]           = role == "host",
                ["is_test"]           = csProj.IsTest
            };
            // Lo que declara el .csproj (ProjectReference y PackageReference), para contrastarlo con las dependencias que
            // se observan en el código (PROJECT_DEPENDS_ON). Sin referencias declaradas no se escribe la clave.
            if (csProj.ProjectReferences is { Count: > 0 } references)
                properties["project_references"] = references;
            if (csProj.Packages is { Count: > 0 } packages)
                properties["packages"] = packages;

            var nodeId = await graph.UpsertNodeAsync(
                project,
                label:         "CsProject",
                name:          csProj.Name,
                qualifiedName: $"_proj_.{csProj.Name}",
                filePath:      "",
                startLine:     0,
                properties:    properties,
                endLine:       0);

            projectNodeIds[csProj.Name] = nodeId;
        }

        progress?.Report($"ProjectEnricher: {projectNodeIds.Count} CsProject nodes upserted.");

        int depEdges = 0;
        foreach (var ((from, to), (totalWeight, byType)) in depMap)
        {
            if (!projectNodeIds.TryGetValue(from, out var fromId)) continue;
            if (!projectNodeIds.TryGetValue(to,   out var toId))   continue;

            await graph.UpsertEdgeAsync(project, fromId, toId, "PROJECT_DEPENDS_ON", new
            {
                weight         = totalWeight,
                via            = byType,
                origin         = "enricher",
                enriched_by    = new[] { "ProjectEnricher" },
                enriched_at    = enrichedAt,
                presentational = true
            });
            depEdges++;
        }

        progress?.Report($"ProjectEnricher: {depEdges} PROJECT_DEPENDS_ON edges created.");

        var memberNodes = await graph.QueryAsync(
            $"SELECT id, file_path FROM nodes WHERE project = @project AND {MemberFilter}", new { project });

        int belongsEdges = 0, skipped = 0;
        foreach (var row in memberNodes)
        {
            long nodeId = (long)row.id;

            var csProj = ctx.CsProjectOf((string?)row.file_path);
            if (csProj is null || !projectNodeIds.TryGetValue(csProj.Name, out var projNodeId)) { skipped++; continue; }

            await graph.UpsertEdgeAsync(project, nodeId, projNodeId, "BELONGS_TO_PROJECT", new
            {
                origin         = "enricher",
                enriched_by    = new[] { "ProjectEnricher" },
                enriched_at    = enrichedAt,
                presentational = false
            });
            belongsEdges++;
        }

        progress?.Report(
            $"ProjectEnricher: {belongsEdges} BELONGS_TO_PROJECT edges created ({skipped} skipped).");
    }

    /// <summary>
    /// Rol del proyecto para las reglas de capas de <see cref="GraphAnalyticsEnricher"/>. Primero lo que dice la propia
    /// solución (proyecto de test, ejecutable); después, convenciones de nombre habituales. Un proyecto de test lleva el
    /// rol <c>test</c>, fuera de la jerarquía de capas: depender de todo es lo normal en un test.
    /// </summary>
    internal static string InferProjectRole(CsProjectInfo project)
    {
        var name = project.Name;
        if (project.IsTest ||
            name.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Spec", StringComparison.OrdinalIgnoreCase))
            return "test";

        if (project.IsExecutable ||
            name.Contains("Host", StringComparison.OrdinalIgnoreCase))
            return "host";

        if (name.Contains("Adapter", StringComparison.OrdinalIgnoreCase))
            return "adapter";

        if (!name.Contains("Api", StringComparison.OrdinalIgnoreCase) &&
            (name.Contains("Frontend", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Web",      StringComparison.OrdinalIgnoreCase) ||
             name.Contains("UI",       StringComparison.Ordinal)))
            return "frontend";

        if (name.Contains("Mock",    StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Stub",    StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Utility", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Common",  StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Shared",  StringComparison.OrdinalIgnoreCase))
            return "utility";

        return "core";
    }
}
