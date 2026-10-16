namespace DotNetEnricher.Enrichers;

/// <summary>
/// Aporta nodos que deben tratarse como <b>raíces vivas</b> del análisis de alcanzabilidad, aunque el
/// grafo estático (CALLS/USAGE/…) no muestre ninguna arista entrante hacia ellos — típicamente porque
/// se cargan/registran por reflexión o convención en runtime (plugins, handlers descubiertos, etc.).
///
/// Análogo a <see cref="IFlowEntryProvider"/>: se inyecta como <c>IEnumerable&lt;IDeadCodeSuppressor&gt;</c>
/// en <see cref="ReachabilityEnricher"/>. Los específicos de una aplicación se registran en <c>Program.cs</c> junto al genérico.
/// </summary>
public interface IDeadCodeSuppressor
{
    /// <summary>Nombre para el campo <c>dead_code_suppressed_by</c> de los nodos que suprime.</summary>
    string Name { get; }

    /// <summary>
    /// Ids de nodos (Method/Function/Class) que deben sembrarse como raíces vivas del BFS.
    /// Consulta el grafo por <c>ctx.Repository</c>, dentro de la transacción de la pasada — p.ej. clases que
    /// implementan una interfaz de plugin conocida (IMPLEMENTS → Interface).
    /// </summary>
    Task<IReadOnlyCollection<long>> GetLiveRootIdsAsync(EnrichmentContext ctx);
}
