using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodebaseAnalysis.Core.Preparation;

/// <summary>
/// E/S de los cuatro artefactos de la preparación en la carpeta de informes (<c>reports/_repository_inventory.json</c>,
/// <c>_domain_map.json</c>, <c>_coverage_plan.json</c> y <c>_time_budget.json</c>). Como <c>_run_meta.json</c>, empiezan por
/// <c>_</c> y no son informes del catálogo: ni se listan ni se copian a <c>backups/</c>.
/// </summary>
/// <remarks>
/// La serialización es estable: snake_case, enumerados como texto, sangría fija, sin escapar los no ASCII y sin reordenar las
/// listas (las deja ordenadas el productor), de modo que la misma entrada da los mismos bytes salvo
/// <see cref="IPreparationArtifact.GeneratedAt"/>. Ninguna operación lanza por un problema de disco o de contenido: perder un
/// artefacto no debe parar un análisis, así que devuelven el aviso que quien llama emite.
/// </remarks>
public static class PreparationArtifacts
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // ReportType va con el nombre exacto del enumerado (el que aceptan --reports y ParseReportType); el resto, en snake_case.
        Converters = { new JsonStringEnumConverter<Models.ReportType>(), new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    /// <summary>Nombre del fichero del artefacto de tipo <typeparamref name="T"/>.</summary>
    public static string FileNameOf<T>() where T : IPreparationArtifact => typeof(T) switch
    {
        var t when t == typeof(RepositoryInventory) => RepositoryInventory.FileName,
        var t when t == typeof(DomainMap) => DomainMap.FileName,
        var t when t == typeof(CoveragePlan) => CoveragePlan.FileName,
        var t when t == typeof(TimeBudgetPlan) => TimeBudgetPlan.FileName,
        _ => throw new ArgumentException($"{typeof(T).Name} no es un artefacto de la preparación conocido.")
    };

    /// <summary>El JSON del artefacto, con salto de línea final.</summary>
    public static string Serialize<T>(T artifact) where T : IPreparationArtifact =>
        JsonSerializer.Serialize(artifact, Json) + "\n";

    /// <summary>
    /// Escribe el artefacto en <paramref name="reportsPath"/> (creando la carpeta). Primero a un temporal y luego lo mueve, para
    /// que un lector concurrente no vea un fichero a medias. Devuelve el aviso si no se pudo, o null.
    /// </summary>
    public static string? TryWrite<T>(string reportsPath, T artifact) where T : IPreparationArtifact
    {
        var fileName = FileNameOf<T>();
        if (string.IsNullOrWhiteSpace(reportsPath))
            return $"⚠️ No hay carpeta de informes donde escribir '{fileName}'.";

        var path = Path.Combine(reportsPath, fileName);
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(reportsPath);
            File.WriteAllText(temp, Serialize(artifact));
            File.Move(temp, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            return $"⚠️ No se pudo escribir '{path}': {ex.Message}";
        }
    }

    /// <summary>
    /// Lee el artefacto de <paramref name="reportsPath"/>. Si el fichero no está, no se puede leer o no es un artefacto de este
    /// formato devuelve null y deja el motivo en <paramref name="warning"/>; quien llama decide si recalcular.
    /// </summary>
    public static T? TryRead<T>(string reportsPath, out string? warning) where T : class, IPreparationArtifact
    {
        warning = null;
        var fileName = FileNameOf<T>();
        if (string.IsNullOrWhiteSpace(reportsPath))
        {
            warning = $"⚠️ No hay carpeta de informes de donde leer '{fileName}'.";
            return null;
        }

        var path = Path.Combine(reportsPath, fileName);
        if (!File.Exists(path))
        {
            warning = $"⚠️ No existe '{path}'.";
            return null;
        }

        try
        {
            var artifact = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
            if (artifact == null)
            {
                warning = $"⚠️ '{path}' está vacío.";
                return null;
            }
            if (artifact.SchemaVersion != SchemaVersionOf<T>())
            {
                warning = $"⚠️ '{path}' tiene la versión de formato {artifact.SchemaVersion}, no la {SchemaVersionOf<T>()} que entiende esta aplicación.";
                return null;
            }
            return artifact;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            warning = $"⚠️ No se pudo leer '{path}': {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// True si el artefacto se calculó sobre el grafo que tiene ahora la huella <paramref name="currentFingerprint"/>. Si
    /// cualquiera de las dos falta no se puede saber y devuelve false: la regeneración recalcula en lugar de fiarse.
    /// </summary>
    public static bool MatchesGraph(IPreparationArtifact? artifact, string? currentFingerprint) =>
        artifact?.GraphFingerprint is { Length: > 0 } stored
        && !string.IsNullOrEmpty(currentFingerprint)
        && string.Equals(stored, currentFingerprint, StringComparison.Ordinal);

    private static int SchemaVersionOf<T>() where T : IPreparationArtifact => typeof(T) switch
    {
        var t when t == typeof(RepositoryInventory) => new RepositoryInventory().SchemaVersion,
        var t when t == typeof(DomainMap) => new DomainMap().SchemaVersion,
        var t when t == typeof(CoveragePlan) => new CoveragePlan().SchemaVersion,
        _ => new TimeBudgetPlan().SchemaVersion
    };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Un temporal que queda atrás no cambia el resultado.
        }
    }
}
