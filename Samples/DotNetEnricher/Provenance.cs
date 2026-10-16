using System.Globalization;
using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Enrichment;

namespace DotNetEnricher;

/// <summary>
/// Procedencia uniforme de lo que escribe la pasada. <see cref="GraphRepository"/> la sella en cada nodo parcheado o
/// creado y en cada arista escrita, así que ningún enriquecedor tiene que acordarse:
///
///   - <c>enriched_by</c>: se suma el enriquecedor que escribe (<see cref="Enricher"/>, que fija la pasada antes de
///     ejecutar cada uno), sin quitar las marcas que hubiera (<see cref="EnrichmentMarks.AddMark"/>).
///   - <c>enriched_at</c>: el inicio de la pasada, salvo que el enriquecedor ponga el suyo.
///   - <c>enricher_version</c>: la de <see cref="DotNetEnricherIdentity.EnricherVersion"/>.
///   - En las aristas, además, <c>origin</c> = <c>enricher</c> si el enriquecedor no da otro (las exactas llevan
///     <c>compiler</c>).
///
/// Antes solo las clases, interfaces y campos llevaban <c>enriched_by</c>; los métodos parcheados no llevaban ninguna marca.
/// Las limpiezas de la pasada no cambian: los nodos se borran por <c>origin = enricher</c> más la marca, y la marca en un nodo
/// de cbm parcheado no lo convierte en propio. Las actualizaciones con SQL directo (<c>language</c> por extensión en
/// <c>ExtensionPointEnricher</c>, los <c>json_remove</c> de limpieza) no pasan por aquí.
/// </summary>
public sealed class ProvenanceStamp(string enricherVersion, DateTimeOffset enrichedAt)
{
    public string EnricherVersion { get; } = enricherVersion;

    public string EnrichedAt { get; } = enrichedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    /// <summary>El enriquecedor que está escribiendo; sin él no se sella nada.</summary>
    public string? Enricher { get; set; }

    /// <summary>Sella un nodo. <paramref name="keepEnrichedAt"/>: el enriquecedor ya puso su <c>enriched_at</c>.</summary>
    public void ApplyToNode(JsonObject properties, bool keepEnrichedAt)
    {
        if (Enricher is null) return;
        EnrichmentMarks.AddMark(properties, Enricher);
        if (!keepEnrichedAt || properties["enriched_at"] is null) properties["enriched_at"] = EnrichedAt;
        properties["enricher_version"] = EnricherVersion;
    }

    public void ApplyToEdge(JsonObject properties)
    {
        if (Enricher is null) return;
        ApplyToNode(properties, keepEnrichedAt: true);
        if (properties["origin"] is null) properties["origin"] = EnrichmentMarks.OriginEnricher;
    }
}
