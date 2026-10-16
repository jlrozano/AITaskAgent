namespace DotNetEnricher.Enrichers;

/// <summary>
/// Supresor genérico: trata las clases de test (nombre <c>*Test</c>/<c>*Tests</c>/<c>*Spec</c>/<c>*Specs</c>
/// o <c>is_test</c>) como raíces vivas. El código de test no es "dead code" del producto, y además su
/// presencia mantiene vivo (por propagación DEFINES_METHOD → CALLS) el código de producción que ejercita,
/// lo que reduce falsos positivos. cbm no marca <c>is_test</c> de forma fiable, de ahí la heurística por nombre.
/// </summary>
public class TestConventionSuppressor : IDeadCodeSuppressor
{
    public string Name => "TestConventionSuppressor";

    public async Task<IReadOnlyCollection<long>> GetLiveRootIdsAsync(EnrichmentContext ctx)
    {
        var ids = (await ctx.Repository.QueryAsync(
            """
            SELECT id
            FROM nodes
            WHERE project = @project AND label = 'Class'
              AND (name LIKE '%Test' OR name LIKE '%Tests'
                   OR name LIKE '%Spec' OR name LIKE '%Specs'
                   OR json_extract(properties, '$.is_test') = 1)
            """,
            new { project = ctx.Project }))
            .Select(x => (long)x.id)
            .ToList();

        return ids;
    }
}
