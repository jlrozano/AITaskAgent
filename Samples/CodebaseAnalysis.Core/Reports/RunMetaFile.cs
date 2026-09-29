using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Reports;

/// <summary>
/// <c>reports/_run_meta.json</c>: el consumo LLM (tokens de entrada y salida, llamadas y coste, en total y por paso) y el
/// estado de la última ejecución que escribió en la carpeta, sea un análisis o una regeneración. Va en un fichero y no en
/// el <c>analysis-meta</c> del resumen ejecutivo porque es de la ejecución y no de un informe: una regeneración sin síntesis
/// no reescribe el <c>00</c>, y la síntesis no puede contar su propia llamada antes de escribirlo. No es un informe del
/// catálogo, así que ni se lista ni se copia a <c>backups/</c>. En la web, tras una regeneración, <c>kind</c> es
/// <c>regeneration</c> y <c>startedAt</c> y <c>usage</c> son de la ejecución entera, desde el análisis.
/// </summary>
public static class RunMetaFile
{
    public const string FileName = "_run_meta.json";

    public const string KindAnalysis = "analysis";
    public const string KindRegeneration = "regeneration";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>El contenido del fichero, con las claves en camelCase.</summary>
    public static Dictionary<string, object?> Build(
        string kind, string? runId, string status, DateTimeOffset startedAt, DateTimeOffset finishedAt, LlmUsageSummary usage) => new()
    {
        ["kind"] = kind,
        ["runId"] = runId,
        ["status"] = status,
        ["startedAt"] = startedAt.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        ["finishedAt"] = finishedAt.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        ["durationMs"] = (long)Math.Round((finishedAt - startedAt).TotalMilliseconds),
        ["app"] = AppVersion.Current,
        ["usage"] = usage.ToData()
    };

    /// <summary>
    /// Escribe el fichero en <paramref name="reportsPath"/>. Devuelve el aviso si no se pudo escribir, sin lanzar: perder el
    /// resumen de consumo no debe cambiar el resultado de la ejecución.
    /// </summary>
    public static string? TryWrite(string reportsPath, Dictionary<string, object?> meta)
    {
        if (string.IsNullOrWhiteSpace(reportsPath))
            return null;

        var path = Path.Combine(reportsPath, FileName);
        try
        {
            Directory.CreateDirectory(reportsPath);
            File.WriteAllText(path, JsonSerializer.Serialize(meta, Json) + "\n");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"⚠️ No se pudo escribir '{path}': {ex.Message}";
        }
    }
}
