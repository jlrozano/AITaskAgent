using System.Text.Json;

namespace CodebaseAnalysisWeb.Workspace;

/// <summary>Lo que la web recuerda entre arranques.</summary>
public sealed record WorkspaceSettings(string? LastRepositoryPath);

/// <summary>
/// Fichero JSON con el último repositorio abierto. Por defecto en
/// <c>%LOCALAPPDATA%/CodebaseAnalysisWeb/settings.json</c>: es por usuario, sobrevive a recompilar y a
/// republicar (la carpeta del ejecutable se vacía o puede ser de solo lectura) y no se versiona con el
/// repositorio analizado. <c>CodebaseAnalysis:Web:SettingsPath</c> lo cambia (los tests lo usan).
/// </summary>
public sealed class WorkspaceSettingsStore(IConfiguration configuration, ILogger<WorkspaceSettingsStore> logger)
{
    public const string SettingsPathKey = "CodebaseAnalysis:Web:SettingsPath";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _lock = new();

    public string FilePath { get; } = ResolvePath(configuration);

    public static string ResolvePath(IConfiguration configuration)
    {
        var configured = configuration[SettingsPathKey];
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "CodebaseAnalysisWeb",
            "settings.json");
    }

    /// <summary>Los ajustes guardados; vacíos si no hay fichero o no se puede leer (con aviso en el log).</summary>
    public WorkspaceSettings Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(FilePath))
                    return new WorkspaceSettings(null);

                return JsonSerializer.Deserialize<WorkspaceSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new WorkspaceSettings(null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                logger.LogWarning("No se pudieron leer los ajustes de la web en {Path}: {Message}", FilePath, ex.Message);
                return new WorkspaceSettings(null);
            }
        }
    }

    /// <summary>Guarda los ajustes de forma atómica. Un fallo se registra y no se propaga: recordar es una comodidad.</summary>
    public void Save(WorkspaceSettings settings)
    {
        lock (_lock)
        {
            var tmp = FilePath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("No se pudieron guardar los ajustes de la web en {Path}: {Message}", FilePath, ex.Message);
            }
            finally
            {
                try
                {
                    if (File.Exists(tmp))
                        File.Delete(tmp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
