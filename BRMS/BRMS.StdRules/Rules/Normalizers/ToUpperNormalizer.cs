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
/// Normalizador que convierte cadenas de texto a mayúsculas.
/// </summary>

[RuleName("ToUpper")]
[Description(ResourcesKeys.Desc_ToUpperNormalizer_Description)]
[SupportedTypes(RuleInputType.String)]
public class ToUpperNormalizer : Normalizer
{
    internal ToUpperNormalizer() { }

    protected override Task<NormalizerResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("**Iniciando ejecución del ToUpperNormalizer** - El normalizador está comenzando la conversión de texto a mayúsculas");

            try
            {
                ArgumentNullException.ThrowIfNull(context);

                Logger.LogDebug("**Procesando campo con ToUpperNormalizer** - Convirtiendo el texto del campo a mayúsculas");

                IEnumerable<(JToken Token, string Path)> tokensToNormalize = GetTokensToNormalize(context);
                bool hasAnyChanges = false;

                foreach ((JToken? token, string? path) in tokensToNormalize)
                {
                    if (token == null || token.Type == JTokenType.Null)
                    {
                        Logger.LogDebug("Valor es null para {Path}, no se requiere normalización", path);
                        continue;
                    }

                    string? value = token.ToObject<string>();

                    if (value is string stringValue)
                    {
                        string upperValue = stringValue.ToUpperInvariant();
                        bool hasChanges = !string.Equals(stringValue, upperValue, StringComparison.Ordinal);

                        if (hasChanges)
                        {
                            context.NewValue?.SetValueWithType(path, upperValue);
                            Logger.LogInformation("Normalización ToUpper completada para {Path}: '{OriginalValue}' -> '{NewValue}'", path, stringValue, upperValue);
                            hasAnyChanges = true;
                        }
                        else
                        {
                            Logger.LogDebug("No se requieren cambios para {Path}, valor ya está en mayúsculas", path);
                        }
                    }
                    else
                    {
                        Logger.LogDebug("Valor no es string para {Path}, no se requiere normalización", path);
                    }
                }

                Logger.LogInformation("**Ejecución del ToUpperNormalizer completada exitosamente** - La conversión a mayúsculas finalizó sin errores");
                return Task.FromResult(new NormalizerResult(this, context, hasChanges: hasAnyChanges));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "**Error en la ejecución del ToUpperNormalizer** - Ocurrió un problema durante la conversión a mayúsculas");
                throw;
            }
        }
    }
}
