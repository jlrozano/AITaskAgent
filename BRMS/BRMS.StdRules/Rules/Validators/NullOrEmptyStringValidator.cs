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
/// Validador que verifica que el valor no sea null ni una cadena vacía.
/// </summary>
[RuleName("NullOrEmptyString")]
[Description(ResourcesKeys.Desc_NullOrEmptyStringValidator_Description)]
[SupportedTypes(RuleInputType.String)]
public class NullOrEmptyStringValidator : Validator
{
    internal NullOrEmptyStringValidator() { }

    protected override Task<IRuleResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("**Iniciando ejecución del NullOrEmptyStringValidator** - El validador está comenzando la verificación de cadenas nulas o vacías");

            try
            {
                ArgumentNullException.ThrowIfNull(context);

                Logger.LogDebug("**Procesando campo con NullOrEmptyStringValidator** - Verificando si el campo es nulo o está vacío");

                IEnumerable<(JToken Token, string Path)> tokensToValidate = GetTokensToValidate(context);
                var errors = new List<string>();

                foreach ((JToken? token, string? path) in tokensToValidate)
                {
                    string? value = token?.ToObject<string>();

                    if (string.IsNullOrWhiteSpace(value))
                    {
                        string errorMessage = ErrorMessage ?? "El valor no debe ser nulo o vacío";
                        Logger.LogInformation("**Validación fallida en NullOrEmptyStringValidator** - El campo {Path} está vacío o es nulo", path);
                        errors.Add($"{path}: {errorMessage}");
                    }
                    else
                    {
                        Logger.LogDebug("Validación NullOrEmptyString exitosa para {Path}: valor tiene contenido válido", path);
                    }
                }

                IRuleResult result = errors.Count > 0
                    ? new RuleResult(this, context, string.Join("; ", errors))
                    : new RuleResult(this, context);

                Logger.LogInformation("**Ejecución del NullOrEmptyStringValidator completada exitosamente** - La validación de cadenas nulas o vacías finalizó sin errores");
                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "**Error en la ejecución del NullOrEmptyStringValidator** - Ocurrió un problema durante la validación de cadenas nulas o vacías");
                throw;
            }
        }
    }


}
