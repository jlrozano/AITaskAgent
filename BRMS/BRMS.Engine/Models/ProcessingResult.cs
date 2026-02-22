using BRMS.Core.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;


namespace BRMS.Engine.Models;

/// <summary>
/// Resultado del procesamiento de datos JSON.
/// </summary>
public record ProcessingResult
{
    /// <summary>
    /// Indica si el procesamiento fue exitoso.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// JSON procesado y normalizado.
    /// </summary>
    public JObject ProcessedJson { get; init; } = [];

    /// <summary>
    /// Resultados de normalización aplicados.
    /// </summary>
    public List<INormalizerResult> NormalizationResults { get; init; } = [];
    /// <summary>
    /// Resultados de validación aplicados.
    /// </summary>
    public List<IRuleResult> RuleResults { get; init; } = [];
    /// <summary>
    /// 
    /// </summary>
    public string[] ProcessingErrors { get; set; } = [];
    /// <summary>
    /// 
    /// </summary>
    [JsonIgnore]
    public Exception? Error { get; set; }

}
