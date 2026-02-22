using BRMS.Core.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NJsonSchema;

namespace BRMS.Core.Models;



/// <summary>
/// Descripción de una regla del sistema BRMS que incluye metadatos y configuración
/// </summary>
public class RuleDescription
{
    /// <summary>
    /// Tipos de entrada soportados por la regla
    /// </summary>
    public IReadOnlyList<RuleInputType> InputTypes { get; init; } = [RuleInputType.Any];

    /// <summary>
    /// Identificador de la regla
    /// </summary>
    public string Id { get; init; } = "";

    /// <summary>
    /// Descripción de la funcionalidad de la regla. Admite formato markdown
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Esquema JSON de los parámetros de configuración de la regla
    /// </summary>
    public JsonSchema? Parameters { get; set; }

    /// <summary>
    /// Ejemplo de uso de la regla en formato JSON
    /// </summary>
    public JObject? Example { get; set; }

    /// <summary>
    /// Tipo de la clase que implementa la regla
    /// </summary>
    [JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public Type Type { get; init; } = typeof(IRule);
    //public string RuleType => Type is IValidator ? "Validator" : (Type is INormalizer ? "Normalizer" : "Transformation");
}
