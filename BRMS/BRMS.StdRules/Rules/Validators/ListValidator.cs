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
/// Validador que verifica que el valor esté (o no esté) en una lista de valores permitidos/prohibidos.
/// </summary>

[RuleName("List")]
[Description(ResourcesKeys.Desc_ListValidator_Description)]
[SupportedTypes(RuleInputType.Any)]
public class ListValidator : Validator
{
    [Description(ResourcesKeys.Desc_ListValidator_AllowedValues_Description)]
    public List<string>? AllowedValues { get; init; }

    [Description(ResourcesKeys.Desc_ListValidator_ForbiddenValues_Description)]
    public List<object>? ForbiddenValues { get; init; }

    /// <summary>
    /// Indica si se permite valor null. Si es false (por defecto), un valor null fallará la validación.
    /// </summary>
    [Description(ResourcesKeys.Desc_Validator_AllowNull_Description)]
    public bool AllowNull { get; init; } = false;

    internal ListValidator() { }

    protected override Task<IRuleResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("**Iniciando ejecución del ListValidator** - El validador está comenzando la verificación de valores permitidos/prohibidos");

            try
            {
                ArgumentNullException.ThrowIfNull(context);

                Logger.LogDebug("**Procesando campo con ListValidator** - Verificando si el valor está en las listas permitidas o prohibidas");

                IEnumerable<(JToken Token, string Path)> tokensToValidate = GetTokensToValidate(context);
                var errors = new List<string>();

                foreach ((JToken? token, string? path) in tokensToValidate)
                {
                    object? value = token?.ToObject<object>();

                    if (value != null)
                    {
                        if (AllowedValues != null && !AllowedValues.Any(av => Equals(av, value)))
                        {
                            string errorMessage = ErrorMessage ?? $"Valor '{value}' no está en la lista permitida";
                            Logger.LogInformation("**Validación fallida en ListValidator** - El valor en {Path} no está permitido", path);
                            errors.Add($"{path}: {errorMessage}");
                        }
                        else if (ForbiddenValues != null && ForbiddenValues.Any(fv => Equals(fv, value)))
                        {
                            string errorMessage = ErrorMessage ?? $"Valor '{value}' está en la lista prohibida";
                            Logger.LogInformation("**Validación fallida en ListValidator** - El valor en {Path} está en la lista de valores prohibidos", path);
                            errors.Add($"{path}: {errorMessage}");
                        }
                        else
                        {
                            Logger.LogDebug("**Validación exitosa en ListValidator** - El valor en {Path} es válido", path);
                        }
                    }
                    else
                    {
                        if (!AllowNull)
                        {
                            string errorMessage = ErrorMessage ?? "El valor no puede ser null";
                            Logger.LogInformation("**Validación fallida en ListValidator** - El valor null en {Path} no está permitido (AllowNull=false)", path);
                            errors.Add($"{path}: {errorMessage}");
                        }
                        else
                        {
                            Logger.LogDebug("**Validación del ListValidator exitosa: el valor en {Path} es nulo (AllowNull=true)**", path);
                        }
                    }
                }

                IRuleResult result = errors.Count > 0
                    ? new RuleResult(this, context, string.Join("; ", errors))
                    : new RuleResult(this, context);

                if (errors.Count == 0)
                {
                    Logger.LogInformation("**Ejecución del ListValidator completada exitosamente** - La validación de listas finalizó sin errores");
                }

                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "**Error en la ejecución del ListValidator** - Ocurrió un problema durante la validación de listas");
                throw;
            }
        }
    }


}
