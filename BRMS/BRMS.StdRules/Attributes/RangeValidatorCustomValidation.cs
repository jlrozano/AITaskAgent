using BRMS.Core.Attributes;
using Newtonsoft.Json.Linq;

namespace BRMS.StdRules.Attributes;

/// <summary>
/// Validación personalizada para RangeValidator que verifica que min sea menor o igual que max
/// según el tipo de dato especificado.
/// </summary>
public sealed class RangeValidatorCustomValidation : CustomValidationAttribute
{
    public override IEnumerable<string> ValidateConfiguration(JObject configuration)
    {
        var errors = new List<string>();
        string? dataType = configuration["dataType"]?.ToString();
        JToken? min = configuration["min"];
        JToken? max = configuration["max"];

        // Si alguno es null, no validamos (ya lo hará el esquema JSON)
        if (dataType == null || min == null || max == null)
        {
            return errors;
        }

        switch (dataType)
        {
            case "Number":
                if (min.Type is JTokenType.Float or JTokenType.Integer)
                {
                    decimal minValue = min.Value<decimal>();
                    decimal maxValue = max.Value<decimal>();

                    if (minValue > maxValue)
                    {
                        errors.Add($"El valor mínimo ({minValue}) no puede ser mayor que el máximo ({maxValue})");
                    }
                }
                break;

            case "Date":
                if (DateTime.TryParse(min.ToString(), out DateTime minDate) &&
                    DateTime.TryParse(max.ToString(), out DateTime maxDate))
                {
                    if (minDate > maxDate)
                    {
                        errors.Add($"La fecha mínima ({minDate:yyyy-MM-dd}) no puede ser posterior a la máxima ({maxDate:yyyy-MM-dd})");
                    }
                }
                break;
        }

        return errors;
    }
}
