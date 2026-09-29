using AITaskAgent.Support.Template;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CodebaseAnalysis.Core.Support;

/// <summary>
/// El grafo sobre el que se generaron los informes: la huella de la indexación de cbm
/// (<see cref="Enrichment.EnrichmentRuns.IndexFingerprintAsync"/>) y, por enriquecedor, la última fila <c>ok</c> de
/// <c>enrichment_runs</c> con esa misma huella, que es la pasada que dejó el grafo como está. La calcula
/// <c>SqlFacetExtractorStep</c> y la leen los auditores y la síntesis para su línea <c>analysis-meta</c>.
/// </summary>
public sealed record GraphProvenance(string? Fingerprint, IReadOnlyList<string> EnrichmentRuns)
{
    /// <summary><c>Enriquecedor#id</c> separados por comas, en el orden de las filas; null si no hay ninguna.</summary>
    public string? EnrichmentRunsText => EnrichmentRuns.Count == 0 ? null : string.Join(",", EnrichmentRuns);

    public static async Task<GraphProvenance> ReadAsync(SqliteConnection conn, CancellationToken ct)
    {
        var fingerprint = await Enrichment.EnrichmentRuns.IndexFingerprintAsync(conn, ct);
        if (fingerprint == null)
            return new GraphProvenance(null, []);

        var latest = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var run in await Enrichment.EnrichmentRuns.ListAsync(conn, ct))
        {
            if (run.Status == Enrichment.EnrichmentRuns.StatusOk && run.IndexFingerprint == fingerprint)
                latest[run.Enricher] = run.Id;
        }

        return new GraphProvenance(fingerprint, [.. latest.OrderBy(r => r.Value).Select(r => $"{r.Key}#{r.Value.ToString(CultureInfo.InvariantCulture)}")]);
    }
}

/// <summary>
/// Procedencia que el pipeline añade a la línea <c>analysis-meta</c> de cada informe y del resumen ejecutivo (recomendación
/// 12 de la auditoría), para poder comparar dos ejecuciones:
/// <list type="bullet">
/// <item><c>generated_at</c>: instante UTC en que se escribió.</item>
/// <item><c>model</c> y <c>provider</c>: modelo y tipo de servicio del rol; <c>response_model</c>, el que dice el servicio que
/// respondió, si es otro (Azure devuelve el nombre con versión, que cambia al actualizar el despliegue).</item>
/// <item><c>system_sha</c> y <c>prompt_sha</c>: SHA-256 corto (12 hexadecimales) de cada plantilla con sus inclusiones
/// resueltas y sin datos (<see cref="TemplateSha"/>).</item>
/// <item><c>graph</c>: la huella de la indexación; <c>enrichment</c>, las filas de <c>enrichment_runs</c>
/// (<see cref="GraphProvenance"/>).</item>
/// <item><c>app</c>: <see cref="AppVersion.Current"/>.</item>
/// </list>
/// </summary>
public static class ReportProvenance
{
    public const string GeneratedAtKey = "generated_at";
    public const string ModelKey = "model";
    public const string ProviderKey = "provider";
    public const string ResponseModelKey = "response_model";
    public const string SystemShaKey = "system_sha";
    public const string PromptShaKey = "prompt_sha";
    public const string GraphKey = "graph";
    public const string EnrichmentKey = "enrichment";
    public const string AppKey = "app";

    /// <summary>Hexadecimales del SHA-256 que se guardan: 48 bits, de sobra para distinguir versiones de una plantilla.</summary>
    public const int ShaLength = 12;

    /// <summary>
    /// SHA-256 corto de la plantilla <paramref name="templateName"/> renderizada sin datos: con las inclusiones
    /// (<c>_common_rules.md</c>, <c>_graph_rules.md</c>...) resueltas y las variables vacías. Se descartan las dos
    /// alternativas: el fichero crudo no cambia cuando cambia una inclusión, y el texto renderizado con los datos cambia con
    /// cada grafo y cada documento, que ya identifican <c>graph</c> y <c>enrichment</c>, así que dos ejecuciones con la misma
    /// plantilla darían hashes distintos. Null si no hay nombre o la plantilla no existe.
    /// </summary>
    public static string? TemplateSha(ITemplateProvider? provider, string? templateName)
    {
        if (provider == null || string.IsNullOrWhiteSpace(templateName) || provider.GetTemplate(templateName) == null)
            return null;

        try
        {
            var rendered = provider.Render(templateName, new Dictionary<string, object?>()) ?? string.Empty;
            return Sha(rendered.ReplaceLineEndings("\n"));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FileNotFoundException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string Sha(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..ShaLength];

    /// <summary>Los pares en el orden en que se escriben; los que no tienen valor no se escriben.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Build(
        DateTimeOffset generatedAt,
        string? model,
        string? provider,
        string? responseModel,
        string? systemSha,
        string? promptSha,
        GraphProvenance? graph)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                pairs.Add(new(key, value));
        }

        Add(GeneratedAtKey, generatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        Add(ModelKey, model);
        Add(ProviderKey, provider);
        if (!string.Equals(responseModel, model, StringComparison.OrdinalIgnoreCase))
            Add(ResponseModelKey, responseModel);
        Add(SystemShaKey, systemSha);
        Add(PromptShaKey, promptSha);
        Add(GraphKey, graph?.Fingerprint);
        Add(EnrichmentKey, graph?.EnrichmentRunsText);
        Add(AppKey, AppVersion.Current);
        return pairs;
    }
}
