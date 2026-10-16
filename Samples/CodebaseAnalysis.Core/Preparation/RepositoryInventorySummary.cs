using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodebaseAnalysis.Core.Preparation;

/// <summary>
/// La versión compacta del inventario que va a las plantillas (<c>ReportFacetInput.RepositoryInventory</c>): sin las líneas de
/// cada fichero ni la lista de ficheros de configuración, con las entradas y las tablas recortadas, en JSON sin sangría. Lo que
/// un auditor necesita para orientarse, no el artefacto completo (que sigue en <c>_repository_inventory.json</c>).
/// </summary>
public static class RepositoryInventorySummary
{
    public const int DefaultEntryPoints = 40;
    public const int DefaultTables = 20;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string ToJson(RepositoryInventory inventory, int maxEntryPoints = DefaultEntryPoints, int maxTables = DefaultTables) =>
        JsonSerializer.Serialize(new
        {
            modules = inventory.Modules,
            source_sets = inventory.SourceSets,
            languages = inventory.Languages,
            production_loc = new { total = inventory.ProductionLoc.Total, by_language = inventory.ProductionLoc.ByLanguage },
            graph_coverage = inventory.GraphCoverage,
            entry_points_total = inventory.EntryPointsTotal,
            entry_points = inventory.EntryPoints.Take(maxEntryPoints),
            tables_total = inventory.Persistence.TablesTotal,
            tables = inventory.Persistence.Tables.Take(maxTables),
            configuration_files_total = inventory.Persistence.ConfigurationFilesTotal,
            warnings = inventory.Warnings
        }, Json);
}
