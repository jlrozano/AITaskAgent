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
/// Validador que verifica que el número de elementos en una colección esté dentro del rango especificado.
/// </summary>
[RuleName("Count")]
[Description(ResourcesKeys.Desc_CountValidator_Description)]
[SupportedTypes(RuleInputType.Any)]
public class CountValidator : Validator
{
    [Description(ResourcesKeys.Desc_CountValidator_MaxCount_Description)]
    public int MaxCount { get; init; }

    internal CountValidator() { }

    protected override Task<IRuleResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("**Iniciando ejecución del CountValidator** - El validador está comenzando la verificación del conteo de caracteres");

            try
            {
                ArgumentNullException.ThrowIfNull(context);

                Logger.LogDebug("**Procesando campo con CountValidator** - Verificando la longitud del campo contra los límites establecidos");

                IEnumerable<(JToken Token, string Path)> tokensToValidate = GetTokensToValidate(context);
                var errors = new List<string>();

                foreach ((JToken? token, string? path) in tokensToValidate)
                {
                    ICollection<object>? value = token?.ToObject<ICollection<object>>();

                    if (value != null && value.Count > MaxCount)
                    {
                        string errorMessage = ErrorMessage ?? $"El número de elementos supera el máximo permitido ({MaxCount})";
                        Logger.LogInformation("Validación Count falló para {Path}: {Count} elementos > {MaxCount} (máximo)", path, value.Count, MaxCount);
                        errors.Add($"{path}: {errorMessage}");
                    }
                    else if (value != null)
                    {
                        Logger.LogDebug("Validación Count exitosa para {Path}: {Count} elementos <= {MaxCount} (máximo)", path, value.Count, MaxCount);
                    }
                    else
                    {
                        Logger.LogDebug("Valor es null para {Path}, validación Count exitosa", path);
                    }
                }

                IRuleResult result = errors.Count > 0
                    ? new RuleResult(this, context, string.Join("; ", errors))
                    : new RuleResult(this, context);

                Logger.LogInformation("**Ejecución del CountValidator completada exitosamente** - La validación del conteo de caracteres finalizó sin errores");
                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "**Error en la ejecución del CountValidator** - Ocurrió un problema durante la validación del conteo de caracteres");
                throw;
            }
        }
    }


}
