using System.Text.RegularExpressions;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// Trata como raíces vivas las clases que un <c>reflection_targets</c> (escrito por <see cref="ConstantEnricher"/>) puede nombrar:
/// <c>Type.GetType(PREFIJO + nombre)</c> con <c>PREFIJO</c> constante da el patrón <c>Orders.Core.Notifications.*</c>, y las
/// clases de ese espacio de nombres se instancian por reflexión aunque ninguna referencia exacta las alcance. Sin esto,
/// las implementaciones que solo se crean así salían como candidatas a código muerto.
/// </summary>
public class ReflectionTargetSuppressor : IDeadCodeSuppressor
{
    public string Name => "ReflectionTargetSuppressor";

    public async Task<IReadOnlyCollection<long>> GetLiveRootIdsAsync(EnrichmentContext ctx)
    {
        var targets = (await ctx.Repository.QueryAsync(
            """
            SELECT DISTINCT j.value AS target
            FROM nodes n, json_each(n.properties, '$.reflection_targets') j
            WHERE n.project = @project AND json_type(n.properties, '$.reflection_targets') = 'array'
            """,
            new { project = ctx.Project }))
            .Select(row => (string)row.target)
            .ToList();
        if (targets.Count == 0) return [];

        var matchers = targets.Select(Matcher).ToList();
        var symbols = await ctx.GetGraphSymbolsAsync();
        return symbols.TypesByNode
            .Where(entry => matchers.Any(match => match(entry.Value.ToDisplayString())))
            .Select(entry => entry.Key)
            .ToList();
    }

    /// <summary>El nombre exacto o, con «*» por cada tramo no constante, el patrón.</summary>
    private static Func<string, bool> Matcher(string target)
    {
        if (!target.Contains('*'))
            return name => string.Equals(name, target, StringComparison.Ordinal);
        var pattern = new Regex("^" + string.Join(".*", target.Split('*').Select(Regex.Escape)) + "$", RegexOptions.CultureInvariant);
        return pattern.IsMatch;
    }
}
