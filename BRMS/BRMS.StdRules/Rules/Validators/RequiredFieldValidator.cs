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
/// Validador que verifica que un campo obligatorio no sea nulo ni vacío.
/// </summary>

[RuleName("RequiredField")]
[Description(ResourcesKeys.Desc_RequiredFieldValidator_Description)]
[SupportedTypes(RuleInputType.String)]
public class RequiredFieldValidator : Validator
{
    internal RequiredFieldValidator() { }

    protected override Task<IRuleResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("**Iniciando ejecución del RequiredFieldValidator** - El validador está comenzando la verificación de campos requeridos");

            try
            {
                ArgumentNullException.ThrowIfNull(context);

                Logger.LogDebug("**Procesando campo con RequiredFieldValidator** - Validando que el campo requerido tenga un valor");

                IEnumerable<(JToken Token, string Path)> tokensToValidate = GetTokensToValidate(context);
                var errors = new List<string>();

                foreach ((JToken? token, string? path) in tokensToValidate)
                {
                    string? value = token?.ToObject<string>();

                    if (string.IsNullOrWhiteSpace(value))
                    {
                        string errorMessage = ErrorMessage ?? "Campo obligatorio vacío o nulo";
                        Logger.LogInformation("Validación RequiredField falló para {Path}: campo está vacío o es null", path);
                        errors.Add($"{path}: {errorMessage}");
                    }
                    else
                    {
                        Logger.LogDebug("Validación RequiredField exitosa para {Path}: campo tiene valor válido", path);
                    }
                }

                IRuleResult result = errors.Count > 0 ? new RuleResult(this, context, string.Join("; ", errors)) : new RuleResult(this, context);
                Logger.LogInformation("**Ejecución del RequiredFieldValidator completada exitosamente** - La validación de campo requerido finalizó sin errores");
                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "**Error en la ejecución del RequiredFieldValidator** - Ocurrió un problema durante la validación del campo requerido");
                throw;
            }
        }
    }
}
