using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodebaseAnalysis.Core.Preparation;

/// <summary>
/// La versión compacta del mapa de dominios que va a las plantillas (<c>ReportFacetInput.DomainMap</c>): sin la lista de ficheros
/// de cada subsistema, con las carpetas, los tipos clave, las entradas y las tablas recortados y, si hay muchos subsistemas, solo los
/// de más líneas, en JSON sin sangría. Lo que un auditor necesita para orientarse; el artefacto completo sigue en
/// <c>_domain_map.json</c>.
/// </summary>
public static class DomainMapSummary
{
    public const int DefaultSubsystems = 60;
    public const int DefaultRoots = 3;
    public const int DefaultKeyTypes = 5;
    public const int DefaultEntryPoints = 5;
    public const int DefaultTables = 5;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static string ToJson(DomainMap map, int maxSubsystems = DefaultSubsystems) =>
        JsonSerializer.Serialize(new
        {
            basis = map.Basis,
            subsystems_total = map.Subsystems.Count,
            unassigned_files = map.UnassignedFiles.Count,
            subsystems = map.Subsystems
                .OrderByDescending(s => s.Metrics.Loc).ThenBy(s => s.Id, StringComparer.Ordinal)
                .Take(maxSubsystems)
                .Select(s => new
                {
                    id = s.Id,
                    name = s.Name,
                    description = s.Description,
                    priority = s.Priority,
                    kind = s.Kind,
                    files = s.Metrics.Files,
                    loc = s.Metrics.Loc,
                    roots = s.Roots.Take(DefaultRoots),
                    key_types = s.KeyTypes.Take(DefaultKeyTypes),
                    entry_points_total = s.Metrics.EntryPoints,
                    entry_points = s.EntryPoints.Take(DefaultEntryPoints).Select(e => $"{e.Kind} {e.Name}"),
                    tables = s.Tables.Take(DefaultTables)
                }),
            warnings = map.Warnings
        }, Json);
}
