using BRMS.Core.Attributes;
using Newtonsoft.Json.Linq;

namespace BRMS.StdRules.Attributes;

/// <summary>
/// Validación personalizada para ListValidator que verifica:
/// - Que no haya elementos comunes entre las listas contains y notContains
/// - Que al menos una de las listas tenga elementos
/// </summary>
public sealed class ListValidatorCustomValidation : CustomValidationAttribute
{
    public override IEnumerable<string> ValidateConfiguration(JObject configuration)
    {
        var errors = new List<string>();
        string[]? contains = configuration["contains"]?.ToObject<string[]>();
        string[]? notContains = configuration["notContains"]?.ToObject<string[]>();

        // Verificar que al menos una lista tiene elementos
        if ((contains == null || contains.Length == 0) &&
            (notContains == null || notContains.Length == 0))
        {
            errors.Add("Al menos una de las listas (contains o notContains) debe tener elementos");
            return errors;
        }

        // Si ambas listas tienen elementos, verificar que no haya elementos comunes
        if (contains != null && notContains != null &&
            contains.Length != 0 && notContains.Length != 0)
        {
            var commonElements = contains.Intersect(notContains, StringComparer.OrdinalIgnoreCase).ToList();
            if (commonElements.Count != 0)
            {
                errors.Add($"Los siguientes elementos aparecen tanto en contains como en notContains: {string.Join(", ", commonElements)}");
            }
        }

        return errors;
    }
}
