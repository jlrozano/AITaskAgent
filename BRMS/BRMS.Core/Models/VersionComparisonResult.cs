namespace BRMS.Core.Models;

/// <summary>
/// Resultado de la comparación de versiones de plugins
/// </summary>
public class VersionComparisonResult
{
    /// <summary>
    /// Indica si el plugin está actualmente cargado
    /// </summary>
    public bool IsLoaded { get; set; }

    /// <summary>
    /// Indica si se requiere una actualización
    /// </summary>
    public bool UpdateRequired { get; set; }

    /// <summary>
    /// Versión actualmente cargada (null si no está cargado)
    /// </summary>
    public string? CurrentVersion { get; set; }

    /// <summary>
    /// Versión solicitada
    /// </summary>
    public string RequestedVersion { get; set; } = string.Empty;

    /// <summary>
    /// Descripción del resultado de la comparación
    /// </summary>
    public string ComparisonResult { get; set; } = string.Empty;
}
