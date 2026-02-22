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
/// Validador que verifica que el valor entero no sea null ni igual a -99.
/// </summary>
[RuleName("NullOrMinus99Int")]
[Description(ResourcesKeys.Desc_NullOrMinus99IntValidator_Description)]
[SupportedTypes(RuleInputType.Number)]
public class NullOrMinus99IntValidator : Validator
{
    internal NullOrMinus99IntValidator() { }

    protected override Task<IRuleResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("**Iniciando ejecución del NullOrMinus99IntValidator** - El validador está comenzando la verificación de valores nulos o -99");

            try
            {
                ArgumentNullException.ThrowIfNull(context);

                Logger.LogDebug("**Procesando campo con NullOrMinus99IntValidator** - Verificando si el valor es nulo o igual a -99");

                IEnumerable<(JToken Token, string Path)> tokensToValidate = GetTokensToValidate(context);
                var errors = new List<string>();

                foreach ((JToken? token, string? path) in tokensToValidate)
                {
                    int? value = token?.ToObject<int?>();

                    if (value is not null and not (-99))
                    {
                        string errorMessage = ErrorMessage ?? "El valor debe ser nulo o igual a -99";
                        Logger.LogInformation("**Validación del NullOrMinus99IntValidator falló para {Path}** - El valor debe ser nulo o igual a -99", path);
                        errors.Add($"{path}: {errorMessage}");
                    }
                }

                IRuleResult result = errors.Count > 0
                    ? new RuleResult(this, context, string.Join("; ", errors))
                    : new RuleResult(this, context);

                if (errors.Count == 0)
                {
                    Logger.LogInformation("**Ejecución del NullOrMinus99IntValidator completada exitosamente** - La validación de valores nulos o -99 finalizó sin errores");
                }

                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "**Error en la ejecución del NullOrMinus99IntValidator** - Ocurrió un problema durante la validación de valores nulos o -99");
                throw;
            }
        }
    }


}
