using System.ComponentModel;
using BRMS.Core.Attributes;
using BRMS.Core.Core;
using BRMS.Core.Extensions;
using BRMS.Core.Models;
using BRMS.StdRules.Constants;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;


namespace BRMS.StdRules.Rules.Normalizers;


[RuleName("DefaultValue")]
[Description(ResourcesKeys.Desc_DefaultValueNormalizer_Description)]
[SupportedTypes(RuleInputType.String)]
public class DefaultValueNormalizer : Normalizer
{
    [Description(ResourcesKeys.Desc_DefaultValueNormalizer_DefaultValue_Description)]
    public required string DefaultValue { get; init; }

    internal DefaultValueNormalizer() { }

    protected override Task<NormalizerResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("**Iniciando ejecución del DefaultValueNormalizer** - El normalizador está comenzando la asignación de valores por defecto");

            try
            {
                ArgumentNullException.ThrowIfNull(context);

                Logger.LogDebug("**Procesando campo con DefaultValueNormalizer** - Evaluando si el campo necesita un valor por defecto");

                IEnumerable<(JToken Token, string Path)> tokensToNormalize = GetTokensToNormalize(context);
                bool hasAnyChanges = false;

                foreach ((JToken? token, string? path) in tokensToNormalize)
                {
                    string? value = token?.ToObject<string>();

                    if (string.IsNullOrWhiteSpace(value))
                    {
                        context.NewValue!.SetValueWithType(path, DefaultValue);
                        hasAnyChanges = true;
                        Logger.LogInformation("Normalización DefaultValue completada para {Path}: '{OriginalValue}' -> '{DefaultValue}'", path, value ?? "null", DefaultValue);
                    }
                    else
                    {
                        Logger.LogDebug("No se requieren cambios para {Path}, valor no está vacío", path);
                    }
                }

                Logger.LogInformation("**Ejecución del DefaultValueNormalizer completada exitosamente** - La asignación de valor por defecto finalizó correctamente");
                return Task.FromResult(new NormalizerResult(this, context, hasChanges: hasAnyChanges));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "**Error en la ejecución del DefaultValueNormalizer** - Ocurrió un problema durante la asignación del valor por defecto");
                throw;
            }
        }
    }
}
