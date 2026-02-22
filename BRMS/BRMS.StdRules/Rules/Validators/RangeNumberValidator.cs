using System.ComponentModel;
using BRMS.Core.Abstractions;
using BRMS.Core.Attributes;
using BRMS.Core.Core;
using BRMS.Core.Models;
using BRMS.StdRules.Constants;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BRMS.StdRules.Rules.Validators;

/// <summary>
/// Validador para asegurar que un valor entero esté dentro de un rango especificado.
/// </summary>

[RuleName("RangeNumber")]
[Description(ResourcesKeys.Desc_RangeNumberValidator_Description)]
[SupportedTypes(RuleInputType.Integer)]
public class RangeNumberValidator : Validator
{
    [Description(ResourcesKeys.Desc_RangeNumberValidator_MinValue_Description)]
    [SampleValue(-10)]
    public long Min { get; init; } = long.MinValue;

    [Description(ResourcesKeys.Desc_RangeNumberValidator_MaxValue_Description)]
    [SampleValue(10)]
    public long Max { get; init; } = long.MaxValue;

    internal RangeNumberValidator() { }

    protected override Task<IRuleResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("**Iniciando ejecución del RangeNumberValidator** - El validador está comenzando la verificación del rango numérico");

            try
            {
                ArgumentNullException.ThrowIfNull(context);

                Logger.LogDebug("**Procesando campo con RangeNumberValidator** - Validando que el número esté en el rango permitido");

                IEnumerable<(JToken Token, string Path)> tokensToValidate = GetTokensToValidate(context);
                var errors = new List<string>();

                foreach ((JToken? token, string? path) in tokensToValidate)
                {
                    if (token == null || token.Type == JTokenType.Null)
                    {
                        Logger.LogDebug("Valor es null para {Path}, validación RangeNumber exitosa", path);
                        continue; // OK si es nulo
                    }

                    if (!long.TryParse(token.ToString(), out long value))
                    {
                        Logger.LogInformation("Validación RangeNumber falló para {Path}: valor no es número válido - {Value}", path, token.ToString());
                        errors.Add($"{path}: El valor no es un número entero válido.");
                        continue;
                    }

                    if (value < Min)
                    {
                        Logger.LogInformation("Validación RangeNumber falló para {Path}: {Value} < {Min} (mínimo)", path, value, Min);
                        errors.Add($"{path}: El valor {value} es menor que el mínimo permitido {Min}.");
                        continue;
                    }

                    if (value > Max)
                    {
                        Logger.LogInformation("Validación RangeNumber falló para {Path}: {Value} > {Max} (máximo)", path, value, Max);
                        errors.Add($"{path}: El valor {value} es mayor que el máximo permitido {Max}.");
                        continue;
                    }
                }

                IRuleResult result = errors.Count > 0
                    ? new RuleResult(this, context, string.Join("; ", errors))
                    : new RuleResult(this, context);

                if (errors.Count == 0)
                {
                    Logger.LogDebug("**Ejecución del RangeNumberValidator completada exitosamente** - La validación del rango numérico finalizó sin errores");
                }

                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "**Error en la ejecución del RangeNumberValidator** - Ocurrió un problema durante la validación del rango numérico");
                throw;
            }
        }
    }
}
