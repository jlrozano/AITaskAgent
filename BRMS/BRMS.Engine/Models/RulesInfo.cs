using BRMS.Core.Models;

namespace BRMS.Engine.Models;

/// <summary>
/// Información sobre las reglas disponibles en el sistema.
/// </summary>
public record RulesInfo
{
    /// <summary>
    /// Número total de reglas registradas.
    /// </summary>
    public int TotalRules { get; init; }

    /// <summary>
    /// Nombres de validadores disponibles.
    /// </summary>
    public RuleDescription[] Validators { get; init; } = [];

    /// <summary>
    /// Nombres de normalizadores disponibles.
    /// </summary>
    public RuleDescription[] Normalizers { get; init; } = [];

    /// <summary>
    /// Número de plugins cargados exitosamente.
    /// </summary>
    public int LoadedPlugins { get; init; }

    /// <summary>
    /// Errores de carga de plugins.
    /// </summary>
    public string[] LoadErrors { get; init; } = [];
}
