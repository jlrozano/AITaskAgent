using System.Text.RegularExpressions;
using BRMS.Core.Attributes;
using Newtonsoft.Json.Linq;

namespace BRMS.StdRules.Attributes;

/// <summary>
/// Validación personalizada para RegexValidator que verifica que
/// el patrón regex sea válido.
/// </summary>
public sealed class RegexValidatorCustomValidation : CustomValidationAttribute
{
    public override IEnumerable<string> ValidateConfiguration(JObject configuration)
    {
        var errors = new List<string>();
        string? pattern = configuration["pattern"]?.Value<string>();

        if (!string.IsNullOrEmpty(pattern))
        {
            try
            {
                // Intentar crear la expresión regular para validar su sintaxis
                _ = new Regex(pattern);
            }
            catch (ArgumentException ex)
            {
                errors.Add($"El patrón regex no es válido: {ex.Message}");
            }
        }

        return errors;
    }
}
