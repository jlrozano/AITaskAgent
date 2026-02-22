using Newtonsoft.Json.Linq;

namespace BRMS.Core.Attributes;

/// <summary>
/// Atributo abstracto que permite a las reglas implementar validaciones personalizadas
/// que no pueden expresarse en JSON Schema.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public abstract class CustomValidationAttribute : Attribute
{
    /// <summary>
    /// Valida la configuración de una regla después de que ha pasado la validación del esquema JSON.
    /// </summary>
    /// <param name="configuration">La configuración a validar</param>
    /// <returns>Lista de errores de validación encontrados. Lista vacía si la validación es exitosa.</returns>
    public abstract IEnumerable<string> ValidateConfiguration(JObject configuration);
}
