using BRMS.Core.Attributes;
using Newtonsoft.Json.Linq;

namespace BRMS.StdRules.Attributes;

/// <summary>
/// Validación personalizada para CountValidator que verifica que
/// minCount sea menor o igual que maxCount.
/// </summary>
public sealed class CountValidatorCustomValidation : CustomValidationAttribute
{
    public override IEnumerable<string> ValidateConfiguration(JObject configuration)
    {
        var errors = new List<string>();
        int? minCount = configuration["minCount"]?.Value<int>();
        int? maxCount = configuration["maxCount"]?.Value<int>();

        if (minCount.HasValue && maxCount.HasValue && minCount.Value > maxCount.Value)
        {
            errors.Add($"El conteo mínimo ({minCount}) no puede ser mayor que el máximo ({maxCount})");
        }

        return errors;
    }
}
