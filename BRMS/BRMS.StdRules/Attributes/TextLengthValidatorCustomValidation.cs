using BRMS.Core.Attributes;
using Newtonsoft.Json.Linq;

namespace BRMS.StdRules.Attributes;

/// <summary>
/// Validación personalizada para TextLengthValidator que verifica que
/// minLength sea menor o igual que maxLength.
/// </summary>
public sealed class TextLengthValidatorCustomValidation : CustomValidationAttribute
{
    public override IEnumerable<string> ValidateConfiguration(JObject configuration)
    {
        var errors = new List<string>();
        int? minLength = configuration["minLength"]?.Value<int>();
        int? maxLength = configuration["maxLength"]?.Value<int>();

        if (minLength.HasValue && maxLength.HasValue && minLength.Value > maxLength.Value)
        {
            errors.Add($"La longitud mínima ({minLength}) no puede ser mayor que la máxima ({maxLength})");
        }

        return errors;
    }
}
