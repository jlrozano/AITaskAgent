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

[RuleName("ToLower")]
[Description(ResourcesKeys.Desc_ToLowerNormalizer_Description)]
[SupportedTypes(RuleInputType.String)]
public class ToLowerNormalizer : Normalizer
{
    internal ToLowerNormalizer() { }

    protected override Task<NormalizerResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("**Iniciando ejecución del ToLowerNormalizer** - El normalizador está comenzando la conversión de texto a minúsculas");

            try
            {
                ArgumentNullException.ThrowIfNull(context);

                Logger.LogDebug("**Procesando campo con ToLowerNormalizer** - Convirtiendo el texto del campo a minúsculas");

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
                        string lowerValue = stringValue.ToLowerInvariant();
                        bool hasChanges = !string.Equals(stringValue, lowerValue, StringComparison.Ordinal);

                        if (hasChanges)
                        {
                            context.NewValue?.SetValueWithType(path, lowerValue);
                            Logger.LogInformation("Normalización ToLower completada para {Path}: '{OriginalValue}' -> '{NewValue}'", path, stringValue, lowerValue);
                            hasAnyChanges = true;
                        }
                        else
                        {
                            Logger.LogDebug("No se requieren cambios para {Path}, valor ya está en minúsculas", path);
                        }
                    }
                    else
                    {
                        Logger.LogDebug("Valor no es string para {Path}, no se requiere normalización", path);
                    }
                }

                Logger.LogInformation("**Ejecución del ToLowerNormalizer completada exitosamente** - La conversión a minúsculas finalizó sin errores");
                return Task.FromResult(new NormalizerResult(this, context, hasChanges: hasAnyChanges));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "**Error en la ejecución del ToLowerNormalizer** - Ocurrió un problema durante la conversión a minúsculas");
                throw;
            }
        }
    }
}
