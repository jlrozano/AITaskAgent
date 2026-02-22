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
/// Normalizador que convierte cadenas vacías o que contienen solo espacios en blanco en valores null.
/// </summary>
[RuleName("NullIfEmpty")]
[Description(ResourcesKeys.Desc_NullIfEmptyNormalizer_Description)]
[SupportedTypes(RuleInputType.String)]
public class NullIfEmptyNormalizer : Normalizer
{
    internal NullIfEmptyNormalizer() { }

    protected override Task<NormalizerResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("**Iniciando ejecución del NullIfEmptyNormalizer** - El normalizador está comenzando la conversión de cadenas vacías a null");

            try
            {
                Logger.LogDebug("**Procesando campo con NullIfEmptyNormalizer** - Evaluando si la cadena está vacía para convertirla a null");

                IEnumerable<(JToken Token, string Path)> tokensToNormalize = GetTokensToNormalize(context);
                bool hasAnyChanges = false;

                foreach ((JToken? token, string? path) in tokensToNormalize)
                {
                    string? value = token?.ToObject<string>();

                    if (value is string stringValue)
                    {
                        string? result = string.IsNullOrWhiteSpace(stringValue) ? null : stringValue;
                        bool hasChanges = !string.Equals(stringValue, result, StringComparison.Ordinal);

                        if (hasChanges)
                        {
                            context.NewValue?.SetValueWithType(path, result);
                            Logger.LogInformation("Normalización NullIfEmpty completada para {Path}: '{OriginalValue}' -> null", path, stringValue);
                            hasAnyChanges = true;
                        }
                        else
                        {
                            Logger.LogDebug("No se requieren cambios para {Path}, valor no está vacío", path);
                        }
                    }
                    else
                    {
                        Logger.LogDebug("Valor no es string para {Path}, no se requiere normalización", path);
                    }
                }

                Logger.LogInformation("**Ejecución del NullIfEmptyNormalizer completada exitosamente** - La conversión de cadenas vacías a null finalizó sin errores");
                return Task.FromResult(new NormalizerResult(this, context, hasChanges: hasAnyChanges));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "**Error en la ejecución del NullIfEmptyNormalizer** - Ocurrió un problema durante la conversión de cadenas vacías a null");
                throw;
            }
        }
    }
}
