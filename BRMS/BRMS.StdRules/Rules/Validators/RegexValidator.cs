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
/// Validador que verifica que un valor cumpla con un patrón regex específico.
/// </summary>

[RuleName("Regex")]
[Description(ResourcesKeys.Desc_RegexValidator_Description)]
[SupportedTypes(RuleInputType.String)]
public class RegexValidator : Validator
{
    // [Description(DescriptionKeys.RegexValidator_Pattern)]
    public required string Pattern { get; init; }

    /// <summary>
    /// Indica si se permite valor null. Si es false (por defecto), un valor null fallará la validación.
    /// </summary>
    [Description(ResourcesKeys.Desc_Validator_AllowNull_Description)]
    public bool AllowNull { get; init; } = false;

    internal RegexValidator() { }

    protected override Task<IRuleResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("**Iniciando ejecución del RegexValidator** - El validador está comenzando la verificación de expresión regular");

            try
            {
                ArgumentNullException.ThrowIfNull(context);

                Logger.LogDebug("**Procesando campo con RegexValidator** - Validando que el valor coincida con el patrón de expresión regular");

                IEnumerable<(JToken Token, string Path)> tokensToValidate = GetTokensToValidate(context);
                var errors = new List<string>();

                foreach ((JToken? token, string? path) in tokensToValidate)
                {
                    if (token == null || token.Type == JTokenType.Null)
                    {
                        if (!AllowNull)
                        {
                            string errorMessage = ErrorMessage ?? "El valor no puede ser null";
                            Logger.LogInformation("RegexValidator falló para {Path}: valor null no permitido (AllowNull=false)", path);
                            errors.Add($"{path}: {errorMessage}");
                        }
                        else
                        {
                            Logger.LogDebug("Valor null aceptado por RegexValidator (AllowNull=true) para {Path}", path);
                        }
                    }
                    else
                    {
                        string? value = token.ToObject<string>();

                        if (value == null || !System.Text.RegularExpressions.Regex.IsMatch(value, Pattern))
                        {
                            string errorMessage = ErrorMessage ?? $"El valor no coincide con el patrón {Pattern}";
                            Logger.LogInformation("**Validación del RegexValidator falló** - El valor '{Value}' en {Path} no coincide con el patrón de expresión regular", value, path);
                            errors.Add($"{path}: {errorMessage}");
                        }
                        else
                        {
                            Logger.LogDebug("**Validación exitosa** - El valor en {Path} coincide con el patrón", path);
                        }
                    }
                }

                IRuleResult result;
                if (errors.Count > 0)
                {
                    result = new RuleResult(this, context, string.Join("; ", errors));
                }
                else
                {
                    Logger.LogDebug("**Ejecución del RegexValidator completada exitosamente** - La validación de expresión regular finalizó sin errores");
                    result = new RuleResult(this, context);
                }

                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "**Error en la ejecución del RegexValidator** - Ocurrió un problema durante la validación de expresión regular");
                throw;
            }
        }
    }
}
