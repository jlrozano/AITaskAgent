namespace DotNetEnricher.Enrichers;

/// <summary>
/// Marca interfaces como puntos de extensión del sistema y sus implementaciones con
/// metadatos de rol. Usa tres señales del grafo existente (sin Roslyn):
///   1. Código: interfaces con ≥2 aristas IMPLEMENTS entrantes
///   2. DI:     interfaces con aristas DI_RESOLVES (registradas en el contenedor IoC)
///   3. Docs:   interfaces referenciadas vía DOC_DESCRIBES desde secciones Markdown
///
/// Además parcha la propiedad 'language' en todos los nodos por extensión de fichero.
/// </summary>
public class ExtensionPointEnricher : IGraphEnricher
{
    public string Name  => "ExtensionPointEnricher";
    public int    Order => 6;

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        var graph   = ctx.Repository;
        var project = ctx.Project;
        var db      = ctx.Repository;
        var enrichedAt = DateTime.UtcNow.ToString("O");

        var langCount = await PatchLanguageAsync(db, project);
        progress?.Report($"ExtensionPoint: {langCount} nodes patched with 'language'.");

        var candidates = await DiscoverCandidatesAsync(db, project);
        progress?.Report($"ExtensionPoint: {candidates.Count} candidate extension point interfaces found.");

        int markedInterfaces = 0, markedImpls = 0, edgesCreated = 0;

        foreach (var (ifaceId, ifaceName, _, ifaceFilePath) in candidates)
        {
            var runtime = InferRuntime(ifaceFilePath);
            var kind    = InferExtensionKind(ifaceName);

            await graph.PatchNodePropertiesByIdAsync(ifaceId, new
            {
                is_extension_point = true,
                extension_kind     = kind,
                runtime,
                enriched_by        = new[] { "ExtensionPointEnricher" },
                enriched_at        = enrichedAt,
                presentational     = true
            });
            markedInterfaces++;

            var implementors = await db.QueryAsync(
                """
                SELECT n.id, n.name, n.file_path
                FROM nodes n
                INNER JOIN edges e ON e.source_id = n.id
                    AND e.type = 'IMPLEMENTS'
                    AND e.project = @project
                WHERE e.target_id = @ifaceId AND n.project = @project
                """,
                new { project, ifaceId });

            foreach (var impl in implementors)
            {
                await graph.PatchNodePropertiesByIdAsync((long)impl.id, new
                {
                    is_extension_impl = true,
                    extension_kind    = kind,
                    runtime           = InferRuntime((string?)impl.file_path),
                    enriched_by       = new[] { "ExtensionPointEnricher" },
                    enriched_at       = enrichedAt,
                    presentational    = false
                });
                markedImpls++;

                await graph.UpsertEdgeAsync(project, (long)impl.id, ifaceId, "EXTENDS_POINT", new
                {
                    extension_kind = kind,
                    runtime,
                    origin         = "enricher",
                    enriched_by    = new[] { "ExtensionPointEnricher" },
                    enriched_at    = enrichedAt,
                    presentational = false
                });
                edgesCreated++;
            }

            var regClasses = await db.QueryAsync(
                """
                SELECT DISTINCT c.id, c.name
                FROM nodes di
                INNER JOIN nodes c ON (replace(di.file_path, '\', '/') = c.file_path
                                       OR replace(di.file_path, '\', '/') LIKE '%/' || c.file_path)
                    AND c.project = di.project
                    AND c.label = 'Class'
                WHERE di.project = @project
                  AND di.label = 'DiRegistration'
                  AND EXISTS (
                      SELECT 1 FROM edges e
                      WHERE e.source_id = di.id
                        AND e.target_id = @ifaceId
                        AND e.type = 'DI_RESOLVES'
                  )
                """,
                new { project, ifaceId });

            foreach (var regClass in regClasses)
            {
                await graph.UpsertEdgeAsync(project, ifaceId, (long)regClass.id, "REGISTERED_VIA", new
                {
                    origin         = "enricher",
                    enriched_by    = new[] { "ExtensionPointEnricher" },
                    enriched_at    = enrichedAt,
                    presentational = true
                });
                edgesCreated++;
            }
        }

        progress?.Report(
            $"ExtensionPoint: {markedInterfaces} interfaces, {markedImpls} impls, {edgesCreated} edges.");
    }

    private static async Task<int> PatchLanguageAsync(GraphRepository db, string project)
    {
        int total = 0;
        var extensions = new Dictionary<string, string>
        {
            [".cs"]   = "csharp",
            [".ts"]   = "typescript",
            [".tsx"]  = "typescript",
            [".md"]   = "markdown",
            [".json"] = "json"
        };

        foreach (var (ext, lang) in extensions)
        {
            int rows = await db.ExecuteAsync(
                """
                UPDATE nodes
                SET properties = json_set(COALESCE(properties, '{}'), '$.language', @lang)
                WHERE project = @project AND file_path LIKE @pattern
                """,
                new { project, lang, pattern = $"%{ext}" });
            total += rows;
        }

        return total;
    }

    private static async Task<List<(long Id, string Name, string QualifiedName, string? FilePath)>> DiscoverCandidatesAsync(
        GraphRepository db, string project)
    {
        const string csFilter = "AND n.file_path LIKE '%.cs'";

        var fromCode = await db.QueryAsync(
            $"""
            SELECT DISTINCT n.id, n.name, n.qualified_name, n.file_path
            FROM nodes n
            INNER JOIN (
                SELECT target_id
                FROM edges
                WHERE type = 'IMPLEMENTS' AND project = @project
                GROUP BY target_id
                HAVING COUNT(*) >= 2
            ) impl_counts ON impl_counts.target_id = n.id
            WHERE n.project = @project AND n.label = 'Interface' {csFilter}
            """,
            new { project });

        var fromDi = await db.QueryAsync(
            $"""
            SELECT DISTINCT n.id, n.name, n.qualified_name, n.file_path
            FROM nodes n
            INNER JOIN edges e ON e.target_id = n.id
                AND e.type = 'DI_RESOLVES'
                AND e.project = @project
            WHERE n.project = @project AND n.label = 'Interface' {csFilter}
            """,
            new { project });

        var fromDocs = await db.QueryAsync(
            $"""
            SELECT DISTINCT n.id, n.name, n.qualified_name, n.file_path
            FROM nodes n
            INNER JOIN edges e ON e.target_id = n.id
                AND e.type = 'DOC_DESCRIBES'
                AND e.project = @project
            WHERE n.project = @project AND n.label = 'Interface' {csFilter}
            """,
            new { project });

        return fromCode.Concat(fromDi).Concat(fromDocs)
            .GroupBy(n => (long)n.id)
            .Select(g => g.First())
            .Select(n => ((long)n.id, (string)n.name, (string)n.qualified_name, (string?)n.file_path))
            .ToList();
    }

    private static string InferRuntime(string? filePath) =>
        Path.GetExtension(filePath ?? "").ToLowerInvariant() switch
        {
            ".cs"            => "csharp",
            ".ts" or ".tsx"  => "typescript",
            ".js"            => "javascript",
            _                => "unknown"
        };

    private static string InferExtensionKind(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("validator"))              return "validator";
        if (lower.Contains("normalizer"))             return "normalizer";
        if (lower.Contains("transform"))              return "transform";
        if (lower.Contains("adapter"))                return "adapter";
        if (lower.Contains("plugin"))                 return "plugin";
        if (lower.Contains("rule"))                   return "rule";
        return "unknown";
    }
}
