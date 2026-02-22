using System.ComponentModel;
using BRMS.Core.Abstractions;
using BRMS.Core.Attributes;
using BRMS.Core.Core;
using BRMS.Core.Extensions;
using BRMS.Core.Models;
using BRMS.StdRules.Constants;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BRMS.StdRules.Rules.Validators;

/// <summary>
/// Validador que comprueba que un correo electrónico tenga un formato válido.
/// Acepta valores nulos o vacíos; use RequiredFieldValidator para obligatoriedad.
/// </summary>
[RuleName("Email")]
[Description(ResourcesKeys.Desc_EmailValidator_Description)]
[SupportedTypes(RuleInputType.String)]
public class EmailValidator : Validator
{
    /// <summary>
    /// Si es true, aplica validación estricta basada en regex razonable; si es false, usa comprobación heurística básica.
    /// </summary>
    [Description(ResourcesKeys.Desc_EmailValidator_Strict_Description)]
    public bool Strict { get; init; } = true;

    /// <summary>
    /// Indica si se permite valor null. Si es false (por defecto), un valor null fallará la validación.
    /// </summary>
    [Description(ResourcesKeys.Desc_Validator_AllowNull_Description)]
    public bool AllowNull { get; init; } = false;

    internal EmailValidator() { }

    protected override Task<IRuleResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("**Iniciando ejecución del EmailValidator** - El validador está comenzando la verificación del correo electrónico");

            try
            {
                ArgumentNullException.ThrowIfNull(context);

                Logger.LogDebug("**Procesando campo con EmailValidator** - Validando que el correo tenga un formato correcto");

                IEnumerable<(JToken Token, string Path)> tokensToValidate = GetTokensToValidate(context);
                var errors = new List<string>();

                foreach ((JToken? token, string? path) in tokensToValidate)
                {
                    string? raw = token?.ToObject<string>();

                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        if (!AllowNull)
                        {
                            string errorMessage = ErrorMessage ?? "El correo electrónico no puede ser null o vacío";
                            Logger.LogInformation("EmailValidator falló para {Path}: valor null/vacío no permitido (AllowNull=false)", path);
                            errors.Add($"{path}: {errorMessage}");
                        }
                        else
                        {
                            Logger.LogDebug("Valor email nulo/vacío aceptado por EmailValidator (AllowNull=true) para {Path}", path);
                        }
                        continue;
                    }

                    bool isValid;
                    string? normalized = NormalizationExtensions.NormalizeEmail(raw);
                    if (Strict)
                    {
                        isValid = normalized is not null;
                    }
                    else
                    {
                        string candidate = raw.Trim();
                        int at = candidate.IndexOf('@');
                        isValid = at > 0 && at < candidate.Length - 1 && candidate.LastIndexOf('.') > at + 1;
                    }

                    if (!isValid)
                    {
                        string msg = ErrorMessage ?? "Correo electrónico inválido";
                        Logger.LogInformation("EmailValidator falló para {Path}: '{Raw}' no es válido", path, raw);
                        errors.Add($"{path}: {msg}");
                    }
                    else
                    {
                        Logger.LogDebug("EmailValidator validó correctamente {Path}: '{Raw}'", path, raw);
                    }
                }

                IRuleResult result = errors.Count > 0
                    ? new RuleResult(this, context, string.Join("; ", errors))
                    : new RuleResult(this, context);

                if (errors.Count == 0)
                {
                    Logger.LogInformation("**Ejecución del EmailValidator completada exitosamente** - La validación del correo electrónico finalizó sin errores");
                }

                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "**Error en la ejecución del EmailValidator** - Ocurrió un problema durante la validación del correo electrónico");
                throw;
            }
        }
    }
}
