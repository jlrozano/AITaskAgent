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
/// Normalizador que elimina espacios en blanco al inicio y final de la cadena de texto.
/// </summary>
[RuleName("TrimSpaces")]
[Description(ResourcesKeys.Desc_TrimSpacesNormalizer_Description)]
[SupportedTypes(RuleInputType.String)]
public class TrimSpacesNormalizer : Normalizer
{
    internal TrimSpacesNormalizer() { }

    protected override Task<NormalizerResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("**Iniciando ejecución del TrimSpacesNormalizer** - El normalizador está comenzando la eliminación de espacios en blanco");

            try
            {
                Logger.LogDebug("**Procesando campo con TrimSpacesNormalizer** - Eliminando espacios en blanco al inicio y final del campo");

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
                        string trimmedValue = stringValue.Trim();
                        bool hasChanges = !string.Equals(stringValue, trimmedValue, StringComparison.Ordinal);

                        if (hasChanges)
                        {
                            context.NewValue?.SetValueWithType(path, trimmedValue);
                            Logger.LogInformation("Normalización TrimSpaces completada para {Path}: '{OriginalValue}' -> '{TrimmedValue}'", path, stringValue, trimmedValue);
                            hasAnyChanges = true;
                        }
                        else
                        {
                            Logger.LogDebug("No se requieren cambios para {Path}, valor ya está recortado", path);
                        }
                    }
                    else
                    {
                        Logger.LogDebug("Valor no es string para {Path}, no se requiere normalización", path);
                    }
                }

                Logger.LogInformation("**Ejecución del TrimSpacesNormalizer completada exitosamente** - La eliminación de espacios en blanco finalizó sin errores");
                return Task.FromResult(new NormalizerResult(this, context, hasChanges: hasAnyChanges));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "**Error en la ejecución del TrimSpacesNormalizer** - Ocurrió un problema durante la eliminación de espacios en blanco");
                throw;
            }
        }
    }
}
