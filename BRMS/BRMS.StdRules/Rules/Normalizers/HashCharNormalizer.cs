using System.ComponentModel;
using BRMS.Core.Attributes;
using BRMS.Core.Core;
using BRMS.Core.Extensions;
using BRMS.Core.Models;
using BRMS.StdRules.Constants;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;


namespace BRMS.StdRules.Rules.Normalizers;

/// <summary>
/// Normalizador que aplica reglas específicas para el carácter '#'.
/// </summary>

[RuleName("HashChar")]
[Description(ResourcesKeys.Desc_HashCharNormalizer_Description)]
[SupportedTypes(RuleInputType.String)]
public class HashCharNormalizer : Normalizer
{
    internal HashCharNormalizer() { }

    public bool EmptyOrWitheSpaceAsNull { get; set; } = true;
    protected override Task<NormalizerResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("**Iniciando ejecución del HashCharNormalizer** - El normalizador está comenzando el reemplazo de caracteres especiales");

            try
            {
                ArgumentNullException.ThrowIfNull(context);

                Logger.LogDebug("**Procesando campo con HashCharNormalizer** - Reemplazando caracteres especiales con símbolos hash");

                IEnumerable<(JToken Token, string Path)> tokensToNormalize = GetTokensToNormalize(context);
                bool hasAnyChanges = false;

                foreach ((JToken? token, string? path) in tokensToNormalize)
                {
                    string? value = token?.ToObject<string>();

                    if (value != null)
                    {
                        string? normalizedValue = value.Replace("#", "");
                        if (EmptyOrWitheSpaceAsNull && string.IsNullOrWhiteSpace(normalizedValue))
                        {
                            normalizedValue = null;
                        }
                        context.NewValue!.SetValueWithType(path, normalizedValue);

                        bool hasChanges = !string.Equals(value, normalizedValue, StringComparison.Ordinal);

                        if (hasChanges)
                        {
                            Logger.LogInformation("Normalización HashChar completada para {Path}: '{OriginalValue}' -> '{NewValue}'", path, value, normalizedValue);
                            hasAnyChanges = true;
                        }
                        else
                        {
                            Logger.LogDebug("No se requieren cambios para {Path}, valor no contiene caracteres '#'", path);
                        }
                    }
                    else
                    {
                        Logger.LogDebug("Valor es null para {Path}, no se requiere normalización", path);
                    }
                }

                Logger.LogInformation("**Ejecución del HashCharNormalizer completada exitosamente** - El reemplazo de caracteres especiales finalizó sin errores");
                return Task.FromResult(new NormalizerResult(this, context, hasChanges: hasAnyChanges));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "**Error en la ejecución del HashCharNormalizer** - Ocurrió un problema durante el reemplazo de caracteres especiales");
                throw;
            }
        }
    }
}
