using Newtonsoft.Json.Linq;

namespace BRMS.Engine.Models;

/// <summary>
/// Referencia a una regla con su configuración opcional.
/// </summary>
public record RuleReference
{
    /// <summary>
    /// Nombre de la regla a aplicar.
    /// </summary>
    public string Name { get; init; } = null!;

    /// <summary>
    /// Configuración JSON opcional para la regla.
    /// </summary>
    public JObject? Configuration { get; init; }
}
