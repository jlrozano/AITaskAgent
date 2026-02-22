namespace BRMS.Engine.Models;

/// <summary>
/// Reglas de procesamiento que definen qué normalizadores y validadores aplicar.
/// </summary>
public record ProcessingRules
{
    /// <summary>
    /// Normalizadores a aplicar en orden.
    /// </summary>
    public RuleReference[]? Normalizers { get; init; }

    /// <summary>
    /// Validadores a aplicar en orden.
    /// </summary>
    public RuleReference[]? Validators { get; init; }
}
