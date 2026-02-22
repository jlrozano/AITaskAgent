using System.ComponentModel;
using BRMS.Core.Abstractions;
using BRMS.Core.Attributes;
using BRMS.Core.Core;
using BRMS.Core.Extensions;
using BRMS.Core.Models;
using BRMS.StdRules.Constants;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using PhoneNumbers;

namespace BRMS.StdRules.Rules.Validators;

/// <summary>
/// Validador que comprueba que un número de teléfono sea válido.
/// Acepta valores nulos o vacíos; use RequiredFieldValidator si el campo es obligatorio.
/// </summary>
[RuleName("Phone")]
[Description(ResourcesKeys.Desc_PhoneValidator_Description)]
[SupportedTypes(RuleInputType.String)]
public class PhoneValidator : Validator
{
    /// <summary>
    /// Código ISO de la región por defecto (p.ej., ES, US). Se usa para el parseo si el número no incluye prefijo internacional.
    /// </summary>
    [Description(ResourcesKeys.Desc_PhoneValidator_RegionIso_Description)]
    public string? RegionIso { get; init; } = "ES";

    /// <summary>
    /// Si es true, requiere que el número sea válido según libphonenumber; si es false, acepta sanitización básica.
    /// </summary>
    [Description(ResourcesKeys.Desc_PhoneValidator_Strict_Description)]
    public bool Strict { get; init; } = true;

    /// <summary>
    /// Indica si se permite valor null. Si es false (por defecto), un valor null fallará la validación.
    /// </summary>
    [Description(ResourcesKeys.Desc_Validator_AllowNull_Description)]
    public bool AllowNull { get; init; } = false;

    internal PhoneValidator() { }

    protected override Task<IRuleResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("**Iniciando ejecución del PhoneValidator** - El validador está comenzando la verificación del número de teléfono");

            try
            {
                ArgumentNullException.ThrowIfNull(context);

                Logger.LogDebug("**Procesando campo con PhoneValidator** - Validando que el número de teléfono sea posible y válido");

                IEnumerable<(JToken Token, string Path)> tokensToValidate = GetTokensToValidate(context);
                var errors = new List<string>();
                var util = PhoneNumberUtil.GetInstance();
                string region = string.IsNullOrWhiteSpace(RegionIso) ? "ES" : RegionIso!.ToUpperInvariant();

                foreach ((JToken? token, string? path) in tokensToValidate)
                {
                    string? raw = token?.ToObject<string>();

                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        if (!AllowNull)
                        {
                            string errorMessage = ErrorMessage ?? "El número de teléfono no puede ser null o vacío";
                            Logger.LogInformation("PhoneValidator falló para {Path}: valor null/vacío no permitido (AllowNull=false)", path);
                            errors.Add($"{path}: {errorMessage}");
                        }
                        else
                        {
                            Logger.LogDebug("Valor teléfono nulo/vacío aceptado por PhoneValidator (AllowNull=true) para {Path}", path);
                        }
                        continue;
                    }

                    bool isValid = false;

                    try
                    {
                        PhoneNumber number = util.Parse(raw, region);
                        isValid = util.IsPossibleNumber(number) && util.IsValidNumber(number);
                    }
                    catch (NumberParseException)
                    {
                        if (!Strict)
                        {
                            string? sanitized = NormalizationExtensions.NormalizePhone(raw, region);
                            isValid = !string.IsNullOrWhiteSpace(sanitized);
                        }
                        else
                        {
                            isValid = false;
                        }
                    }

                    if (!isValid)
                    {
                        string msg = ErrorMessage ?? "Número de teléfono inválido";
                        Logger.LogInformation("PhoneValidator falló para {Path}: '{Raw}' no es válido", path, raw);
                        errors.Add($"{path}: {msg}");
                    }
                }

                IRuleResult result = errors.Count > 0
                    ? new RuleResult(this, context, string.Join("; ", errors))
                    : new RuleResult(this, context);

                if (errors.Count == 0)
                {
                    Logger.LogInformation("**Ejecución del PhoneValidator completada exitosamente** - La validación del número de teléfono finalizó sin errores");
                }

                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "**Error en la ejecución del PhoneValidator** - Ocurrió un problema durante la validación del número de teléfono");
                throw;
            }
        }
    }
}
