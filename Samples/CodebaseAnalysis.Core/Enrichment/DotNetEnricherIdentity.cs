namespace CodebaseAnalysis.Core.Enrichment;

/// <summary>
/// Identidad de la pasada del enriquecedor Roslyn (<c>Samples/DotNetEnricher</c>) en <c>enrichment_runs</c> y dónde se
/// despliega su ejecutable. El CLI enlaza este fichero, así que el nombre y la versión con que registra la pasada son los
/// mismos con que <see cref="Steps.DotNetEnricherStep"/> decide si puede saltársela.
/// </summary>
public static class DotNetEnricherIdentity
{
    /// <summary>Valor de <c>enrichment_runs.enricher</c>. Cada enriquecedor interno marca sus nodos y aristas con su propio nombre.</summary>
    public const string EnricherName = "DotNetEnricher";

    /// <summary>Sube cuando cambia lo que se escribe en el grafo: una pasada correcta de otra versión no vale.</summary>
    public const string EnricherVersion = "5";

    /// <summary>Nombre del ensamblado y del ejecutable del CLI.</summary>
    public const string ExecutableName = "DotNetEnricher";

    /// <summary>Subcarpeta de la salida de la app (CLI y web) donde el build deja el CLI con sus dependencias.</summary>
    public const string BundleFolder = "dotnet-enricher";
}
