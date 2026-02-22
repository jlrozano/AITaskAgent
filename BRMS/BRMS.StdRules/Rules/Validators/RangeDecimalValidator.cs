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
/// Validador para asegurar que un valor decimal esté dentro de un rango especificado.
/// </summary>

[RuleName("RangeDecimal")]
[Description(ResourcesKeys.Desc_RangeDecimalValidator_Description)]
[SupportedTypes(RuleInputType.Number)]
public class RangeDecimalValidator : Validator
{
    [Description(ResourcesKeys.Desc_RangeDecimalValidator_MinValue_Description)]
    public decimal Min { get; init; } = decimal.MinValue;

    [Description(ResourcesKeys.Desc_RangeDecimalValidator_MaxValue_Description)]
    public decimal Max { get; init; } = decimal.MaxValue;

    internal RangeDecimalValidator() { }

    protected override Task<IRuleResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("**Iniciando ejecución del RangeDecimalValidator** - El validador está comenzando la verificación del rango decimal");

            try
            {
                ArgumentNullException.ThrowIfNull(context);

                Logger.LogDebug("**Procesando campo con RangeDecimalValidator** - Validando que el valor decimal esté en el rango permitido");

                IEnumerable<(JToken Token, string Path)> tokensToValidate = GetTokensToValidate(context);
                var errors = new List<string>();

                foreach ((JToken? token, string? path) in tokensToValidate)
                {
                    if (token == null || token.Type == JTokenType.Null)
                    {
                        Logger.LogDebug("Valor es null para {Path}, validación RangeDecimal exitosa", path);
                        continue;
                    }

                    if (!decimal.TryParse(token.ToString(), out decimal value))
                    {
                        Logger.LogInformation("Validación RangeDecimal falló para {Path}: valor no es decimal válido - {Value}", path, token.ToString());
                        errors.Add($"{path}: El valor no es un número decimal válido.");
                        continue;
                    }

                    if (value < Min)
                    {
                        Logger.LogInformation("Validación RangeDecimal falló para {Path}: {Value} < {Min} (mínimo)", path, value, Min);
                        errors.Add($"{path}: El valor {value} es menor que el mínimo permitido {Min}.");
                    }
                    else if (value > Max)
                    {
                        Logger.LogInformation("Validación RangeDecimal falló para {Path}: {Value} > {Max} (máximo)", path, value, Max);
                        errors.Add($"{path}: El valor {value} es mayor que el máximo permitido {Max}.");
                    }
                    else
                    {
                        Logger.LogDebug("Validación RangeDecimal exitosa para {Path}: {Value} está en rango [{Min}, {Max}]", path, value, Min, Max);
                    }
                }

                IRuleResult result = errors.Count > 0
                    ? new RuleResult(this, context, string.Join("; ", errors))
                    : new RuleResult(this, context);

                Logger.LogInformation("**Ejecución del RangeDecimalValidator completada exitosamente** - La validación del rango decimal finalizó sin errores");
                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "**Error en la ejecución del RangeDecimalValidator** - Ocurrió un problema durante la validación del rango decimal");
                throw;
            }
        }
    }
}
